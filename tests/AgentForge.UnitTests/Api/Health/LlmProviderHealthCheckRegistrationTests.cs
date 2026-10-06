using System.Net;
using FluentAssertions;
using AgentForge.Api.Health;
using AgentForge.Llm;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class LlmProviderHealthCheckRegistrationTests
{
    private const string ApiKey = "test-key-registration";

    [Fact]
    public async Task AddLlmProviderHealthCheckClient_ResolvedFromTheContainer_SendsTheConfiguredKeyOnTheProbe()
    {
        // The host's own registration, not a handler the test assembles: without the auth handler
        // on the typed client the probe goes out keyless and reads 401.
        var provider = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new LlmProviderOptions
        {
            ApiKey = ApiKey,
            Model = "test-model",
            BaseUrl = "https://api.anthropic.example",
            InputPricePerMillionTokensUsd = 0,
            OutputPricePerMillionTokensUsd = 0,
        }));
        services.AddSingleton(Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(5) }));
        services.AddLlmProviderHealthCheckClient();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => provider));

        using var container = services.BuildServiceProvider();
        var sut = container.GetRequiredService<LlmProviderHealthCheck>();

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        provider.LastRequest!.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
        provider.LastRequest.Headers.Contains("anthropic-version").Should().BeTrue();
    }
}
