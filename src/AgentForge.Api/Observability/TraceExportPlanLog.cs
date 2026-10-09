using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>
/// Source-generated log message for a rejected <see cref="TraceExportPlan"/> endpoint (CA1848). Names the
/// setting, never its value.
/// </summary>
internal static partial class TraceExportPlanLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Observability:TraceOtlpEndpoint is set but is not an absolute http(s) URL; no trace exporter " +
            "is registered and spans are not exported")]
    public static partial void EndpointRejected(ILogger logger);
}
