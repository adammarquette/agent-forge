using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentForge.Api.Health;

/// <summary>
/// Serves <typeparamref name="TCheck"/>'s answer through its <see cref="ReadinessResultCache{TCheck}"/>,
/// so <c>/ready</c> - public through the proxy - asks an external dependency at most once per
/// <see cref="ReadinessOptions.ResultCacheTtl"/> however often it is called.
/// </summary>
/// <remarks>
/// The wrapped check still decides the status (<c>ARCHITECTURE.md</c> D17); this only decides when to
/// ask it again. So <c>/ready</c> reflects an outage or a recovery at most one TTL plus two probe
/// durations after it happens, about 34 seconds at the defaults: the TTL runs from a probe's completion.
/// </remarks>
/// <typeparam name="TCheck">The check being cached.</typeparam>
public sealed class CachedReadinessCheck<TCheck>(TCheck inner, ReadinessResultCache<TCheck> cache) : IHealthCheck
    where TCheck : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        cache.GetOrProbeAsync(ct => inner.CheckHealthAsync(context, ct), cancellationToken);
}
