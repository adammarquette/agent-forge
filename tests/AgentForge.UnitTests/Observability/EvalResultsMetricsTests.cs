using System.Diagnostics.Metrics;
using FluentAssertions;
using AgentForge.Observability;

namespace AgentForge.UnitTests.Observability;

/// <summary>
/// The per-category eval series <c>AgentForgeEvalCategoryRegression</c> evaluates. Given the eval run
/// baked into the running build and the baseline it was judged against, when Prometheus scrapes, then
/// the two gauges carry one bounded <c>category</c> label each, and nothing else. A separate change
/// </summary>
public sealed class EvalResultsMetricsTests : IDisposable
{
    private const string Baseline = """
        {
          "max_regression": 0.05,
          "pass_thresholds": { "safety": 1.0, "quality": 0.8 },
          "categories": {
            "no_phi_in_logs": { "baseline": 1.0, "tier": "safety" },
            "citation_present": { "baseline": 0.9, "tier": "quality" }
          }
        }
        """;

    private const string Results = """
        {
          "generated_at": "2026-09-23T10:15:30.5+00:00",
          "total_cases": 3,
          "passed": false,
          "category_rates": { "no_phi_in_logs": 1.0, "citation_present": 0.8 }
        }
        """;

    private readonly List<(string Name, double Value, IReadOnlyDictionary<string, object?> Tags)> _observed = [];
    private readonly MeterListener _listener = new();
    private readonly TestMeterFactory _meterFactory = new();

    public EvalResultsMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Scoped to this test's own factory: a host built by another test class registers the same
            // instruments under the same meter name, and would otherwise report into this listener.
            if (instrument.Meter.Name == AgentForgeMetrics.MeterName && ReferenceEquals(instrument.Meter.Scope, _meterFactory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            _observed.Add((instrument.Name, value, dict));
        });
        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        _meterFactory.Dispose();
    }

    [Fact]
    public void Parse_ValidResultsAndBaseline_ReadsRatesBaselinesAndRunTime()
    {
        var snapshot = EvalResultsSnapshot.Parse(Results, Baseline);

        snapshot.CategoryPassRates.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["no_phi_in_logs"] = 1.0,
            ["citation_present"] = 0.8,
        });
        snapshot.BaselinePassRates.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["no_phi_in_logs"] = 1.0,
            ["citation_present"] = 0.9,
        });
        snapshot.GeneratedAt.Should().Be(new DateTimeOffset(2026, 9, 23, 10, 15, 30, 500, TimeSpan.Zero));
        snapshot.Passed.Should().BeFalse();
    }

    [Fact]
    public void Parse_ResultCategoryAbsentFromBaseline_IsNotPublished()
    {
        // The label's value set is the policy file's category list, which the repository owns. A result
        // row the policy does not name has no threshold to regress against, and must not mint a series.
        const string results = """
            { "generated_at": "2026-09-23T00:00:00Z", "passed": true,
              "category_rates": { "no_phi_in_logs": 1.0, "not_a_policy_category": 0.1 } }
            """;

        var snapshot = EvalResultsSnapshot.Parse(results, Baseline);

        snapshot.CategoryPassRates.Keys.Should().Equal("no_phi_in_logs");
    }

    [Theory]
    [InlineData("""{ "category_rates": { "no_phi_in_logs": 1.0 } }""")]
    [InlineData("""{ "generated_at": "2026-09-23T00:00:00Z", "passed": true }""")]
    [InlineData("""{ "generated_at": "2026-09-23T00:00:00Z", "passed": true, "category_rates": { "no_phi_in_logs": 1.5 } }""")]
    [InlineData("""{ "generated_at": "2026-09-23T00:00:00Z", "passed": true, "category_rates": { "no_phi_in_logs": "high" } }""")]
    [InlineData("""{ "generated_at": "2026-09-23T00:00:00Z", "category_rates": { "no_phi_in_logs": 1.0 } }""")]
    [InlineData("""{ "generated_at": "2026-09-23T00:00:00Z", "passed": "false", "category_rates": { "no_phi_in_logs": 1.0 } }""")]
    [InlineData("not json")]
    public void Parse_MalformedResults_Throws(string results)
    {
        var act = () => EvalResultsSnapshot.Parse(results, Baseline);

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("""{ "max_regression": 0.05 }""")]
    [InlineData("""{ "categories": { "no_phi_in_logs": 1.0 } }""")]
    [InlineData("""{ "categories": { "no_phi_in_logs": { "tier": "safety" } } }""")]
    [InlineData("""{ "categories": { "no_phi_in_logs": { "baseline": -0.1, "tier": "safety" } } }""")]
    public void Parse_MalformedBaseline_Throws(string baseline)
    {
        // A bare-number baseline is the earlier shape; reading it as "no baseline" would publish rates
        // with nothing to compare them to and leave the alert silently unable to fire.
        var act = () => EvalResultsSnapshot.Parse(Results, baseline);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Observe_WithSnapshot_PublishesEachCategoryRateAndBaselineAndTheRunTime()
    {
        using var sut = new EvalResultsMetrics(_meterFactory, EvalResultsSnapshot.Parse(Results, Baseline));

        _listener.RecordObservableInstruments();

        _observed.Should().Contain(m => m.Name == "agentforge.eval.category_pass_rate"
            && (string)m.Tags["category"]! == "citation_present" && m.Value == 0.8);
        _observed.Should().Contain(m => m.Name == "agentforge.eval.category_pass_rate"
            && (string)m.Tags["category"]! == "no_phi_in_logs" && m.Value == 1.0);
        _observed.Should().Contain(m => m.Name == "agentforge.eval.baseline_pass_rate"
            && (string)m.Tags["category"]! == "citation_present" && m.Value == 0.9);
        _observed.Should().Contain(m => m.Name == "agentforge.eval.run_timestamp"
            && m.Value == new DateTimeOffset(2026, 9, 23, 10, 15, 30, 500, TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000d);
    }

    [Fact]
    public void Observe_WithSnapshot_TagsRatesWithCategoryOnly()
    {
        // Every label is a permanent series; the rule joins on(category), so nothing else is needed.
        using var sut = new EvalResultsMetrics(_meterFactory, EvalResultsSnapshot.Parse(Results, Baseline));

        _listener.RecordObservableInstruments();

        _observed.Where(m => m.Name.EndsWith("_pass_rate", StringComparison.Ordinal))
            .Should().OnlyContain(m => m.Tags.Count == 1 && m.Tags.ContainsKey("category"));
        _observed.Single(m => m.Name == "agentforge.eval.run_timestamp").Tags.Should().BeEmpty();
        _observed.Single(m => m.Name == "agentforge.eval.run_passed").Tags.Should().BeEmpty();
    }

    [Fact]
    public void Observe_CategoryInBaselineButNotEvaluated_PublishesItsBaselineAndNoRate()
    {
        // The staleness answer: a category that stopped being evaluated has no rate to report, so it is
        // absent rather than frozen at its last value, and its baseline alone shows that it went missing.
        const string results = """{ "generated_at": "2026-09-23T00:00:00Z", "passed": true, "category_rates": { "no_phi_in_logs": 1.0 } }""";
        using var sut = new EvalResultsMetrics(_meterFactory, EvalResultsSnapshot.Parse(results, Baseline));

        _listener.RecordObservableInstruments();

        _observed.Should().NotContain(m => m.Name == "agentforge.eval.category_pass_rate"
            && (string)m.Tags["category"]! == "citation_present");
        _observed.Should().Contain(m => m.Name == "agentforge.eval.baseline_pass_rate"
            && (string)m.Tags["category"]! == "citation_present");
    }

    [Theory]
    [InlineData(false, 0d)]
    [InlineData(true, 1d)]
    public void Observe_WithSnapshot_PublishesTheGateVerdict(bool passed, double expected)
    {
        // The gate fails on things no per-category drop shows: a SAFETY rubric one case below its 100%
        // floor (a 1-point drop, under the 5-point regression rule), an orphaned baseline, a population
        // check. The verdict is the one series that carries all of them. A separate change
        var results = $$"""{ "generated_at": "2026-09-23T00:00:00Z", "passed": {{(passed ? "true" : "false")}}, "category_rates": { "no_phi_in_logs": 0.98936 } }""";
        using var sut = new EvalResultsMetrics(_meterFactory, EvalResultsSnapshot.Parse(results, Baseline));

        _listener.RecordObservableInstruments();

        _observed.Should().ContainSingle(m => m.Name == "agentforge.eval.run_passed")
            .Which.Value.Should().Be(expected);
    }

    [Fact]
    public void Observe_WithoutSnapshot_PublishesNothing()
    {
        using var sut = new EvalResultsMetrics(_meterFactory, snapshot: null);

        _listener.RecordObservableInstruments();

        _observed.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_Always_PublishesUnderTheMeterPrometheusScrapes()
    {
        using var sut = new EvalResultsMetrics(_meterFactory, EvalResultsSnapshot.Parse(Results, Baseline));

        _listener.RecordObservableInstruments();

        _observed.Select(m => m.Name).Distinct().Should().BeEquivalentTo(
            "agentforge.eval.category_pass_rate", "agentforge.eval.baseline_pass_rate", "agentforge.eval.run_timestamp",
            "agentforge.eval.run_passed");
    }
}

/// <summary>An <see cref="IMeterFactory"/> whose meters are scoped to it, so a listener can tell them apart.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}
