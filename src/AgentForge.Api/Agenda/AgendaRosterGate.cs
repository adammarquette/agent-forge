using System.Globalization;
using AgentForge.Mcp;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Agenda;

/// <summary>
/// The drill-down authorization check (ARCHITECTURE.md §19.1 step 5): a client selecting a
/// patient from the agenda may only select one this session's own agenda already returned. Kept
/// as a pure function, independent of session/HTTP plumbing, so the one decision that matters here
/// is trivially unit-testable.
/// </summary>
public static class AgendaRosterGate
{
    /// <summary>The audit trail's tool name for a drill-down selection.</summary>
    public const string AuditToolName = "agenda_select_patient";

    /// <summary>The audit trail's reason for a selection outside the clinician's own roster.</summary>
    public const string AuditReason = "not-on-own-agenda-roster (FR-AUTH-3)";

    /// <summary>Whether <paramref name="requestedPatientId"/> was part of <paramref name="rosterPatientIds"/>.</summary>
    public static bool Authorize(IReadOnlySet<string> rosterPatientIds, string requestedPatientId) =>
        rosterPatientIds.Contains(requestedPatientId);

    /// <summary>
    /// <see cref="Authorize"/>, and on a refusal one <see cref="AccessAuditLog.RecordRefusal"/> line naming the
    /// clinician, the requested patient and <paramref name="correlationId"/> (REQUIREMENTS.md §13.1, "Denied access attempt
    /// (FR-AUTH-4)"). The roster size fills the clinic-day count field. A permitted selection writes nothing.
    /// </summary>
    public static bool AuthorizeAndAudit(
        IReadOnlySet<string> rosterPatientIds, string clinicianIdentity, string requestedPatientId,
        string correlationId, ILogger<AccessAudit> auditLogger)
    {
        if (Authorize(rosterPatientIds, requestedPatientId))
        {
            return true;
        }

        AccessAuditLog.RecordRefusal(
            auditLogger,
            clinicianIdentity,
            requestedPatientId,
            AuditToolName,
            AuditReason,
            rosterPatientIds.Count.ToString(CultureInfo.InvariantCulture),
            correlationId);
        return false;
    }
}
