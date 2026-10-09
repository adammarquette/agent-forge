using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Api.Chat;

public sealed class ChatHubTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 5, 36, TimeSpan.Zero);
    private static readonly ChatPatientScope PatientDefault123 = new("default", "123");

    private readonly IAgentOrchestrator _orchestrator = A.Fake<IAgentOrchestrator>();
    private readonly IConversationStateStore _conversationStore = A.Fake<IConversationStateStore>();
    private readonly IChatMessageOutbox _outbox = A.Fake<IChatMessageOutbox>();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    // The real accessor, not a fake: the signal has to land on the id the turn itself carried,
    // and only the real ambient storage can show that.
    private readonly MutableCorrelationIdAccessor _correlationIdAccessor = new();
    private readonly CapturingLogger<ExpiredSessionSignal> _signalLogger = new();
    private readonly CapturingLogger<ChatHub> _hubLogger = new();
    private readonly IScopedAccessTokenProvider _tokenProvider = A.Fake<IScopedAccessTokenProvider>();
    private readonly IScopedClinicianIdentityAccessor _clinicianIdentity = A.Fake<IScopedClinicianIdentityAccessor>();
    private readonly IConversationTurnBudget _turnBudget = A.Fake<IConversationTurnBudget>();
    private readonly ChatHub _sut;

    public ChatHubTests()
    {
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).Returns(true);
        var coordinator = new ChatSessionCoordinator(
            _orchestrator,
            _conversationStore,
            _outbox,
            _tokenProvider,
            _clinicianIdentity,
            _correlationIdAccessor,
            A.Fake<ILogger<ChatSessionCoordinator>>(),
            A.Fake<ILogger<AgentForge.AccessAudit>>(),
            _turnBudget,
            A.Fake<AgentForge.Data.IDerivedFactStore>());

        _sut = new ChatHub(
            coordinator,
            _timeProvider,
            _correlationIdAccessor,
            new ExpiredSessionSignal(_metrics, _correlationIdAccessor, _signalLogger),
            _hubLogger);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Resume_CalledTwiceAfterConnect_BothCallsUseTheSessionCapturedAtConnectTimeRatherThanRereadingHttpContext()
    {
        // Context.Features returns the connection-establishing request's features on its first
        // access, then a bare request's on every access after that - the shape of a second,
        // unrelated long-poll. A hub that re-read the HttpContext per method would find nothing on
        // the second call, so this pins that the identity is captured once, at connect.
        var session = new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1));
        var connectingRequestFeatures = BuildHttpContextResolvedBySessionMiddleware(session, "session-xyz").Features;
        var laterPollRequestFeatures = BuildHttpContext().Features; // nothing resolved on it

        var hubContext = A.Fake<HubCallerContext>();
        A.CallTo(() => hubContext.Items).Returns(new Dictionary<object, object?>());
        A.CallTo(() => hubContext.ConnectionAborted).Returns(CancellationToken.None);
        A.CallTo(() => hubContext.Features).ReturnsNextFromSequence(connectingRequestFeatures, laterPollRequestFeatures);
        _sut.Context = hubContext;

        await _sut.OnConnectedAsync();
        var first = () => _sut.Resume(5);
        var second = () => _sut.Resume(6);

        await first.Should().NotThrowAsync();
        await second.Should().NotThrowAsync();
        A.CallTo(() => _outbox.GetSince("session-xyz", PatientDefault123, 5)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _outbox.GetSince("session-xyz", PatientDefault123, 6)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task OnConnectedAsync_HttpContextCarriesNoSessionFeature_AuthenticatesFromWhatTheMiddlewareResolved()
    {
        // The long-polling shape: SignalR hands the hub a clone of the establishing request that
        // carries Items but not ISessionFeature, so any read of HttpContext.Session throws
        // InvalidOperationException ("Session has not been configured...") instead of connecting.
        // ASP.NET Core does not support session state in SignalR at all; the hub must not need it.
        var session = new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1));
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(session, "session-xyz"));

        var connect = () => _sut.OnConnectedAsync();
        await connect.Should().NotThrowAsync();
        var act = () => _sut.Resume(5);

        await act.Should().NotThrowAsync();
        A.CallTo(() => _outbox.GetSince("session-xyz", PatientDefault123, 5)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Resume_ConnectedWithNoAuthenticatedSession_ThrowsHubException()
    {
        // The middleware resolved a session id but no patient context - the real "browser hasn't
        // completed the SMART launch yet" shape.
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(session: null, "session-xyz"));

        await _sut.OnConnectedAsync();
        var act = () => _sut.Resume(5);

        await act.Should().ThrowAsync<HubException>().WithMessage("No authenticated session*");
    }

    [Fact]
    public async Task OnConnectedAsync_NothingResolvedTheSessionForThisRequest_FailsClosedAndSaysWhyToTheServer()
    {
        // A composition fault - the middleware not wired, or wired before UseSession(). The hub must
        // not fall back to reading HttpContext.Session, and must not admit the connection either;
        // the clinician sees the ordinary "launch first" refusal, and the operator gets the cause.
        ConnectTo(BuildHttpContext());

        await _sut.OnConnectedAsync();
        var act = () => _sut.Resume(5);

        await act.Should().ThrowAsync<HubException>().WithMessage("No authenticated session*");
        A.CallTo(() => _outbox.GetSince(A<string>._, A<ChatPatientScope>._, A<long>._)).MustNotHaveHappened();
        _hubLogger.Lines.Should().ContainSingle().Which.Should().Contain(nameof(ChatHubSessionMiddleware));
    }

    [Fact]
    public async Task RequestBrief_AuthenticatedConnection_RunsTheTurnAsTheConnectionsOwnClinicianTokenAndPatient()
    {
        // FR-AUTH-2 is enforced below the model on every tool dispatch, against the clinician
        // identity and token the hub hands the turn. Moving where the hub reads them from must not
        // change which ones it hands over.
        var session = new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1));
        await ConnectWithSession(session);
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._, A<IProgress<string>>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        A.CallTo(() => _outbox.Append(A<string>._, A<ChatPatientScope>._, A<string>._, A<string>._))
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBrief();

        A.CallToSet(() => _clinicianIdentity.ClinicianIdentity).To("dr-jones").MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenProvider.Adopt("token-abc", session.ExpiresAt)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _outbox.Append("session-xyz", A<ChatPatientScope>._, A<string>._, A<string>._)).MustHaveHappenedOnceExactly();
    }

    private void ConnectTo(HttpContext connectingRequest)
    {
        var hubContext = A.Fake<HubCallerContext>();
        A.CallTo(() => hubContext.Items).Returns(new Dictionary<object, object?>());
        A.CallTo(() => hubContext.ConnectionAborted).Returns(CancellationToken.None);
        A.CallTo(() => hubContext.ConnectionId).Returns("connection-1");
        A.CallTo(() => hubContext.Features).Returns(connectingRequest.Features);
        _sut.Context = hubContext;
        // RequestBrief/AskFollowUp stream interim status to Clients.Caller.
        _sut.Clients = A.Fake<IHubCallerClients>();
    }

    /// <summary>
    /// The connecting request as the hub receives it once <see cref="ChatHubSessionMiddleware"/> has
    /// run - Items populated, and deliberately no <c>ISessionFeature</c>, which is what a
    /// long-polling hub gets and what every transport must now be able to live with.
    /// </summary>
    /// <remarks>
    /// The page is taken to have been rendered for <paramref name="session"/>'s own patient unless
    /// <paramref name="renderedFor"/> says otherwise - the connecting request presents that page's key.
    /// </remarks>
    private static DefaultHttpContext BuildHttpContextResolvedBySessionMiddleware(
        PatientSessionContext? session, string sessionId, PatientSessionContext? renderedFor = null)
    {
        var httpContext = BuildHttpContext();
        ChatHubSessionItems.Set(httpContext, sessionId, session);
        if ((renderedFor ?? session) is { } page)
        {
            httpContext.Request.QueryString = QueryString.Create(
                PatientContextBinding.QueryParameter, PatientContextBinding.KeyFor(sessionId, page));
        }

        return httpContext;
    }

    /// <summary>
    /// A bare <see cref="DefaultHttpContext"/> does not register itself as
    /// <see cref="IHttpContextFeature"/> on its own <see cref="HttpContext.Features"/> - the
    /// mechanism <c>HubCallerContext.GetHttpContext()</c> relies on - so tests must wire it up
    /// explicitly to get a context back at all.
    /// </summary>
    private static DefaultHttpContext BuildHttpContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));
        return httpContext;
    }

    private sealed class TestHttpContextFeature(HttpContext httpContext) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    [Fact]
    public async Task RequestBrief_SessionTokenExpiredAfterTheConnectionWasEstablished_ThrowsTheReLaunchHubExceptionWithoutRunningATurn()
    {
        // review round 1. The session is cached on Context.Items at connect time, so a
        // connection opened at T0 still holds a live-looking context at T0+61min. Before this, the
        // turn ran, AuthHandler threw below the model, nothing caught it, and SignalR replaced the
        // message with "An unexpected error occurred invoking 'RequestBrief' on the server." - the
        // clinician was told to try again, which is the one thing that cannot work.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddMinutes(5)));
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        var act = () => _sut.RequestBrief();

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Contain(ChatHub.SessionExpiredMessage);
        // An expired session must not bill an LLM round before failing.
        A.CallTo(_orchestrator).MustNotHaveHappened();
    }

    [Fact]
    public async Task AskFollowUp_TokenExpiresMidTurn_TranslatesTheFailureIntoTheSameReLaunchHubException()
    {
        // The narrow case the pre-check cannot cover: live when the turn started, dead by the time
        // a tool call went out. It must reach the clinician as the same refusal, not as a generic
        // transport error.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>>._))
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        var act = () => _sut.AskFollowUp("what changed?");

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Contain(ChatHub.SessionExpiredMessage);
    }

    [Fact]
    public async Task RequestBrief_SessionStillWithinItsLifetime_RunsTheTurn()
    {
        // The other side of the guard: a live session must still reach the orchestrator, so the
        // refusal above is the expiry and not the check itself.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._, A<IProgress<string>>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        A.CallTo(() => _outbox.Append(A<string>._, A<ChatPatientScope>._, A<string>._, A<string>._))
            .Returns(new ChatMessage(1, "brief", "{}"));

        var act = () => _sut.RequestBrief();

        await act.Should().NotThrowAsync();
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._, A<IProgress<string>>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RequestBrief_SessionExpiredBeforeTheTurnOpened_CountsTheRefusalOnTheExpiredSessionSeries()
    {
        // The pre-check is right and stays right - what was missing is that it said nothing
        // to the server. A separate change removed the RelationshipUnresolvable outcome and the
        // "ACCESS AUDIT - REFUSED" row that had been standing in for expiry, both misdescriptions,
        // and nothing replaced them: the one-hour wall had no rate an operator could read.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddMinutes(5)));
        _timeProvider.Advance(TimeSpan.FromMinutes(6));

        var act = () => _sut.RequestBrief();

        await act.Should().ThrowAsync<HubException>();
        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(ExpiredSessionSurface.ChatPreTurn))
            .MustHaveHappenedOnceExactly();
        // Distinguishable from an authorization refusal, or it re-blurs what a separate change separated.
        A.CallTo(() => _metrics.RecordAuthorizationDecision(A<bool>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AskFollowUp_TokenExpiresMidTurn_CountsTheRefusalUnderTheSameCorrelationIdTheTurnCarried()
    {
        // The criterion that is not automatic here: CorrelationIdMiddleware skips hub traffic and
        // defers to ChatSessionCoordinator's per-turn scope, and this refusal is raised outside that
        // scope - before it on the pre-check, after it has been disposed on this path. Reading the
        // ambient accessor at the moment of refusal mints a *fresh* id unless the hub established one
        // first, and a refusal under an id no other line carries joins nothing.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));
        string? idDuringTurn = null;
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>>._))
            .Invokes(() => idDuringTurn = _correlationIdAccessor.CorrelationId)
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        var act = () => _sut.AskFollowUp("what changed?");

        await act.Should().ThrowAsync<HubException>();
        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(ExpiredSessionSurface.ChatTurn))
            .MustHaveHappenedOnceExactly();
        idDuringTurn.Should().NotBeNullOrWhiteSpace();
        _signalLogger.Lines.Should().ContainSingle()
            .Which.Should().Contain($"CorrelationId={idDuringTurn}");
    }

    [Fact]
    public async Task RequestBrief_SessionStillWithinItsLifetime_LeavesTheExpiredSessionSeriesAlone()
    {
        // The other side of the signal: a live turn must move nothing, or the rate measures traffic
        // rather than the wall.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._, A<IProgress<string>>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        A.CallTo(() => _outbox.Append(A<string>._, A<ChatPatientScope>._, A<string>._, A<string>._))
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBrief();

        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(A<string>._)).MustNotHaveHappened();
        _signalLogger.Lines.Should().BeEmpty();
    }

    private async Task ConnectWithSession(PatientSessionContext session)
    {
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(session, "session-xyz"));
        await _sut.OnConnectedAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
    [Fact]
    public async Task AskFollowUp_ConversationTurnLimitReached_TellsTheClinicianWhyInsteadOfAGenericTransportError()
    {
        // SignalR replaces a non-HubException's message with a generic one, so the cap has to be translated here
        // or the clinician sees "That didn't go through" and retries into the same refusal.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));
        A.CallTo(() => _turnBudget.TryConsume("session-xyz")).Returns(false);

        var act = () => _sut.AskFollowUp("one more?");

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Be(ChatHub.TurnLimitMessage);
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task Resume_PageRenderedForThePreviousPatient_RefusesWithThePatientChangedMessageAndReplaysNothing()
    {
        // a separate change (N1). Tab 1 shows pt-A, tab 2 drills down to pt-B on the same cookie, and tab 1's reconnect
        // resolves pt-B's session. Its replay must not pull pt-B's answers under pt-A's banner.
        var renderedFor = new PatientSessionContext("token-abc", "default", "pt-A", "dr-jones", Now.AddHours(1));
        var current = renderedFor with { PatientId = "pt-B" };
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(current, "session-xyz", renderedFor));
        await _sut.OnConnectedAsync();

        var act = () => _sut.Resume(0);

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Be(ChatHub.PatientChangedMessage);
        A.CallTo(() => _outbox.GetSince(A<string>._, A<ChatPatientScope>._, A<long>._)).MustNotHaveHappened();
        _hubLogger.Lines.Should().ContainSingle().Which.Should().NotContain("pt-A").And.NotContain("pt-B");
    }

    [Fact]
    public async Task AskFollowUp_PageRenderedForThePreviousPatient_RefusesWithoutChargingOrRunningATurn()
    {
        // The answer would be about pt-B, shown under pt-A's banner.
        var renderedFor = new PatientSessionContext("token-abc", "default", "pt-A", "dr-jones", Now.AddHours(1));
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(renderedFor with { PatientId = "pt-B" }, "session-xyz", renderedFor));
        await _sut.OnConnectedAsync();

        var act = () => _sut.AskFollowUp("latest potassium?");

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Be(ChatHub.PatientChangedMessage);
        A.CallTo(_orchestrator).MustNotHaveHappened();
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RequestBrief_PageRenderedForTheSamePatientIdOnAnotherSite_Refuses()
    {
        var renderedFor = new PatientSessionContext("token-abc", "site-one", "123", "dr-jones", Now.AddHours(1));
        ConnectTo(BuildHttpContextResolvedBySessionMiddleware(renderedFor with { Site = "site-two" }, "session-xyz", renderedFor));
        await _sut.OnConnectedAsync();

        var act = () => _sut.RequestBrief();

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Be(ChatHub.PatientChangedMessage);
        A.CallTo(_orchestrator).MustNotHaveHappened();
    }

    [Fact]
    public async Task Resume_ConnectionPresentsNoPageKey_FailsClosed()
    {
        // Nothing says which patient the page shows, so nothing may be shown on it.
        var session = new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1));
        var httpContext = BuildHttpContextResolvedBySessionMiddleware(session, "session-xyz");
        httpContext.Request.QueryString = QueryString.Empty;
        ConnectTo(httpContext);
        await _sut.OnConnectedAsync();

        var act = () => _sut.Resume(0);

        (await act.Should().ThrowAsync<HubException>()).And.Message.Should().Be(ChatHub.PatientChangedMessage);
        A.CallTo(() => _outbox.GetSince(A<string>._, A<ChatPatientScope>._, A<long>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Resume_PageRenderedForTheSessionsCurrentPatient_ReplaysThatPatientsOutboxOnly()
    {
        // The other direction: a page that still matches keeps its reconnect-and-replay.
        await ConnectWithSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Now.AddHours(1)));

        var act = () => _sut.Resume(0);

        await act.Should().NotThrowAsync();
        A.CallTo(() => _outbox.GetSince("session-xyz", PatientDefault123, 0)).MustHaveHappenedOnceExactly();
    }
}
