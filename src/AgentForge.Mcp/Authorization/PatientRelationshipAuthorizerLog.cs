using Microsoft.Extensions.Logging;

namespace AgentForge.Mcp.Authorization;

/// <summary>
/// Source-generated log messages for <see cref="PatientRelationshipAuthorizer"/> (CA1848). Diagnostic stream:
/// the clinician, the exception type and OpenEMR's HTTP status when there was one, never the patient or the
/// exception message. Every caller audits the resulting refusal through <see cref="AccessAuditLog.RecordRefusal"/>
/// under the same correlation id, and that line is the one that names the patient (CONVENTIONS.md §7).
/// </summary>
internal static partial class PatientRelationshipAuthorizerLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not resolve the care relationship between clinician {ClinicianIdentity} and the requested " +
                  "patient - failing closed and refusing access (FR-AUTH-2): {ExceptionType}, HTTP status {StatusCode}")]
    public static partial void RelationshipUnresolvable(ILogger logger, string clinicianIdentity, Type exceptionType, int? statusCode);
}
