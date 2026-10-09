using System.Text.Json;
using AgentForge.Agent;
using AgentForge.Agents;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp;
using AgentForge.Verification;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Chat;

/// <summary>
/// The hub-independent core of a chat turn: sets the session's token in scope, runs the
/// orchestrator, persists the resulting conversation state, and appends the answer to the outbox.
/// Kept independent of <see cref="Microsoft.AspNetCore.SignalR.Hub"/> so it is directly
/// unit-testable; <see cref="ChatHub"/> is the thin layer that reads the session and pushes the
/// result to the caller.
/// </summary>
public sealed class ChatSessionCoordinator(
    IAgentOrchestrator orchestrator,
    IConversationStateStore conversationStore,
    IChatMessageOutbox outbox,
    IScopedAccessTokenProvider tokenProvider,
    IScopedClinicianIdentityAccessor clinicianIdentityAccessor,
    ICorrelationIdAccessor correlationIdAccessor,
    ILogger<ChatSessionCoordinator> logger,
    ILogger<AccessAudit> auditLogger,
    IConversationTurnBudget turnBudget,
    IDerivedFactStore? factStore = null)
{
    /// <summary>The access-audit trail's tool name for the chat turn's document-citation read (FR-AUTH-4).</summary>
    public const string CitationAuditToolName = "chat_document_citations";

    /// <summary>Starts the pre-visit brief for <paramref name="session"/> and returns the message appended to the outbox.</summary>
    /// <exception cref="ConversationTurnLimitExceededException">The session has used its LLM turn budget; nothing ran.</exception>
    public async Task<ChatMessage> RequestBriefAsync(
        string sessionId, PatientSessionContext session, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        ChargeTurn(sessionId);
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);
        clinicianIdentityAccessor.ClinicianIdentity = session.ClinicianIdentity;

        var correlationId = correlationIdAccessor.CorrelationId;
        using var scope = BeginCorrelationScope(sessionId, correlationId);
        var result = await orchestrator.StartBriefAsync(session.Site, session.PatientId, cancellationToken, progress).ConfigureAwait(false);

        conversationStore.Save(sessionId, result.State);
        var payload = ToPayload(result, await LoadDocumentCitationsAsync(session, correlationId, cancellationToken).ConfigureAwait(false));
        return outbox.Append(sessionId, ChatPatientScope.From(session), "brief", JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Asks a follow-up for <paramref name="session"/>, resuming the session's saved conversation
    /// state if it belongs to the session's current site and patient, or starting fresh for them if
    /// not - so a patient switch within one browser session never dispatches a tool call for the
    /// previous patient.
    /// </summary>
    /// <exception cref="ConversationTurnLimitExceededException">The session has used its LLM turn budget; nothing ran.</exception>
    public async Task<ChatMessage> AskFollowUpAsync(
        string sessionId, PatientSessionContext session, string question, CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        ChargeTurn(sessionId);
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);
        clinicianIdentityAccessor.ClinicianIdentity = session.ClinicianIdentity;

        var correlationId = correlationIdAccessor.CorrelationId;
        using var scope = BeginCorrelationScope(sessionId, correlationId);
        var state = ResumeOrStart(sessionId, session);
        var result = await orchestrator.AskFollowUpAsync(state, question, cancellationToken, progress).ConfigureAwait(false);

        conversationStore.Save(sessionId, result.State);
        var payload = ToPayload(result, await LoadDocumentCitationsAsync(session, correlationId, cancellationToken).ConfigureAwait(false));
        return outbox.Append(sessionId, ChatPatientScope.From(session), "answer", JsonSerializer.Serialize(payload));
    }

    // The store is keyed by session id, but the session outlives a patient switch (agenda drill-down, a
    // second launch on the same cookie), and every tool call takes its patient from the state. A state for
    // another patient is discarded rather than re-bound: its history holds that patient's tool results.
    private ConversationState ResumeOrStart(string sessionId, PatientSessionContext session)
    {
        var saved = conversationStore.TryGet(sessionId);
        return saved is not null
            && string.Equals(saved.Site, session.Site, StringComparison.Ordinal)
            && string.Equals(saved.PatientId, session.PatientId, StringComparison.Ordinal)
            ? saved
            : ConversationState.Start(session.Site, session.PatientId);
    }

    // Before the token is adopted or the orchestrator called, so a refused turn bills no LLM round.
    private void ChargeTurn(string sessionId)
    {
        if (!turnBudget.TryConsume(sessionId))
        {
            throw new ConversationTurnLimitExceededException();
        }
    }

    /// <summary>
    /// Opens the logging scope every downstream call in this turn (tool dispatch, the LLM call,
    /// verification) inherits, so both ids reach every log line without being threaded as explicit
    /// parameters through each of those layers (FR-OBS-1, CONVENTIONS.md Sec.7).
    /// </summary>
    /// <remarks>
    /// Two ids, because they answer different questions. The correlation id is minted per hub
    /// invocation and scopes one turn; the conversation id is derived from the session and is
    /// stable across every turn of one chat, which is what makes UC-2's multi-turn follow-up
    /// reconstructable rather than a set of unrelated turns. Stable per session, not exclusive to one
    /// patient - a session outlives a patient switch, so it answers "which session", not "which
    /// patient". The session id itself is never logged - see <see cref="ConversationId"/>.
    /// </remarks>
    private IDisposable? BeginCorrelationScope(string sessionId, string correlationId) =>
        logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["ConversationId"] = ConversationId.From(sessionId),
        });

    /// <summary>
    /// Returns every message for <paramref name="sessionId"/> after <paramref name="lastSeenSequence"/> that was
    /// delivered for <paramref name="session"/>'s current site and patient - never one delivered before a patient
    /// switch, which the session id outlives.
    /// </summary>
    public IReadOnlyList<ChatMessage> Resume(string sessionId, PatientSessionContext session, long lastSeenSequence) =>
        outbox.GetSince(sessionId, ChatPatientScope.From(session), lastSeenSequence);

    // The pre-visit brief must surface facts ingested before the visit (UC-9), and the client needs each
    // fact's source-document id + region to open the PDF and highlight it (FR-CITE-2). Read-only; empty when
    // none. Week 2 is optional (Program.cs gates the store on a configured database): with no store wired the
    // Week 1 chat still runs, just without click-to-source document citations.
    // The read sends fact values to the client and no tool call in the turn is guaranteed to have audited it,
    // so it records its own access - before the read, so a failed read is still an audited attempt.
    private async Task<IReadOnlyList<DocumentCitation>> LoadDocumentCitationsAsync(
        PatientSessionContext session, string correlationId, CancellationToken cancellationToken)
    {
        if (factStore is null)
        {
            return [];
        }

        AccessAuditLog.RecordAccess(
            auditLogger, session.ClinicianIdentity, session.PatientId, CitationAuditToolName, correlationId);
        return DerivedFactCitationProjector.Project(
            await factStore.GetByPatientAsync(session.PatientId, cancellationToken).ConfigureAwait(false));
    }

    private static ChatAnswerPayload ToPayload(AgentTurnResult result, IReadOnlyList<DocumentCitation> documentCitations) => new(
        result.Answer,
        [.. result.SafetyFlags.Select(f => new SafetyFlagPayload(f.RuleId, f.Description, [.. f.Sources.Select(s => s.Citation)]))],
        [.. result.SuppressedClaims.Select(c => new SuppressedClaimPayload(c.Line, c.Reason))],
        documentCitations,
        result.IsDeterministicFallback);
}
