using System.Net;
using AgentForge.Api.Health;
using AgentForge.Retrieval;
using AgentForge.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class RerankerHealthCheckRegistrationTests
{
    private const string ApiKey = "test-key-registration";

    [Fact]
    public async Task AddRerankerHealthCheckClient_ResolvedFromTheContainer_SendsTheConfiguredKeyOnTheProbe()
    {
        // The host's own registration, not a handler the test assembles: without CohereAuthHandler on
        // the typed client the probe goes out keyless and reads 401.
        var provider = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new CohereOptions
        {
            ApiKey = ApiKey,
            BaseUrl = "http://cohere.local",
        }));
        services.AddSingleton(Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(5) }));
        services.AddRerankerHealthCheckClient();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => provider));

        using var container = services.BuildServiceProvider();
        var sut = container.GetRequiredService<RerankerHealthCheck>();

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        provider.LastRequest!.Headers.Authorization!.Parameter.Should().Be(ApiKey);
    }
}
