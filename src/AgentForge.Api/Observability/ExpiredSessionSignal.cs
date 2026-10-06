using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>
/// The one server-side record that a SMART session was refused for having aged out: a counter
/// increment on <c>agentforge.expired_session_refusals</c> and one correlation-scoped log line.
/// Every surface in <see cref="ExpiredSessionSurface"/> emits through here rather than instrumenting
/// itself, so the rules below are stated once. A separate change
/// </summary>
/// <remarks>
/// <para>
/// <b>Observation, not behaviour.</b> a separate change settled when a session is refused and what the
/// clinician is told; this adds nothing to either. What it replaces is what correctly took
/// away — a <c>RelationshipUnresolvable</c> outcome and an <c>ACCESS AUDIT … REFUSED</c> row, both
/// of which described expiry as an authorization decision. So this is a <b>series of its own</b>:
/// re-routing expiry onto <c>agentforge.authorization_decisions</c> would restate that falsehood on
/// the panel an operator reads.
/// </para>
/// <para>
/// <b>What may be said about a dead session.</b> The surface, and a correlation id. Not the access
/// token, not the session key, not the expiry instant, not the patient — the session is expired, and
/// the record of its expiry must not become the interesting artefact (<c>REQUIREMENTS.md</c> §13.1 draws the
/// same line for what the clinician is shown). A caller that has a session id hands it over raw and
/// this type derives <see cref="ConversationId"/> from it, so the credential-shaped value dies here
/// rather than at each call site.
/// </para>
/// </remarks>
public sealed class ExpiredSessionSignal(
    IAgentForgeMetrics metrics,
    ICorrelationIdAccessor correlationIdAccessor,
    ILogger<ExpiredSessionSignal> logger)
{
    /// <summary>
    /// Records one expired-session refusal at <paramref name="surface"/>, joining it to the request
    /// or turn it ended via the correlation id a separate change established.
    /// </summary>
    /// <param name="surface">A value from <see cref="ExpiredSessionSurface"/> — it becomes an exported label.</param>
    /// <param name="sessionId">
    /// The server-side session key. Never logged: only its one-way <see cref="ConversationId"/>
    /// derivation is, which is what joins the refusal to the other turns of the same session (UC-2).
    /// Optional so that a surface without one, or one whose store has not minted one yet, records
    /// the refusal rather than throwing on the way to a 401.
    /// </param>
    public void Record(string surface, string? sessionId = null)
    {
        metrics.RecordExpiredSessionRefusal(surface);

        var scopeState = new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationIdAccessor.CorrelationId,
        };

        // Guarded rather than assumed: ConversationId.From throws on a blank id by design, and a
        // telemetry helper that threw on the refusal path would turn a clean 401 into a 500 - the
        // shape of failure a separate change was reported as.
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            scopeState["ConversationId"] = ConversationId.From(sessionId);
        }

        using var scope = logger.BeginScope(scopeState);
        ExpiredSessionSignalLog.SessionExpired(logger, surface);
    }
}
