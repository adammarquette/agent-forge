namespace AgentForge.Api.Health;

/// <summary>
/// Self-hosted observability backend configuration (Epic 9 - docker-compose Prometheus/Loki/Tempo/Grafana),
/// bound via the Options pattern. Distinct from <see cref="Integration.OpenEmr.OpenEmrOptions"/>/
/// <see cref="Llm.LlmProviderOptions"/> in one way: this dependency is optional infra, not a
/// product-blocking one, so nothing here is <c>required</c> or validated on start.
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Observability";

    /// <summary>
    /// The self-hosted Prometheus instance's <c>/-/healthy</c> endpoint, if deployed
    /// (<c>observability/docker-compose.yml</c>). Optional - <see cref="ObservabilityHealthCheck"/>
    /// reports <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded"/>
    /// rather than an unconditional pass when it's unset (NFR-REL-2). Set it only where Prometheus
    /// actually runs: once set, a URL that is unreachable or silent fails readiness (503) like any
    /// other dependency - promptly, within <see cref="ReadinessOptions.ProbeTimeout"/>, rather than
    /// after <see cref="HttpClient"/>'s 100-second default.
    /// </summary>
    public string? PrometheusHealthUrl { get; init; }

    /// <summary>
    /// Full OTLP/HTTP logs endpoint of a self-hosted Loki instance (Epic 107), e.g.
    /// <c>http://agentforge-loki:3100/otlp/v1/logs</c>. When set, the sidecar adds an
    /// OTLP log exporter alongside the console one (<c>Program.cs</c>) so structured logs are queryable
    /// in Grafana. Optional and fail-open: unset -> console-only logging, and a wrong/unreachable value
    /// never blocks the app (the exporter batches and drops on failure). Include the full
    /// <c>/otlp/v1/logs</c> path - it is used as-is, not appended to.
    /// </summary>
    public string? LokiOtlpEndpoint { get; init; }

    /// <summary>
    /// Full OTLP/HTTP traces endpoint of a self-hosted trace backend (Tempo), e.g.
    /// <c>http://tempo:4318/v1/traces</c>. When set to an absolute http(s) URL the sidecar adds an OTLP
    /// trace exporter, so spans are queryable in Grafana; used as-is, not appended to. Optional and
    /// fail-open: unset means no exporter, a malformed value means no exporter and one startup warning that
    /// does not echo it, and an unreachable one never blocks the app. Every span passes
    /// <c>SpanPhiScrubber</c> first.
    /// </summary>
    public string? TraceOtlpEndpoint { get; init; }

    /// <summary>
    /// Also writes every span to stdout. For local debugging only, and off by default: console spans reach
    /// whatever collects the container's stdout.
    /// </summary>
    public bool TraceConsoleExporter { get; init; }
}
