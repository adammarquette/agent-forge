using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentForge.Observability;
using FluentAssertions;

namespace AgentForge.UnitTests.Observability;

/// <summary>
/// Pins one word across the four places an on-call reads about a failed retrieval stage: the
/// <c>agentforge.retrieval_degradations</c> instrument, the <c>AgentForgeRetrievalDegradation</c> alert, the
/// dashboard panel over that counter, and the rerank latency instrument beside it.
/// <para>
/// The <c>retrieval.*</c> spans reserve <c>skipped</c> for a stage that never ran (no candidates) and use
/// <c>degraded</c> for one that failed. These strings called the failure "skipped", so a responder who read
/// the alert and filtered traces on <c>outcome=skipped</c> found the empty-corpus spans, never the failures.
/// </para>
/// </summary>
public sealed class RetrievalDegradationVocabularyTests : IDisposable
{
    private const string AlertRuleRelativePath = "observability/alerts/agentforge-alerts.yml";
    private const string DashboardRelativePath = "observability/grafana/dashboards/agentforge.json";
    private const string AlertName = "AgentForgeRetrievalDegradation";
    private const string DegradationSeries = "agentforge_retrieval_degradations_total";

    // Any form of "skip", whichever tense a later edit reaches for - except the literal span filter
    // `outcome=skipped`, which a responder may be told is the other case.
    private static readonly Regex SkipWord = new(@"(?<!outcome=)\bskip(s|ped|ping)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly List<Instrument> _instruments = [];
    private readonly MeterListener _listener = new();
    private readonly AgentForgeMetrics _metrics = new();

    public RetrievalDegradationVocabularyTests()
    {
        _listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == AgentForgeMetrics.MeterName)
            {
                lock (_instruments)
                {
                    _instruments.Add(instrument);
                }
            }
        };
        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public void RetrievalDegradationsInstrument_Description_CallsAFailedStageDegradedNotSkipped()
    {
        var description = InstrumentDescription("agentforge.retrieval_degradations");

        description.Should().Contain("degraded", "the span outcome for a failed stage is `degraded`");
        SkipWord.IsMatch(description).Should().BeFalse(
            "`skipped` is the span outcome for a stage with no candidates, not a failed one; description was: {0}", description);
    }

    [Fact]
    public void RerankDurationInstrument_Description_SaysADegradedCallIsCountedElsewhere()
    {
        var description = InstrumentDescription("agentforge.rerank.duration");

        // The gap between this histogram's count and the retrieval count was read as "rerank skipped"
        // (METRICS.md); the description has to say where the missing samples went.
        description.Should().Contain("degraded").And.Contain("agentforge.retrieval_degradations");
        SkipWord.IsMatch(description).Should().BeFalse("description was: {0}", description);
    }

    [Fact]
    public void RetrievalDegradationAlert_AsWrittenInTheRuleFile_CallsAFailedStageDegradedNotSkipped()
    {
        var rule = ReadRuleBlock(File.ReadAllText(RepositoryPath(AlertRuleRelativePath)));

        rule.Should().Contain(DegradationSeries, "this is the rule over the degradation counter");
        rule.Should().Contain("outcome=degraded", "the alert should name the span filter that finds these failures");
        SkipWord.IsMatch(rule).Should().BeFalse("{0} must not call a failed stage `skipped`", AlertName);
    }

    [Fact]
    public void RetrievalDegradationsPanel_AsWrittenInTheDashboard_CallsAFailedStageDegradedNotSkipped()
    {
        using var dashboard = JsonDocument.Parse(File.ReadAllText(RepositoryPath(DashboardRelativePath)));

        var panels = Panels(dashboard.RootElement)
            .Where(p => p.TryGetProperty("targets", out var targets)
                && targets.EnumerateArray().Any(t => t.TryGetProperty("expr", out var expr)
                    && expr.GetString()!.Contains(DegradationSeries, StringComparison.Ordinal)))
            .ToList();

        panels.Should().NotBeEmpty("a dashboard panel plots {0}", DegradationSeries);
        foreach (var panel in panels)
        {
            var text = panel.GetProperty("title").GetString() + " " + panel.GetProperty("description").GetString();
            text.Should().Contain("degraded");
            SkipWord.IsMatch(text).Should().BeFalse("panel text was: {0}", text);
        }
    }

    private string InstrumentDescription(string name)
    {
        // Other test classes' instances publish the same instrument in parallel; every copy says the same.
        List<Instrument> published;
        lock (_instruments)
        {
            published = [.. _instruments.Where(i => i.Name == name)];
        }

        published.Should().NotBeEmpty("{0} must still be published by AgentForgeMetrics", name);
        return published[0].Description ?? string.Empty;
    }

    private static IEnumerable<JsonElement> Panels(JsonElement element)
    {
        if (!element.TryGetProperty("panels", out var panels))
        {
            yield break;
        }

        foreach (var panel in panels.EnumerateArray())
        {
            yield return panel;
            foreach (var nested in Panels(panel))
            {
                yield return nested;
            }
        }
    }

    private static string RepositoryPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root is found by walking up to AgentForge.slnx");
        var path = Path.Combine(directory!.FullName, relative);
        File.Exists(path).Should().BeTrue("{0} is a file this test guards", path);
        return path;
    }

    // From this rule's `- alert:` line (name ending the line) to the next rule or group-level comment, so
    // another rule's prose cannot satisfy or fail it.
    private static string ReadRuleBlock(string rules)
    {
        var match = Regex.Match(rules, $@"- alert: {AlertName}\r?$", RegexOptions.Multiline);
        match.Success.Should().BeTrue("{0} must still exist in {1}", AlertName, AlertRuleRelativePath);

        var end = Regex.Match(rules[(match.Index + 1)..], @"- alert: |^  #", RegexOptions.Multiline);
        return end.Success ? rules.Substring(match.Index, end.Index + 1) : rules[match.Index..];
    }
}
