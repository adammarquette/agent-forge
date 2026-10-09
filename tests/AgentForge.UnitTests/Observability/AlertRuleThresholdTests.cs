using System.Globalization;
using System.Text.RegularExpressions;
using AgentForge.Observability;
using FluentAssertions;

namespace AgentForge.UnitTests.Observability;

/// <summary>
/// Makes each latency alert's threshold pin against its histogram's explicit bucket boundaries two-way:
/// <c>AgentForgeHighTurnLatencyP95</c>'s <c>26</c> against
/// <see cref="AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds"/> (<c>a separate change</c>), and
/// <c>AgentForgeHighEvidenceRetrievalLatencyP95</c>'s <c>6</c> against
/// <see cref="AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds"/> (<c>a separate change</c>). Also
/// pins <c>AgentForgeHighTurnLatencyP95</c>'s <c>{turn_type="brief"}</c> filter, off the rule text itself
/// (<c>a separate change</c>).
/// <para>
/// <c>AgentForgeMetricsTests</c> asserts each boundary list contains its literal target, which catches a drop
/// on the <em>instrument</em> side only. This reads the rule as shipped, so editing a threshold off a boundary
/// is red too - a threshold sitting mid-bucket puts <c>histogram_quantile</c> back to interpolating across
/// the band at exactly the point being tested. <c>Week2HistogramExportTests</c> is the third side: that the
/// boundaries actually reach the exported series.
/// </para>
/// <para>
/// <c>AgentTurnBriefHistogramExportTests</c> (<c>a separate change</c>) pins that a <c>Brief</c> turn is exported
/// labeled <c>turn_type="brief"</c> - the wire name a selector reading that literal string would find. It
/// never reads <c>agentforge-alerts.yml</c>, so it cannot catch the filter being edited or deleted from the
/// rule's own <c>expr</c>: deleting <c>{turn_type="brief"}</c> from the rule left that test, and the rest of
/// the unit tier, green. Only reading the rule text, as this test does, catches it.
/// </para>
/// </summary>
public sealed class AlertRuleThresholdTests
{
    private const string AlertRuleRelativePath = "observability/alerts/agentforge-alerts.yml";

    [Theory]
    [InlineData("AgentForgeHighTurnLatencyP95", "agentforge_agent_turn_duration_seconds_bucket", "{turn_type=\"brief\"}")]
    [InlineData("AgentForgeHighEvidenceRetrievalLatencyP95", "agentforge_evidence_retrieval_duration_seconds_bucket", "")]
    public void LatencyAlert_AsWrittenInTheRuleFile_ComparesAgainstAnExplicitBucketBoundary(string alertName, string expectedSeries, string expectedFilter)
    {
        var expr = ReadRuleExpression(alertName);

        expr.Should().Contain(expectedSeries,
            "the boundaries in AgentForgeMetrics only govern the quantile if {0} is the series {1} reads", expectedSeries, alertName);

        if (!string.IsNullOrEmpty(expectedFilter))
        {
            // Joined with no gap, exactly as PromQL requires a label selector to sit immediately after the
            // metric name - checking for the two together, not expectedSeries alone, is what makes the
            // filter itself a pin.
            expr.Should().Contain(expectedSeries + expectedFilter,
                "{0} must filter to {1} - without it the rule evaluates the unfiltered series, dominated by " +
                "short Daily Agenda and follow-up turns, and NFR-PERF-1's budgeted population is no longer " +
                "what it fires on", alertName, expectedFilter);
        }

        // Anchored on the closing paren of histogram_quantile(...), and read from `expr` alone, so the
        // prose in `description` cannot satisfy the match. No match fails below rather than passing vacuously.
        var comparison = Regex.Matches(expr, @"\)\s*>\s*(?<threshold>\d+(?:\.\d+)?)");

        comparison.Should().HaveCount(1,
            "{0}'s expr must contain exactly one parseable threshold comparison for this guard to mean anything", alertName);

        var threshold = double.Parse(comparison[0].Groups["threshold"].Value, CultureInfo.InvariantCulture);

        BoundariesFor(expectedSeries).Should().Contain(threshold,
            "{0} fires at > {1}, and a threshold that is not itself a bucket boundary is resolved by " +
            "interpolating across the band it falls in", alertName, threshold);
    }

    private static IReadOnlyList<double> BoundariesFor(string series) => series switch
    {
        "agentforge_agent_turn_duration_seconds_bucket" => AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds,
        "agentforge_evidence_retrieval_duration_seconds_bucket" => AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
    };

    /// <summary>
    /// Returns the <c>expr</c> of the named rule only: from its <c>- alert:</c> line to the next one or EOF,
    /// then from <c>expr:</c> to the next key or comment at the rule's own indentation, so neither another
    /// rule's threshold nor a number in this rule's comments or annotations can be read as its threshold.
    /// </summary>
    private static string ReadRuleExpression(string alertName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root is found by walking up to AgentForge.slnx");
        var path = Path.Combine(directory!.FullName, AlertRuleRelativePath);
        File.Exists(path).Should().BeTrue("{0} is the rule file this test guards", path);

        var rules = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = IndexOfAlertDeclaration(rules, alertName);
        start.Should().BeGreaterThanOrEqualTo(0, "{0} must still exist in {1}", alertName, AlertRuleRelativePath);

        var next = rules.IndexOf("- alert: ", start + 1, StringComparison.Ordinal);
        var block = next < 0 ? rules[start..] : rules[start..next];

        var expr = Regex.Match(block, @"^(?<indent> +)expr:(?<body>.*?)(?=^\k<indent>\S|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        expr.Success.Should().BeTrue("{0} must declare an expr", alertName);
        return expr.Groups["body"].Value;
    }

    /// <summary>
    /// Index of the <c>- alert: </c> line declaring <paramref name="alertName"/>, or <c>-1</c>. The name has
    /// to end the line: a bare <c>IndexOf</c> also matched a rule whose name merely <em>starts</em> with this
    /// one, so renaming the alert to <c>AgentForgeHighTurnLatencyP95Brief</c> left this guard green over a
    /// rule that no longer existed.
    /// </summary>
    private static int IndexOfAlertDeclaration(string rules, string alertName)
    {
        var declaration = $"- alert: {alertName}";
        for (var i = rules.IndexOf(declaration, StringComparison.Ordinal);
             i >= 0;
             i = rules.IndexOf(declaration, i + 1, StringComparison.Ordinal))
        {
            var after = i + declaration.Length;
            if (after >= rules.Length || rules[after] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
