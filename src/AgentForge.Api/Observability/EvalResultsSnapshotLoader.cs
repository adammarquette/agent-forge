using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>
/// Reads the eval run and its baseline for <see cref="EvalResultsMetrics"/>. Never throws: the series is
/// optional telemetry and the sidecar must boot without it, so every way of not having a run is logged by
/// name instead - a silent absence would read as "no regression".
/// </summary>
/// <param name="readFile">Returns a file's text, or <see langword="null"/> when it does not exist.</param>
/// <param name="logger">Where a missing or unreadable run is reported.</param>
internal sealed class EvalResultsSnapshotLoader(Func<string, string?> readFile, ILogger<EvalResultsSnapshotLoader> logger)
{
    /// <summary>Reads from disk.</summary>
    public EvalResultsSnapshotLoader(ILogger<EvalResultsSnapshotLoader> logger)
        : this(path => File.Exists(path) ? File.ReadAllText(path) : null, logger)
    {
    }

    /// <summary>The run named by <paramref name="options"/>, or <see langword="null"/> when there is none to publish.</summary>
    public EvalResultsSnapshot? Load(EvalResultsOptions options, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(options.ResultsPath))
        {
            EvalResultsSnapshotLoaderLog.Disabled(logger);
            return null;
        }

        var resultsPath = Path.GetFullPath(options.ResultsPath, contentRootPath);
        var baselinePath = Path.GetFullPath(options.BaselinePath, contentRootPath);

        try
        {
            var results = readFile(resultsPath);
            if (results is null)
            {
                EvalResultsSnapshotLoaderLog.FileMissing(logger, resultsPath);
                return null;
            }

            var baseline = readFile(baselinePath);
            if (baseline is null)
            {
                EvalResultsSnapshotLoaderLog.FileMissing(logger, baselinePath);
                return null;
            }

            var snapshot = EvalResultsSnapshot.Parse(results, baseline);
            EvalResultsSnapshotLoaderLog.Loaded(logger, snapshot.GeneratedAt, snapshot.CategoryPassRates.Count);
            return snapshot;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            EvalResultsSnapshotLoaderLog.Unreadable(logger, resultsPath, baselinePath, ex.Message);
            return null;
        }
    }
}
