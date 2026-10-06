using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Launch;

/// <summary>
/// Source-generated log messages for <see cref="SmartLaunchService"/> (CA1848). Introspection
/// metadata only - never a token, patient identifier, or other PHI (CONVENTIONS.md §7).
/// </summary>
internal static partial class SmartLaunchServiceLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Introspection returned no subject claim for client {ClientId}: active={Active}")]
    public static partial void IntrospectionMissingSubject(ILogger logger, string? clientId, bool active);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Introspection reports the token is not active for client {ClientId}")]
    public static partial void IntrospectionInactive(ILogger logger, string? clientId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "OpenEMR granted client {ClientId} a token without the requested scopes {DroppedScopes}: the " +
                  "client's registration lacks them and OpenEMR drops them silently, so any FHIR read needing one " +
                  "will 401, and a dropped Appointment read makes the FR-AUTH-2 relationship check fail closed. " +
                  "Re-run tools/BootstrapOpenEmr to reconcile the registration")]
    public static partial void RequestedScopesNotGranted(ILogger logger, string? clientId, string droppedScopes);
}
