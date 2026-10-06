using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>
/// Source-generated log message for <see cref="ExpiredSessionSignal"/> (CA1848). The surface and
/// nothing else — never the token, the session key, the expiry instant or the patient
/// (CONVENTIONS.md §7). A separate change
/// </summary>
internal static partial class ExpiredSessionSignalLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SMART session refused at surface={Surface} - its OpenEMR access token has expired and only a " +
            "fresh launch recovers it; this is not an authorization decision")]
    public static partial void SessionExpired(ILogger logger, string surface);
}
