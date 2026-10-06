using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Observability;

/// <summary>Source-generated log messages for <see cref="EvalResultsSnapshotLoader"/> (CA1848). A separate change</summary>
internal static partial class EvalResultsSnapshotLoaderLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Eval series disabled - EvalResults:ResultsPath is blank, so no agentforge_eval_* series is published")]
    public static partial void Disabled(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No eval run at {Path} - no agentforge_eval_* series is published, so AgentForgeEvalCategoryRegression " +
            "cannot fire for this build")]
    public static partial void FileMissing(ILogger logger, string path);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Eval run at {ResultsPath} / {BaselinePath} is unreadable ({Reason}) - no agentforge_eval_* series is " +
            "published, so AgentForgeEvalCategoryRegression cannot fire for this build")]
    public static partial void Unreadable(ILogger logger, string resultsPath, string baselinePath, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Publishing the eval run generated at {GeneratedAt:o}: {Categories} rubric categories")]
    public static partial void Loaded(ILogger logger, DateTimeOffset generatedAt, int categories);
}
