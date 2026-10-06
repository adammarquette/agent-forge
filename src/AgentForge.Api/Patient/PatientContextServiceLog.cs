using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Patient;

/// <summary>
/// Source-generated log messages for <see cref="PatientContextService"/> (CA1848). Diagnostic stream: the FHIR
/// resource type and the exception type only - no patient id, and no exception message, which can echo one
/// from a FHIR path (CONVENTIONS.md §7). The session has one patient, so the correlation id is enough.
/// </summary>
internal static partial class PatientContextServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Patient-context {ResourceType} fetch failed, degrading to unreachable: {ExceptionType}")]
    public static partial void FetchFailed(ILogger logger, string resourceType, Type exceptionType);
}
