using System.ComponentModel.DataAnnotations;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Daily Agenda fan-out configuration, bound via the Options pattern (CONVENTIONS.md
/// §6, ARCHITECTURE.md §19.4).
/// </summary>
public sealed class AgendaOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Agenda";

    private static readonly TimeSpan MaxSummaryCacheTtl = TimeSpan.FromHours(12);

    /// <summary>
    /// Bounds how many per-patient summary turns run concurrently - protects both the LLM
    /// provider's rate limits and OpenEMR against a full ~20-30-patient panel fired unbounded in
    /// one request. Lowered to 3 to ease peak contention on the slow staging OpenEMR, which was
    /// tripping FHIR timeouts under the roster fan-out.
    /// </summary>
    public int MaxConcurrentSummaries { get; init; } = 3;

    /// <summary>
    /// How long a generated patient summary is re-served to reloads of the same clinician's roster before it is
    /// regenerated (and charged) again. A TTL rather than chart-change invalidation: nothing tells the sidecar a
    /// chart changed, and probing each chart on every load would cost the FHIR calls the fan-out is throttled
    /// for. 30 minutes bounds how stale a glance can be while a reload costs nothing. At most 12 hours - one
    /// budget window. A separate change
    /// </summary>
    public TimeSpan SummaryCacheTtl { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Bounds how many summaries the process holds. A clinician's day is a few dozen, so the default covers
    /// dozens of concurrent clinicians before eviction starts.
    /// </summary>
    public int MaxCachedSummaries { get; init; } = 1000;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxConcurrentSummaries <= 0)
        {
            yield return new ValidationResult(
                $"{nameof(MaxConcurrentSummaries)} must be positive.", [nameof(MaxConcurrentSummaries)]);
        }

        if (SummaryCacheTtl <= TimeSpan.Zero || SummaryCacheTtl > MaxSummaryCacheTtl)
        {
            yield return new ValidationResult(
                $"{nameof(SummaryCacheTtl)} must be positive and at most {MaxSummaryCacheTtl}.", [nameof(SummaryCacheTtl)]);
        }

        if (MaxCachedSummaries <= 0)
        {
            yield return new ValidationResult(
                $"{nameof(MaxCachedSummaries)} must be positive.", [nameof(MaxCachedSummaries)]);
        }
    }
}
