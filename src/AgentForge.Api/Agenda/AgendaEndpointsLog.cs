using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Source-generated log messages for <see cref="AgendaEndpoints"/> (CA1848). Diagnostic stream: the clinician
/// only, never the patient - a diagnostic line may not name one, whatever the audit trail carries
/// (CONVENTIONS.md §7).
/// </summary>
internal static partial class AgendaEndpointsLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Clinician {ClinicianIdentity} attempted to select a patient who was not part of their own agenda roster - rejected (FR-AUTH-3)")]
    public static partial void PatientNotInRoster(ILogger logger, string clinicianIdentity);
}
