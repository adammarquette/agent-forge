using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Readiness check for the self-hosted observability backend (NFR-HEALTH-1): a real GET against
/// Prometheus's own <c>/-/healthy</c> endpoint when <see cref="ObservabilityOptions.PrometheusHealthUrl"/>
/// is configured, bounded by <see cref="ReadinessOptions.ProbeTimeout"/>.
/// </summary>
/// <remarks>
/// Two outcomes, and the line between them is whether the URL is set. <b>Unconfigured</b> is
/// <see cref="HealthStatus.Degraded"/>: the self-hosted stack is optional infra
/// (<c>observability/docker-compose.yml</c>), so "never contacted" is a distinct, honest state
/// rather than an unconditional pass (NFR-REL-2). <b>Configured and not answering</b> is
/// <see cref="HealthStatus.Unhealthy"/>, exactly like <see cref="OpenEmrHealthCheck"/> and
/// <see cref="LlmProviderHealthCheck"/> - NFR-REL-2 and NFR-HEALTH-1 both name the observability
/// backend among the dependencies <c>/ready</c> must fail for, and `ARCHITECTURE.md` D17 records the
/// ruling that kept it that way. What a separate change changed is the wait, not the verdict: the probe is
/// bounded instead of inheriting the 100-second default that took staging's <c>/ready</c> to 100.33s
/// </remarks>
public sealed class ObservabilityHealthCheck(
    HttpClient httpClient,
    IOptions<ObservabilityOptions> options,
    IOptions<ReadinessOptions> readinessOptions) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var url = options.Value.PrometheusHealthUrl;
        if (string.IsNullOrEmpty(url))
        {
            return HealthCheckResult.Degraded(
                $"{ObservabilityOptions.SectionName}:PrometheusHealthUrl not configured - metrics are emitted but scrape reachability is unverified.");
        }

        var probeTimeout = readinessOptions.Value.ProbeTimeout;

        var outcome = await ReadinessProbe.GetAsync(httpClient, url, probeTimeout, cancellationToken)
            .ConfigureAwait(false);
        using var response = outcome.Response;

        if (response is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Prometheus {outcome.DescribeFailure(probeTimeout)}.", outcome.Failure);
        }

        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy("Prometheus reachable.")
            : HealthCheckResult.Unhealthy($"Prometheus responded {(int)response.StatusCode}.");
    }
}
