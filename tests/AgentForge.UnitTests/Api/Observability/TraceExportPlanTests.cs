using AgentForge.Api.Health;
using AgentForge.Api.Observability;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// Which trace exporters the sidecar registers, decided from configuration alone. The OTLP exporter
/// exists only where an environment names a trace backend - staging with Tempo, never production ('s
/// disposition) - and the console exporter only where someone asked for it, because console spans reach
/// the platform's log retention with every URL in them.
/// </summary>
public sealed class TraceExportPlanTests
{
    private static TraceExportPlan PlanFor(Dictionary<string, string?> settings)
    {
        var options = new ConfigurationBuilder().AddInMemoryCollection(settings).Build()
            .GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        return TraceExportPlan.From(options);
    }

    [Fact]
    public void From_NothingConfigured_RegistersNoExporterAtAll()
    {
        // Production's position: no backend, no console spans, and nothing to warn about.
        var plan = PlanFor([]);

        plan.OtlpEndpoint.Should().BeNull();
        plan.ConsoleExporter.Should().BeFalse();
        plan.EndpointRejected.Should().BeFalse();
    }

    [Fact]
    public void From_TraceEndpointConfigured_ExportsToExactlyThatUrl()
    {
        var plan = PlanFor(new() { ["Observability:TraceOtlpEndpoint"] = "http://tempo:4318/v1/traces" });

        plan.OtlpEndpoint.Should().Be(new Uri("http://tempo:4318/v1/traces"));
        plan.EndpointRejected.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://:4318/v1/traces")]
    [InlineData("tempo:4318")]
    [InlineData("/v1/traces")]
    [InlineData("ftp://tempo/v1/traces")]
    public void From_TraceEndpointMalformed_RegistersNoExporterAndSaysSoInsteadOfFailingStartup(string value)
    {
        // Regression shape of the note on the Loki exporter: `new Uri(...)` on an unresolved Railway
        // reference threw at startup and the sidecar did not boot. Optional infra must fail open.
        var plan = PlanFor(new() { ["Observability:TraceOtlpEndpoint"] = value });

        plan.OtlpEndpoint.Should().BeNull();
        plan.EndpointRejected.Should().BeTrue();
    }

    [Fact]
    public void From_TraceEndpointBlank_IsTreatedAsUnset()
    {
        var plan = PlanFor(new() { ["Observability:TraceOtlpEndpoint"] = "  " });

        plan.OtlpEndpoint.Should().BeNull();
        plan.EndpointRejected.Should().BeFalse();
    }

    [Fact]
    public void From_ConsoleExporterRequested_RegistersIt()
    {
        var plan = PlanFor(new() { ["Observability:TraceConsoleExporter"] = "true" });

        plan.ConsoleExporter.Should().BeTrue();
    }
}
