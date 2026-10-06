namespace AgentForge.Api.Observability;

/// <summary>
/// Where the sidecar finds the eval run it publishes as <c>agentforge_eval_*</c> series. Optional
/// telemetry, so nothing here is validated on start: a missing run is logged and the sidecar serves
/// traffic without the series. A separate change
/// </summary>
public sealed class EvalResultsOptions
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "EvalResults";

    /// <summary>
    /// The eval gate's <c>results.json</c>. Relative paths resolve against the content root; the default is
    /// where the <c>Dockerfile</c>'s <c>evals</c> stage puts the run it made against the image's own source.
    /// Blank disables the series.
    /// </summary>
    public string ResultsPath { get; init; } = "evals/results.json";

    /// <summary>The <c>baseline.json</c> that run was judged against, resolved the same way.</summary>
    public string BaselinePath { get; init; } = "evals/baseline.json";
}
