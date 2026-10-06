using System.Diagnostics.Metrics;

namespace AgentForge.Observability;

/// <summary>
/// Publishes the eval run baked into the running build as the series
/// <c>AgentForgeEvalCategoryRegression</c> evaluates and the dashboard's live eval panel plots:
/// <c>agentforge_eval_category_pass_rate{category}</c>, <c>agentforge_eval_baseline_pass_rate{category}</c>
/// <c>agentforge_eval_run_timestamp_seconds</c> and <c>agentforge_eval_run_passed</c> (the gate's verdict, 1/0 -
/// the only series that shows a safety-floor, orphan or population failure, none of which is a >5-point drop). Observable gauges read on every scrape, so a value is
/// never cached anywhere that could outlive this process, and a category the run did not evaluate has no
/// rate at all rather than a frozen one. With no snapshot it publishes nothing. A separate change
/// </summary>
public sealed class EvalResultsMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Registers the three instruments under <see cref="AgentForgeMetrics.MeterName"/>.</summary>
    public EvalResultsMetrics(IMeterFactory meterFactory, EvalResultsSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _meter = meterFactory.Create(AgentForgeMetrics.MeterName);

        _meter.CreateObservableGauge<double>(
            "agentforge.eval.category_pass_rate",
            () => Rates(snapshot?.CategoryPassRates),
            description: "Eval pass rate per rubric category, from the run the running build's image was built with.");
        _meter.CreateObservableGauge<double>(
            "agentforge.eval.baseline_pass_rate",
            () => Rates(snapshot?.BaselinePassRates),
            description: "Baseline pass rate per rubric category (evals/baseline.json) that run was judged against.");
        _meter.CreateObservableGauge<double>(
            "agentforge.eval.run_timestamp",
            () => snapshot is null
                ? []
                : [new Measurement<double>((snapshot.GeneratedAt - DateTimeOffset.UnixEpoch).TotalSeconds)],
            unit: "s",
            description: "When that eval run was made, as Unix time.");
        _meter.CreateObservableGauge<double>(
            "agentforge.eval.run_passed",
            () => snapshot is null ? [] : [new Measurement<double>(snapshot.Passed ? 1 : 0)],
            description: "1 if the eval gate passed that run, 0 if it blocked it.");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private static IEnumerable<Measurement<double>> Rates(IReadOnlyDictionary<string, double>? rates) =>
        rates is null
            ? []
            : rates.Select(r => new Measurement<double>(r.Value, new KeyValuePair<string, object?>("category", r.Key)));
}
