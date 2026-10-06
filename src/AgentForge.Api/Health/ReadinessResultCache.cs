using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Holds the last answer of one <c>/ready</c> check, <typeparamref name="TCheck"/>, for
/// <see cref="ReadinessOptions.ResultCacheTtl"/>. A singleton per check type, so each check keeps its
/// own slot for the life of the process.
/// </summary>
/// <remarks>
/// The probe runs detached from every caller: it starts without the caller's token and is shared as one
/// in-flight task, which each caller awaits under its own token. A caller that disconnects therefore only
/// stops waiting; the probe finishes and its answer is cached, so aborting <c>/ready</c> in a loop cannot
/// keep the cache empty. The probe stays bounded by <see cref="ReadinessOptions.ProbeTimeout"/>, which
/// every wrapped check applies itself through <see cref="ReadinessProbe"/>. Every answer is cached, a
/// failure included, because a rejected key would otherwise turn each call back into a dependency request
/// for as long as the outage lasted. A probe that throws caches nothing and clears the in-flight slot, so
/// the next call probes again.
/// </remarks>
/// <typeparam name="TCheck">The check whose answer this holds; it keys the singleton.</typeparam>
public sealed class ReadinessResultCache<TCheck>(TimeProvider timeProvider, IOptions<ReadinessOptions> options)
    where TCheck : IHealthCheck
{
    private readonly Lock _sync = new();
    private CachedResult? _last;
    private Task<HealthCheckResult>? _inFlight;

    /// <summary>
    /// Returns the cached answer while it is younger than the TTL; otherwise awaits the one in-flight run
    /// of <paramref name="probe"/>, starting it if none is running. <paramref name="cancellationToken"/>
    /// ends only this caller's wait, never the probe.
    /// </summary>
    public Task<HealthCheckResult> GetOrProbeAsync(
        Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        Task<HealthCheckResult> shared;
        lock (_sync)
        {
            if (_last is not null && timeProvider.GetUtcNow() - _last.At < options.Value.ResultCacheTtl)
            {
                return Task.FromResult(_last.Result);
            }

            // Task.Run: the probe's finally takes this lock, so it cannot clear the slot before it is set.
            _inFlight ??= Task.Run(() => ProbeAndStoreAsync(probe), CancellationToken.None);
            shared = _inFlight;
        }

        return shared.WaitAsync(cancellationToken);
    }

    private async Task<HealthCheckResult> ProbeAndStoreAsync(Func<CancellationToken, Task<HealthCheckResult>> probe)
    {
        try
        {
            var result = await probe(CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _last = new CachedResult(result, timeProvider.GetUtcNow());
            }

            return result;
        }
        finally
        {
            lock (_sync)
            {
                _inFlight = null;
            }
        }
    }

    private sealed record CachedResult(HealthCheckResult Result, DateTimeOffset At);
}
