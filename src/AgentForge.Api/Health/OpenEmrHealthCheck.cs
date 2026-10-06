using AgentForge.Integration.OpenEmr;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Readiness check for the OpenEMR FHIR dependency (NFR-HEALTH-1): a real GET against the FHIR
/// endpoint's SMART discovery document (<c>/apis/{site}/fhir/.well-known/smart-configuration</c>) -
/// publicly readable per SMART App Launch, no bearer token needed - not an unconditional pass.
/// Bounded by <see cref="ReadinessOptions.ProbeTimeout"/>: OpenEMR is product-blocking, so an
/// unreachable one still fails readiness, but it fails it promptly rather than holding <c>/ready</c>
/// open.
/// </summary>
/// <remarks>
/// <b>Why not <c>/fhir/metadata</c>.</b> OpenEMR builds the CapabilityStatement afresh on every
/// request, and it measured 1.95-7.55s through production's front door and 4.65-8.8s in staging
/// (inside the container too, so the time is PHP rather than the proxy). No readiness budget worth
/// having covers that, so the 2s one read every environment Unhealthy. The discovery document is
/// served from the same FHIR route table, behind the same authorization skip-list, by the same
/// dispatcher, and measured 0.32-1.95s - it proves the same thing, cheaply
/// (<c>ARCHITECTURE.md</c> D17).
/// </remarks>
public sealed class OpenEmrHealthCheck(
    HttpClient httpClient,
    IOptions<OpenEmrOptions> options,
    IOptions<ReadinessOptions> readinessOptions) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var uri = $"{options.Value.BaseUrl.TrimEnd('/')}/apis/{options.Value.Site}/fhir/.well-known/smart-configuration";
        var probeTimeout = readinessOptions.Value.ProbeTimeout;

        var outcome = await ReadinessProbe.GetAsync(httpClient, uri, probeTimeout, cancellationToken)
            .ConfigureAwait(false);
        using var response = outcome.Response;

        if (response is null)
        {
            return HealthCheckResult.Unhealthy(
                $"OpenEMR FHIR {outcome.DescribeFailure(probeTimeout)}.", outcome.Failure);
        }

        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy("OpenEMR FHIR SMART discovery document reachable.")
            : HealthCheckResult.Unhealthy($"OpenEMR FHIR responded {(int)response.StatusCode}.");
    }
}
