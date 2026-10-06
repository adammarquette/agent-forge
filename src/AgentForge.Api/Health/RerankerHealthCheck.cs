using AgentForge.Retrieval;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Readiness check for the Cohere reranker (NFR-HEALTH-W2-1): an authenticated GET of Cohere's
/// <c>/v1/models</c> — cheap and side-effect-free, unlike <c>/v2/rerank</c> — when
/// <c>Cohere:ApiKey</c> is configured. Bounded by <see cref="ReadinessOptions.ProbeTimeout"/> like every
/// other <c>/ready</c> probe.
/// </summary>
/// <remarks>
/// Two outcomes short of a clean answer, and the line between them is whether the key is set.
/// <b>Not configured</b> is <see cref="HealthStatus.Degraded"/>: sparse-only retrieval is a valid
/// deployment shape (<see cref="RetrievalServiceCollectionExtensions"/> wires
/// <c>DisabledReranker</c>), so "never contacted" is a distinct, honest state rather than an
/// unconditional pass (NFR-REL-2, <c>ARCHITECTURE.md</c> D17). <b>Configured and not answering</b> is
/// <see cref="HealthStatus.Unhealthy"/>, exactly like <see cref="ObservabilityHealthCheck"/> and
/// <see cref="LlmProviderHealthCheck"/> — NFR-HEALTH-W2-1 names the reranker API among the dependencies
/// <c>/ready</c> must surface when it is wired.
/// </remarks>
public sealed class RerankerHealthCheck(
    HttpClient httpClient,
    IOptions<CohereOptions> options,
    IOptions<ReadinessOptions> readinessOptions) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cohere = options.Value;
        if (!cohere.IsEnabled)
        {
            return HealthCheckResult.Degraded(
                $"{CohereOptions.SectionName}:ApiKey not configured - reranking is not wired in this deployment; retrieval runs sparse-only and the reranker was never contacted.");
        }

        var uri = $"{cohere.BaseUrl.TrimEnd('/')}/v1/models";
        var probeTimeout = readinessOptions.Value.ProbeTimeout;

        var outcome = await ReadinessProbe.GetAsync(httpClient, uri, probeTimeout, cancellationToken)
            .ConfigureAwait(false);
        using var response = outcome.Response;

        if (response is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Reranker (Cohere) {outcome.DescribeFailure(probeTimeout)}.", outcome.Failure);
        }

        return response.IsSuccessStatusCode
            ? HealthCheckResult.Healthy("Reranker (Cohere) reachable.")
            : HealthCheckResult.Unhealthy($"Reranker (Cohere) responded {(int)response.StatusCode}.");
    }
}
