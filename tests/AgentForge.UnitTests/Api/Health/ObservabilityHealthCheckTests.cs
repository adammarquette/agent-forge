using System.Diagnostics;
using System.Net;
using FluentAssertions;
using AgentForge.Api.Health;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class ObservabilityHealthCheckTests
{
    /// <summary>
    /// Generous next to <see cref="ShortProbeTimeout"/>: the assertion is "bounded", not "fast", so a
    /// loaded CI agent must not redden it. Without the probe budget the wait is 100 seconds
    /// (<see cref="HttpClient"/>'s default), so the gap this has to separate is enormous.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ShortProbeTimeout = TimeSpan.FromMilliseconds(50);

    // Generous on purpose: a cold CI runner's first HttpClient use (JIT, DNS, socket
    // warm-up) can outrun a tight budget even though the mocked handler answers instantly. Only the
    // test that asserts the bound itself fires (below) needs ShortProbeTimeout.
    private static IOptions<ReadinessOptions> Readiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(2) });

    private static IOptions<ReadinessOptions> ShortReadiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = ShortProbeTimeout });

    // Tighter than Deadline on purpose: a mutant that ignores the injected
    // ReadinessOptions and hardcodes the 2s production default still finishes inside Deadline (10s),
    // so Deadline alone cannot prove ShortProbeTimeout's 50ms budget was the one actually honoured.
    private static readonly TimeSpan ShortProbeBudgetUpperBound = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task CheckHealthAsync_PrometheusHealthUrlNotConfigured_ReturnsDegradedRatherThanUnconditionalPass()
    {
        // NFR-REL-2's "meaningful check, not an unconditional 200" - self-hosted observability is
        // optional infra (docker-compose), so an unconfigured URL is a real, distinct state from
        // "checked and confirmed reachable," not silently treated as healthy.
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new ObservabilityHealthCheck(
            new HttpClient(handler), Options.Create(new ObservabilityOptions()), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealthAsync_PrometheusReachable_ReturnsHealthy()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = new ObservabilityOptions { PrometheusHealthUrl = "http://prometheus.local/-/healthy" };
        var sut = new ObservabilityHealthCheck(new HttpClient(handler), Options.Create(options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://prometheus.local/-/healthy");
    }

    [Fact]
    public async Task CheckHealthAsync_PrometheusUnreachable_ReturnsUnhealthyRatherThanPropagating()
    {
        var handler = new CapturingHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var options = new ObservabilityOptions { PrometheusHealthUrl = "http://prometheus.local/-/healthy" };
        var sut = new ObservabilityHealthCheck(new HttpClient(handler), Options.Create(options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_PrometheusRespondsWithAnErrorStatus_ReturnsUnhealthy()
    {
        // NFR-REL-2 and NFR-HEALTH-1 name the observability backend among the dependencies /ready
        // must fail for, so a configured Prometheus answering 503 fails readiness rather than
        // downgrading it - only an UNCONFIGURED url is Degraded (ARCHITECTURE.md D17).
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var options = new ObservabilityOptions { PrometheusHealthUrl = "http://prometheus.local/-/healthy" };
        var sut = new ObservabilityHealthCheck(new HttpClient(handler), Options.Create(options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_PrometheusAcceptsTheConnectionAndNeverAnswers_GivesUpWithinTheProbeBudget()
    {
        // The failure mode this guards: an unreachable dependency stalls readiness past its budget.
        // Measured live on staging - Prometheus at prometheus.railway.internal:9090 absorbed the
        // connection and /ready answered after 100.33s, HttpClient.Timeout's default, by which time
        // any load balancer or uptime check has already called the service down.
        var sut = new ObservabilityHealthCheck(
            new HttpClient(new StallingHttpMessageHandler()),
            Options.Create(new ObservabilityOptions { PrometheusHealthUrl = "http://prometheus.local/-/healthy" }),
            ShortReadiness);

        var stopwatch = Stopwatch.StartNew();
        var probe = sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var finished = await Task.WhenAny(probe, Task.Delay(Deadline));
        // Captured before any assertion runs: FluentAssertions' own first-use cost on a cold
        // runner must not count against ShortProbeBudgetUpperBound.
        var elapsed = stopwatch.Elapsed;

        finished.Should().BeSameAs(probe,
            "a silent dependency must not hold /ready open past the probe budget it was given");
        elapsed.Should().BeLessThan(Deadline);
        elapsed.Should().BeLessThan(ShortProbeBudgetUpperBound,
            "the check must honour the configured ProbeTimeout, not fall back to a longer default");
        (await probe).Status.Should().Be(HealthStatus.Unhealthy);
    }
}
