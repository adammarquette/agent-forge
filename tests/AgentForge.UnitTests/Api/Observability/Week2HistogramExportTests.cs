using System.Globalization;
using System.Text.RegularExpressions;
using AgentForge.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The third side of each Week 2 bucket pin: that the explicit boundaries reach the
/// EXPORTED series, not only the constant. An <c>AddView</c> binds by instrument name, so renaming the
/// instrument or mistyping the view leaves every constant-level test green while <c>/metrics</c> silently
/// reverts to OpenTelemetry's defaults. This boots the real <c>Program</c>, records through the host's own
/// <see cref="IAgentForgeMetrics"/>, scrapes its <c>/metrics</c>, and requires the published <c>le</c> set to
/// be exactly the declared boundaries - no default boundary surviving.
/// </summary>
public sealed class Week2HistogramExportTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["OpenEmr:BaseUrl"] = "https://openemr.histogram-export-test.invalid",
            ["OpenEmr:Site"] = "default",
            ["OpenEmr:ClientId"] = "histogram-export-test",
            ["OpenEmr:Scopes:0"] = "launch",
            ["Bff:PublicBaseUrl"] = "https://bff.histogram-export-test.invalid",
            ["Llm:ApiKey"] = "histogram-export-test",
            ["Llm:Model"] = "histogram-export-test",
            ["Llm:InputPricePerMillionTokensUsd"] = "0",
            ["Llm:OutputPricePerMillionTokensUsd"] = "0",
        })
        {
            builder.UseSetting(key, value);
        }
    });

    [Theory]
    [InlineData("agentforge_evidence_retrieval_duration_seconds_bucket")]
    [InlineData("agentforge_document_ingestion_duration_seconds_bucket")]
    public async Task Scrape_AfterARecording_PublishesExactlyTheDeclaredBoundaries(string series)
    {
        var client = _factory.CreateClient();
        var metrics = _factory.Services.GetRequiredService<IAgentForgeMetrics>();
        metrics.RecordEvidenceRetrieval(true, 3, TimeSpan.FromSeconds(6.5), EvidenceRetrievalEntryPoint.EvidenceAsk);
        metrics.RecordDocumentIngestion("ingested", TimeSpan.FromSeconds(10.5));

        var body = await client.GetStringAsync(new Uri("/metrics", UriKind.Relative));

        var published = Regex.Matches(body, $@"^{series}\{{[^}}]*\ble=""(?<le>[^""]+)""", RegexOptions.Multiline)
            .Select(m => m.Groups["le"].Value)
            .Where(le => le != "+Inf")
            .Select(le => double.Parse(le, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

        published.Should().NotBeEmpty("{0} must be exported once a value is recorded", series);
        published.Should().Equal(DeclaredBoundaries(series),
            "the view must bind {0}'s explicit boundaries - an extra value here is an OpenTelemetry default surviving", series);
    }

    public void Dispose() => _factory.Dispose();

    private static IReadOnlyList<double> DeclaredBoundaries(string series) => series switch
    {
        "agentforge_evidence_retrieval_duration_seconds_bucket" => AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds,
        "agentforge_document_ingestion_duration_seconds_bucket" => AgentForgeMetrics.DocumentIngestionDurationBucketBoundariesSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(series), series, null),
    };
}
