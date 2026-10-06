using AgentForge.Api.Health;

namespace AgentForge.Api.Observability;

/// <summary>
/// The trace exporters the sidecar registers, resolved from <see cref="ObservabilityOptions"/> before the
/// host is built. Optional infra, so it fails open: an unset endpoint means no OTLP
/// exporter, and a malformed one means no OTLP exporter plus <see cref="EndpointRejected"/> - never a
/// startup failure.
/// </summary>
/// <param name="OtlpEndpoint">The absolute http(s) OTLP/HTTP traces URL to export to, or <see langword="null"/> for none.</param>
/// <param name="ConsoleExporter">Whether spans are also written to stdout, for local debugging only.</param>
/// <param name="EndpointRejected">An endpoint was configured but is not an absolute http(s) URL, so none is used.</param>
public sealed record TraceExportPlan(Uri? OtlpEndpoint, bool ConsoleExporter, bool EndpointRejected)
{
    /// <summary>Resolves the plan from bound options.</summary>
    public static TraceExportPlan From(ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var raw = options.TraceOtlpEndpoint;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TraceExportPlan(null, options.TraceConsoleExporter, EndpointRejected: false);
        }

        var valid = Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host);
        return valid
            ? new TraceExportPlan(uri, options.TraceConsoleExporter, EndpointRejected: false)
            : new TraceExportPlan(null, options.TraceConsoleExporter, EndpointRejected: true);
    }
}
