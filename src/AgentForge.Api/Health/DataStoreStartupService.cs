using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Runs the store's startup work (<see cref="IDataStoreStartupWork"/>) after the host is listening, retrying each
/// step with capped exponential backoff until it completes. A store that is unreachable at boot therefore leaves
/// the host up - liveness answers and <c>/ready</c> is 503 naming the store - rather than aborting startup, and a
/// store that comes back brings <c>/ready</c> to 200 without a restart (maintainer ruling).
/// Each failed attempt is logged with the elapsed wait, so a cold start reads differently from an outage
/// </summary>
/// <remarks>
/// Only the migrations gate readiness (<see cref="DataStoreStartupState"/>). The seed runs after them and is
/// retried the same way, but a seed that is failing does not hold <c>/ready</c> at 503: with the store up its only
/// other dependency is the embedding provider, whose outage degrades retrieval rather than failing it
/// (<c>ARCHITECTURE-DOCUMENTS.md</c> §10), and a store that is down again is already a 503 through the probe.
/// </remarks>
public sealed class DataStoreStartupService(
    IDataStoreStartupWork work,
    DataStoreStartupState state,
    IOptions<DataStoreStartupOptions> options,
    TimeProvider timeProvider,
    ILogger<DataStoreStartupService> logger) : BackgroundService
{
    /// <summary>Step name logged for the migrations.</summary>
    internal const string MigrationsStep = "migrations";

    /// <summary>Step name logged for the guideline seed.</summary>
    internal const string SeedStep = "guideline seed";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the boot path before the first connect: StartAsync must not wait on the store.
        await Task.Yield();

        if (!await RetryUntilDoneAsync(MigrationsStep, work.MigrateAsync, state.RecordFailure, stoppingToken)
                .ConfigureAwait(false))
        {
            return;
        }

        state.MarkComplete();
        await RetryUntilDoneAsync(SeedStep, work.SeedAsync, static _ => { }, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>Doubles <paramref name="previous"/>, capped at <see cref="DataStoreStartupOptions.MaxRetryDelay"/>.</summary>
    internal static TimeSpan NextRetryDelay(TimeSpan previous, DataStoreStartupOptions options)
    {
        var doubled = previous * 2;
        return doubled > options.MaxRetryDelay ? options.MaxRetryDelay : doubled;
    }

    private async Task<bool> RetryUntilDoneAsync(
        string step, Func<CancellationToken, Task> run, Action<Exception> recordFailure, CancellationToken stoppingToken)
    {
        var started = timeProvider.GetTimestamp();
        var delay = options.Value.InitialRetryDelay;
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await run(stoppingToken).ConfigureAwait(false);
                var elapsed = timeProvider.GetElapsedTime(started).TotalSeconds;
                DataStoreStartupLog.Completed(logger, step, attempt, elapsed);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                // Everything else is retried: fatal here is the abort-on-boot a separate change ruled out.
                recordFailure(ex);
                var elapsed = timeProvider.GetElapsedTime(started).TotalSeconds;
                var failureType = ex.GetType().Name;
                DataStoreStartupLog.AttemptFailed(logger, step, attempt, elapsed, failureType, delay.TotalSeconds, ex);
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            delay = NextRetryDelay(delay, options.Value);
        }

        return false;
    }
}

/// <summary>Source-generated log messages for <see cref="DataStoreStartupService"/> (CA1848).</summary>
internal static partial class DataStoreStartupLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Data store startup {Step} attempt {Attempt} failed after {ElapsedSeconds:0.#}s of waiting " +
            "({FailureType}); the host stays up and retries in {RetryDelaySeconds:0.#}s")]
    public static partial void AttemptFailed(
        ILogger logger, string step, int attempt, double elapsedSeconds, string failureType, double retryDelaySeconds,
        Exception failure);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Data store startup {Step} completed on attempt {Attempt} after {ElapsedSeconds:0.#}s")]
    public static partial void Completed(ILogger logger, string step, int attempt, double elapsedSeconds);
}
