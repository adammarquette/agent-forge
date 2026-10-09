using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Chat;

/// <summary>
/// The browser-facing SignalR hub (ARCHITECTURE.md D11, CONVENTIONS.md §12). Every
/// method reads the caller's identity from the server-side session only - no bearer token ever
/// travels over this connection - and delegates the actual turn to
/// <see cref="ChatSessionCoordinator"/>, whose outbox is what makes <see cref="Resume"/> an
/// idempotent recovery path after a reconnect rather than a best-effort one.
/// </summary>
public sealed class ChatHub(
    ChatSessionCoordinator coordinator,
    TimeProvider timeProvider,
    MutableCorrelationIdAccessor correlationIdAccessor,
    ExpiredSessionSignal expiredSession,
    ILogger<ChatHub> logger) : Hub
{
    /// <summary>
    /// Route this hub is mapped at. A constant because the correlation-id middleware has to
    /// recognise hub traffic and leave its per-turn scope to <see cref="ChatSessionCoordinator"/>.
    /// </summary>
    public const string Route = "/hubs/chat";

    /// <summary>
    /// What the clinician is told when the SMART session has aged out. The browser matches on this
    /// text to show the <c>#expired</c> panel (<c>wwwroot/index.html</c>), so it is a contract with
    /// the client, not a log line - SignalR replaces the message of any non-<see cref="HubException"/>
    /// with a generic transport error, which is how the refusal used to be lost.
    /// </summary>
    public const string SessionExpiredMessage =
        "No authenticated session - this SMART session has expired; re-launch AgentForge from the "
        + "patient's chart to continue.";

    /// <summary>
    /// What the clinician is told when the session has used its LLM turn budget. Sent as a
    /// <see cref="HubException"/> for the same reason as <see cref="SessionExpiredMessage"/>, and matched by the
    /// browser (<c>wwwroot/index.html</c>) so it is shown rather than read as a transient failure to retry.
    /// </summary>
    public const string TurnLimitMessage =
        "This session has reached its turn limit - no further AI answers until its budget resets.";

    /// <summary>
    /// What a connection is told when the page it serves was rendered for a different patient than the
    /// session's current one - another tab drilled down, or a second launch on the same cookie. The browser
    /// matches on this text and reloads, so the banner and the chat show the same patient again.
    /// </summary>
    public const string PatientChangedMessage =
        "The patient for this session has changed since this page was loaded - reload to continue with the "
        + "current patient.";

    private const int MaxDeliveryAttempts = 3;
    private const string SessionIdItemKey = "patient-session.id";
    private const string SessionItemKey = "patient-session.context";
    private const string PatientChangedItemKey = "patient-session.page-mismatch";

    /// <summary>
    /// Captures the connection's identity once, from what <see cref="ChatHubSessionMiddleware"/>
    /// resolved into the connecting request's <see cref="HttpContext.Items"/>, and caches it on
    /// <see cref="HubCallerContext.Items"/> for every hub method on this connection.
    /// <para>
    /// Never <c>HttpContext.Session</c>: ASP.NET Core does not support session state in SignalR.
    /// Under long-polling SignalR hands the hub a clone of the connecting request that carries
    /// <c>Items</c> but not <c>ISessionFeature</c>, so reading the session here threw
    /// <see cref="InvalidOperationException"/> on that transport instead of connecting
    /// . <c>Items</c> is on the hub's context under WebSockets, server-sent
    /// events and long-polling alike.
    /// </para>
    /// <para>
    /// The connection is also bound to the patient its page was rendered for: the page presents the key
    /// <c>GET /patient</c> gave it (<see cref="PatientContextBinding"/>), and a connection whose key does not
    /// match the session's current site and patient is admitted but refuses every method with
    /// <see cref="PatientChangedMessage"/>. A reconnect re-resolves the session, which may have switched patient
    /// under a page still showing the previous one.
    /// </para>
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext()
            ?? throw new HubException("No HTTP context available for this connection.");

        if (ChatHubSessionItems.TryGet(httpContext, out var sessionId, out var session))
        {
            Context.Items[SessionIdItemKey] = sessionId;
            if (session is not null)
            {
                var presentedKey = httpContext.Request.Query[PatientContextBinding.QueryParameter].ToString();
                if (PatientContextBinding.Matches(presentedKey, sessionId, session))
                {
                    Context.Items[SessionItemKey] = session;
                }
                else
                {
                    Context.Items[PatientChangedItemKey] = true;
                    ChatHubLog.PagePatientMismatch(logger, Context.ConnectionId);
                }
            }
        }
        else
        {
            // Fails closed: every method on this connection refuses as unauthenticated.
            ChatHubLog.SessionNotResolved(logger, Context.ConnectionId);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the pre-visit brief for the authenticated session's patient (UC-1).</summary>
    public async Task RequestBrief()
    {
        var (sessionId, session) = GetAuthenticatedSessionOrThrow();
        ChatMessage message;
        try
        {
            message = await coordinator.RequestBriefAsync(
                sessionId, session, Context.ConnectionAborted, CreateStatusProgress()).ConfigureAwait(false);
        }
        catch (AccessTokenExpiredException ex)
        {
            expiredSession.Record(ExpiredSessionSurface.ChatTurn, sessionId);
            throw new HubException(SessionExpiredMessage, ex);
        }
        catch (ConversationTurnLimitExceededException ex)
        {
            ChatHubLog.TurnLimitReached(logger, ConversationId.From(sessionId));
            throw new HubException(TurnLimitMessage, ex);
        }

        await DeliverAsync(message).ConfigureAwait(false);
    }

    /// <summary>Asks a follow-up question within the authenticated session (UC-2).</summary>
    public async Task AskFollowUp(string question)
    {
        var (sessionId, session) = GetAuthenticatedSessionOrThrow();
        ChatMessage message;
        try
        {
            message = await coordinator.AskFollowUpAsync(
                sessionId, session, question, Context.ConnectionAborted, CreateStatusProgress()).ConfigureAwait(false);
        }
        catch (AccessTokenExpiredException ex)
        {
            expiredSession.Record(ExpiredSessionSurface.ChatTurn, sessionId);
            throw new HubException(SessionExpiredMessage, ex);
        }
        catch (ConversationTurnLimitExceededException ex)
        {
            ChatHubLog.TurnLimitReached(logger, ConversationId.From(sessionId));
            throw new HubException(TurnLimitMessage, ex);
        }

        await DeliverAsync(message).ConfigureAwait(false);
    }

    // Streams the orchestrator's tool-call status to the caller as interim "ChatStatus" messages while the
    // (verified) answer is assembled - perceived-latency only, never unverified content. Fire-and-forget: a
    // dropped status is harmless, since the answer + Resume outbox remain the durable delivery path.
    private Progress<string> CreateStatusProgress()
    {
        var caller = Clients.Caller;
        var connectionAborted = Context.ConnectionAborted;
        return new Progress<string>(status => _ = caller.SendAsync("ChatStatus", status, connectionAborted));
    }

    /// <summary>
    /// Called by a reconnecting client to replay anything sent while it was disconnected
    /// (CONVENTIONS.md §12 - idempotent resume: safe to call repeatedly with the same
    /// <paramref name="lastSeenSequence"/>, and safe across a new connection id after a drop).
    /// </summary>
    public Task<IReadOnlyList<ChatMessage>> Resume(long lastSeenSequence)
    {
        var (sessionId, session) = GetAuthenticatedSessionOrThrow();
        return Task.FromResult(coordinator.Resume(sessionId, session, lastSeenSequence));
    }

    /// <summary>
    /// Reads the connection's cached session and refuses an aged-out one, establishing this
    /// invocation's correlation id first.
    /// </summary>
    /// <remarks>
    /// The id is minted <em>here</em> rather than left to <see cref="ChatSessionCoordinator"/>'s
    /// lazy read, because both expired-session refusals sit outside that per-turn scope - this one
    /// before it opens, the mid-turn catch after it has been disposed - and an <c>AsyncLocal</c> the
    /// coordinator set would not flow back out to either. Reading the accessor at the moment of
    /// refusal would therefore mint a <em>second</em> id belonging to no other line, which is a
    /// parallel mechanism rather than a separate change. Setting it before the turn instead means the turn
    /// and the refusal that ended it carry the same id. <see cref="Observability.CorrelationIdMiddleware"/>
    /// deliberately leaves hub traffic alone, so nothing upstream has set one.
    /// </remarks>
    private (string SessionId, PatientSessionContext Session) GetAuthenticatedSessionOrThrow()
    {
        correlationIdAccessor.CorrelationId = MutableCorrelationIdAccessor.NewCorrelationId();

        if (Context.Items.ContainsKey(PatientChangedItemKey))
        {
            throw new HubException(PatientChangedMessage);
        }

        if (Context.Items.TryGetValue(SessionItemKey, out var value) && value is PatientSessionContext session)
        {
            var sessionId = (string)Context.Items[SessionIdItemKey]!;

            // Re-checked per method, not once at connect: the session is cached on Context.Items when
            // the connection opens, so a tab left open crosses the token's one-hour lifetime while
            // still holding a live-looking context. Refusing here also means an aged-out session
            // never bills an LLM round before failing.
            if (AccessTokenLifetime.HasExpired(session.ExpiresAt, timeProvider.GetUtcNow()))
            {
                expiredSession.Record(ExpiredSessionSurface.ChatPreTurn, sessionId);
                throw new HubException(SessionExpiredMessage);
            }

            return (sessionId, session);
        }

        // Not counted as an expiry: no session was ever saved here, which is an un-launched browser
        // rather than the one-hour wall, and conflating them would make the wall's rate unreadable.
        throw new HubException("No authenticated session - complete the SMART launch first.");
    }

    /// <summary>
    /// Bounded retry with backoff (CONVENTIONS.md §12); if every attempt fails, the
    /// message is not lost - it already sits in the outbox, so the next <see cref="Resume"/> call
    /// (this reconnect or a later one) recovers it. Logging here is the "reported to observers"
    /// half of "never silently discarded."
    /// </summary>
    private async Task DeliverAsync(ChatMessage message)
    {
        for (var attempt = 1; attempt <= MaxDeliveryAttempts; attempt++)
        {
            try
            {
                await Clients.Caller.SendAsync("ChatMessage", message, Context.ConnectionAborted).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxDeliveryAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), Context.ConnectionAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ChatHubLog.MessageDeliveryFailed(logger, message.Kind, message.Sequence, Context.ConnectionId, ex);
            }
        }
    }
}
