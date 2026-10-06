using System.Text.Json;
using AgentForge.Api.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// NFR-HEALTH-W2-1's *"naming the unavailable dependency"*: before a separate change `/ready` used the default
/// response writer, so the body was the single word `Unhealthy` and an operator had to read the logs to
/// learn which dependency was down.
/// </summary>
public sealed class ReadinessResponseTests
{
    private static HealthReport Report(params (string Name, HealthStatus Status, string Description)[] entries) =>
        new(
            entries.ToDictionary(
                e => e.Name,
                e => new HealthReportEntry(e.Status, e.Description, TimeSpan.Zero, exception: null, data: null)),
            TimeSpan.Zero);

    [Fact]
    public void Serialize_OneDependencyDown_NamesItAndItsStatusInTheBody()
    {
        var json = JsonDocument.Parse(ReadinessResponse.Serialize(Report(
            ("openemr", HealthStatus.Healthy, "OpenEMR FHIR SMART discovery document reachable."),
            ("vector-index", HealthStatus.Unhealthy, "Vector index (Postgres/pgvector) unreachable."))));

        json.RootElement.GetProperty("status").GetString().Should().Be("Unhealthy");
        var down = json.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("status").GetString() == "Unhealthy");
        down.GetProperty("name").GetString().Should().Be("vector-index");
        down.GetProperty("description").GetString().Should().Contain("Vector index");
    }

    [Fact]
    public void Serialize_EveryDependencyUp_ReportsHealthyAndStillListsEachCheck()
    {
        // The paired direction: the body has to stay readable when nothing is wrong, or an operator
        // learns to ignore it.
        var json = JsonDocument.Parse(ReadinessResponse.Serialize(Report(
            ("openemr", HealthStatus.Healthy, "reachable"),
            ("vector-index", HealthStatus.Healthy, "reachable"))));

        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        json.RootElement.GetProperty("checks").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Serialize_Always_OrdersChecksByNameSoTheBodyIsStable()
    {
        // HealthReport.Entries is a dictionary; an unordered body makes diffing two probes useless.
        var json = JsonDocument.Parse(ReadinessResponse.Serialize(Report(
            ("vector-index", HealthStatus.Healthy, "reachable"),
            ("llm-provider", HealthStatus.Healthy, "reachable"),
            ("openemr", HealthStatus.Healthy, "reachable"))));

        json.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .Should().ContainInOrder("llm-provider", "openemr", "vector-index");
    }

    [Fact]
    public void Serialize_CheckFailedWithAnException_OmitsTheExceptionFromTheBody()
    {
        // /ready is unauthenticated. An Npgsql failure can carry the host, port and connection-string
        // keywords it was configured with, so the body carries only what the check chose to say.
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["vector-index"] = new(
                    HealthStatus.Unhealthy,
                    "Vector index (Postgres/pgvector) unreachable.",
                    TimeSpan.Zero,
                    new InvalidOperationException("Host=db;Username=agentforge;Password=hunter2"),
                    data: null),
            },
            TimeSpan.Zero);

        var body = ReadinessResponse.Serialize(report);

        body.Should().NotContain("hunter2").And.NotContain("Password");
    }
}
