using System.ComponentModel.DataAnnotations;

namespace AgentForge.Api.Health;

/// <summary>
/// Backoff for retrying the store's startup work (<see cref="DataStoreStartupService"/>), bound via the Options
/// pattern (CONVENTIONS.md §6). The retry never gives up - a store that comes back must bring
/// <c>/ready</c> back without a restart - so only the spacing is configurable.
/// </summary>
public sealed class DataStoreStartupOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "DataStoreStartup";

    /// <summary>Wait after the first failed attempt; doubles on each failure after it.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait between attempts, however long the store has been down.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest delay <see cref="Task.Delay(TimeSpan,TimeProvider,CancellationToken)"/> accepts - <c>uint.MaxValue -
    /// 1</c> ms, about 49.7 days. Past this it throws <see cref="ArgumentOutOfRangeException"/> instead of waiting,
    /// which would crash <see cref="DataStoreStartupService"/> the next time the delay is used.
    /// </summary>
    public static readonly TimeSpan MaxTaskDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (InitialRetryDelay <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                $"{nameof(InitialRetryDelay)} must be positive.", [nameof(InitialRetryDelay)]);
        }
        else if (InitialRetryDelay > MaxTaskDelay)
        {
            // Task.Delay throws past this, in DataStoreStartupService, not here - fail fast at startup instead.
            yield return new ValidationResult(
                $"{nameof(InitialRetryDelay)} must not exceed {MaxTaskDelay}.", [nameof(InitialRetryDelay)]);
        }
        else if (MaxRetryDelay < InitialRetryDelay)
        {
            yield return new ValidationResult(
                $"{nameof(MaxRetryDelay)} must not be less than {nameof(InitialRetryDelay)}.", [nameof(MaxRetryDelay)]);
        }
        else if (MaxRetryDelay > MaxTaskDelay)
        {
            yield return new ValidationResult(
                $"{nameof(MaxRetryDelay)} must not exceed {MaxTaskDelay}.", [nameof(MaxRetryDelay)]);
        }
    }
}
