using System.Net;
using AgentForge.Api.Health;
using AgentForge.Retrieval;
using AgentForge.Retrieval.Cohere;
using AgentForge.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class RerankerHealthCheckTests
{
    private static IOptions<ReadinessOptions> Readiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(2) });

    private static IOptions<ReadinessOptions> ShortReadiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromMilliseconds(50) });

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ShortProbeBudgetUpperBound = TimeSpan.FromSeconds(1);

    private static RerankerHealthCheck CreateSut(
        HttpMessageHandler handler, CohereOptions? options = null, IOptions<ReadinessOptions>? readiness = null)
    {
        var cohereOptions = Options.Create(options ?? new CohereOptions { ApiKey = "test-key", BaseUrl = "http://cohere.local" });
        var authenticated = new CohereAuthHandler(cohereOptions) { InnerHandler = handler };
        return new RerankerHealthCheck(new HttpClient(authenticated), cohereOptions, readiness ?? Readiness);
    }

    [Fact]
    public async Task CheckHealthAsync_ApiKeyNotConfigured_ReturnsDegradedRatherThanUnconditionalPass()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new RerankerHealthCheck(new HttpClient(handler), Options.Create(new CohereOptions()), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
        handler.LastRequest.Should().BeNull("an unconfigured reranker must never be contacted");
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredAndReachable_ReturnsHealthy()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = CreateSut(handler);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://cohere.local/v1/models");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("test-key");
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredAndUnreachable_ReturnsUnhealthyRatherThanPropagating()
    {
        var handler = new CapturingHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var sut = CreateSut(handler);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredAndRespondsWithAnErrorStatus_ReturnsUnhealthy()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var sut = CreateSut(handler, new CohereOptions { ApiKey = "bad-key", BaseUrl = "http://cohere.local" });

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ConfiguredAndNeverAnswers_GivesUpWithinTheProbeBudget()
    {
        var sut = CreateSut(new StallingHttpMessageHandler(), readiness: ShortReadiness);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var probe = sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var finished = await Task.WhenAny(probe, Task.Delay(Deadline));
        var elapsed = stopwatch.Elapsed;

        finished.Should().BeSameAs(probe,
            "a silent dependency must not hold /ready open past the probe budget it was given");
        elapsed.Should().BeLessThan(Deadline);
        elapsed.Should().BeLessThan(ShortProbeBudgetUpperBound);
        (await probe).Status.Should().Be(HealthStatus.Unhealthy);
    }
}
