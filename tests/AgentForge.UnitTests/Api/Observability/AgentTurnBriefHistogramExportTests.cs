using System.Globalization;
using System.Text.RegularExpressions;
using AgentForge.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// Applies <c>Week2HistogramExportTests</c>' pattern to the histogram a separate change is about:
/// <c>agentforge.agent_turn.duration</c>. <c>AgentForgeMetricsTests</c> and <c>AlertRuleThresholdTests</c>
/// both pin <see cref="AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds"/> as a <b>constant</b> -
/// neither reaches the <c>AddView</c> registration in <c>Program.cs</c>, so a renamed or mistyped
/// <c>instrumentName</c> there leaves both green while <c>/metrics</c> silently reverts to OpenTelemetry's
/// defaults. This boots the real host, records a <see cref="AgentTurnType.Brief"/> turn through
/// <see cref="IAgentForgeMetrics"/>, scrapes <c>/metrics</c>, and requires the exported <c>le</c> set
/// labeled <c>turn_type="brief"</c> to be exactly the declared boundaries. A wire-name drift on
/// <see cref="AgentTurnType.Brief"/> (<c>AgentTurnTypeExtensions.ToWireName</c>) is pinned the same way:
/// the regex below matches the literal label value, not merely "some turn_type", so a rename that still
/// exports SOME label leaves this red if it is not "brief".
/// <para>
/// <b>This test does not read <c>agentforge-alerts.yml</c> and does not pin
/// <c>AgentForgeHighTurnLatencyP95</c>'s <c>{turn_type="brief"}</c> selector.</b> Every bucket boundary in
/// <c>AddView</c> applies uniformly across every label value on the same instrument, so widening the regex
/// below to also match <c>turn_type="agenda"</c> would still pass here: <c>.Distinct()</c> merges the
/// identical boundaries from both series into the same set. The recorded
/// <see cref="AgentTurnType.Agenda"/> turn therefore proves nothing about the filter and is not a guard
/// against that widening - it is present only so the exported <c>/metrics</c> body has more than one
/// <c>turn_type</c> series in it, closer to the shape a real host produces.
/// <c>AlertRuleThresholdTests</c> is what pins the rule's own filter, off the rule text (<c>a separate change</c>).
/// </para>
/// </summary>
public sealed class AgentTurnBriefHistogramExportTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["OpenEmr:BaseUrl"] = "https://openemr.turn-brief-histogram-export-test.invalid",
            ["OpenEmr:Site"] = "default",
            ["OpenEmr:ClientId"] = "turn-brief-histogram-export-test",
            ["OpenEmr:Scopes:0"] = "launch",
            ["Bff:PublicBaseUrl"] = "https://bff.turn-brief-histogram-export-test.invalid",
            ["Llm:ApiKey"] = "turn-brief-histogram-export-test",
            ["Llm:Model"] = "turn-brief-histogram-export-test",
            ["Llm:InputPricePerMillionTokensUsd"] = "0",
            ["Llm:OutputPricePerMillionTokensUsd"] = "0",
        })
        {
            builder.UseSetting(key, value);
        }
    });

    [Fact]
    public async Task Scrape_AfterABriefTurn_PublishesExactlyTheDeclaredBoundariesForTheBriefSeries()
    {
        var client = _factory.CreateClient();
        var metrics = _factory.Services.GetRequiredService<IAgentForgeMetrics>();
        metrics.RecordAgentTurn(AgentTurnType.Brief, succeeded: true, TimeSpan.FromSeconds(27));
        // NOT a guard and proves nothing about the {turn_type="brief"} filter (see class doc comment) -
        // every label shares the same AddView boundaries, and .Distinct() below would merge them even if
        // the regex matched both. Present only so /metrics carries more than one turn_type series.
        metrics.RecordAgentTurn(AgentTurnType.Agenda, succeeded: true, TimeSpan.FromSeconds(1));

        var body = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative));

        var published = Regex.Matches(
                body,
                @"^agentforge_agent_turn_duration_seconds_bucket\{(?=[^}]*\bturn_type=""brief"")[^}]*\ble=""(?<le>[^""]+)""",
                RegexOptions.Multiline)
            .Select(m => m.Groups["le"].Value)
            .Where(le => le != "+Inf")
            .Select(le => double.Parse(le, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

        published.Should().NotBeEmpty(
            "the {{turn_type=\"brief\"}} series must be exported once a brief turn is recorded - an empty " +
            "result here means the wire name AgentForgeHighTurnLatencyP95 filters on is not what gets published");
        published.Should().Equal(
            AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds,
            "the AddView in Program.cs must bind agentforge.agent_turn.duration's explicit boundaries for " +
            "the brief series - an extra value here is an OpenTelemetry default surviving");
    }

    public void Dispose() => _factory.Dispose();
}
