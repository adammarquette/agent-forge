using System.Collections.Concurrent;
using System.Net;
using AgentForge.Api.Health;
using AgentForge.Integration.OpenEmr;
using AgentForge.Llm;
using AgentForge.Retrieval;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The host's own <c>/ready</c> registration, driven through <see cref="HealthCheckService"/> as the
/// endpoint drives it: which checks are cached is a claim about the wiring, not about the cache
/// </summary>
public sealed class ReadinessCheckRegistrationTests : IDisposable
{
    private const int Polls = 5;

    private readonly CountingHandler _dependencies = new();

    public void Dispose() => _dependencies.Dispose();

    private ServiceProvider BuildContainer(HttpStatusCode llmStatus = HttpStatusCode.OK)
    {
        _dependencies.LlmStatus = llmStatus;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new OpenEmrOptions
        {
            BaseUrl = "http://openemr.local",
            Site = "default",
            ClientId = "client",
            Scopes = ["openid"],
        }));
        services.AddSingleton(Options.Create(new LlmProviderOptions
        {
            ApiKey = "test-key-registration",
            Model = "test-model",
            BaseUrl = "http://llm.local",
            InputPricePerMillionTokensUsd = 0,
            OutputPricePerMillionTokensUsd = 0,
        }));
        services.AddSingleton(Options.Create(new CohereOptions { ApiKey = "test-cohere-key", BaseUrl = "http://cohere.local" }));
        services.AddSingleton(Options.Create(new ObservabilityOptions { PrometheusHealthUrl = "http://prometheus.local/-/healthy" }));
        services.AddSingleton(Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(5) }));
        services.AddReadinessChecks();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => _dependencies));
        return services.BuildServiceProvider();
    }

    private static async Task<HealthReport> PollAsync(ServiceProvider container, string check)
    {
        var health = container.GetRequiredService<HealthCheckService>();
        HealthReport report = null!;
        for (var i = 0; i < Polls; i++)
        {
            report = await health.CheckHealthAsync(r => r.Name == check, CancellationToken.None);
        }

        return report;
    }

    [Theory]
    [InlineData("llm-provider", "llm.local")]
    [InlineData("openemr", "openemr.local")]
    [InlineData("reranker", "cohere.local")]
    public async Task AddReadinessChecks_ExternalDependencyPolledRepeatedly_IsAskedOnce(string check, string host)
    {
        // The three checks that leave the deployment: a paid model provider, a paid reranker and the
        // clinic's own EMR. Each /ready call used to be one request to every one of them.
        using var container = BuildContainer();

        var report = await PollAsync(container, check);

        report.Entries[check].Status.Should().Be(HealthStatus.Healthy);
        _dependencies.RequestsTo(host).Should().Be(1);
    }

    [Fact]
    public async Task AddReadinessChecks_ObservabilityPolledRepeatedly_IsAskedEveryTime()
    {
        // The cache is scoped to the external checks on purpose: Prometheus sits on the deployment's
        // own network and costs nothing to ask, so its check stays live.
        using var container = BuildContainer();

        await PollAsync(container, "observability");

        _dependencies.RequestsTo("prometheus.local").Should().Be(Polls);
    }

    [Fact]
    public async Task AddReadinessChecks_ProviderAnswers429_StaysDegradedThroughTheCache()
    {
        // ARCHITECTURE.md D17: a provider 429 is Degraded (HTTP 200), not Unhealthy.
        using var container = BuildContainer(HttpStatusCode.TooManyRequests);

        var report = await PollAsync(container, "llm-provider");

        report.Entries["llm-provider"].Status.Should().Be(HealthStatus.Degraded);
        _dependencies.RequestsTo("llm.local").Should().Be(1);
    }

    [Fact]
    public async Task AddReadinessChecks_TwoExternalChecks_KeepSeparateCaches()
    {
        // One shared slot would answer the second check with the first check's result.
        using var container = BuildContainer(HttpStatusCode.Unauthorized);
        var health = container.GetRequiredService<HealthCheckService>();

        var llm = await health.CheckHealthAsync(r => r.Name == "llm-provider", CancellationToken.None);
        var openEmr = await health.CheckHealthAsync(r => r.Name == "openemr", CancellationToken.None);

        llm.Entries["llm-provider"].Status.Should().Be(HealthStatus.Unhealthy);
        openEmr.Entries["openemr"].Status.Should().Be(HealthStatus.Healthy);
        _dependencies.RequestsTo("openemr.local").Should().Be(1);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);

        public HttpStatusCode LlmStatus { get; set; } = HttpStatusCode.OK;

        public int RequestsTo(string host) => _requests.GetValueOrDefault(host);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            _requests.AddOrUpdate(host, 1, (_, n) => n + 1);
            var status = host == "llm.local" ? LlmStatus : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
