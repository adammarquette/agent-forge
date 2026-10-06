using System.ComponentModel.DataAnnotations;

namespace AgentForge.Api.Health;

/// <summary>
/// Bounds how long a single <c>/ready</c> dependency probe may take, bound via the Options pattern
/// (CONVENTIONS.md §6). Readiness is always read under someone else's deadline - a load
/// balancer, an orchestrator, an uptime check - so a probe that outlives that deadline makes the
/// endpoint useless whatever it eventually answers. <see cref="HttpClient"/>'s own default of 100
/// seconds is not a readiness budget, and inheriting it is what made staging's <c>/ready</c> take
/// 100.33s to report an unreachable Prometheus; every probe now applies this bound itself
/// </summary>
public sealed class ReadinessOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Readiness";

    /// <summary>
    /// Wall-clock bound on one dependency probe (<see cref="OpenEmrHealthCheck"/>,
    /// <see cref="LlmProviderHealthCheck"/>, <see cref="ObservabilityHealthCheck"/>,
    /// <see cref="VectorIndexHealthCheck"/>). A probe that
    /// has not answered inside it is reported as unreachable rather than waited on, so
    /// <c>/ready</c> answers in at most this much per check even when a dependency accepts the
    /// connection and goes silent. Two seconds is a working default rather than a tuned one - it is
    /// well under any readiness deadline worth having, and deliberately safe in an environment that
    /// never sets this section, which is both deployed environments today.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the answer of a check that leaves the deployment - <see cref="LlmProviderHealthCheck"/>,
    /// <see cref="OpenEmrHealthCheck"/>, <see cref="RerankerHealthCheck"/> - is reused
    /// (<see cref="CachedReadinessCheck{TCheck}"/>), so a public <c>/ready</c> cannot be turned into one
    /// dependency request per call. It is also how late <c>/ready</c> can be to show an outage or a
    /// recovery. Thirty seconds leaves <c>post-deploy-verify.sh --wait 120</c> room to see a fresh deploy
    /// recover from a failed first probe.
    /// </summary>
    public TimeSpan ResultCacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ProbeTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult($"{nameof(ProbeTimeout)} must be positive.", [nameof(ProbeTimeout)]);
        }

        // Zero would quietly restore one dependency request per /ready call.
        if (ResultCacheTtl <= TimeSpan.Zero)
        {
            yield return new ValidationResult($"{nameof(ResultCacheTtl)} must be positive.", [nameof(ResultCacheTtl)]);
        }
    }
}
