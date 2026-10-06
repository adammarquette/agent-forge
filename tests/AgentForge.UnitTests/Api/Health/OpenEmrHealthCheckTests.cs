using System.Diagnostics;
using System.Net;
using FluentAssertions;
using AgentForge.Api.Health;
using AgentForge.Integration.OpenEmr;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class OpenEmrHealthCheckTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly OpenEmrOptions _options = new()
    {
        BaseUrl = "https://emr.example.org",
        Site = "default",
        ClientId = "client-1",
        Scopes = ["patient/patient.read"],
    };

    // Generous on purpose: a cold CI runner's first HttpClient use (JIT, DNS, socket
    // warm-up) can outrun a tight budget even though the mocked handler answers instantly. Only the
    // test that asserts the bound itself fires needs a short one - see ShortReadiness below.
    private static IOptions<ReadinessOptions> Readiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(2) });

    private static IOptions<ReadinessOptions> ShortReadiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = TimeSpan.FromMilliseconds(50) });

    // Tighter than Deadline on purpose: a mutant that ignores the injected
    // ReadinessOptions and hardcodes the 2s production default still finishes inside Deadline (10s),
    // so Deadline alone cannot prove ShortReadiness's 50ms budget was the one actually honoured.
    private static readonly TimeSpan ShortProbeBudgetUpperBound = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task CheckHealthAsync_SmartDiscoveryReachable_ReturnsHealthy()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new OpenEmrHealthCheck(new HttpClient(handler), Options.Create(_options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Probes_TheSmartDiscoveryDocumentNotTheCapabilityStatement()
    {
        // Regression: /fhir/metadata builds the CapabilityStatement per request and measured 1.95-7.55s
        // in production and 4.65-8.8s in staging, so a 2s budget read every environment Unhealthy.
        // The SMART discovery document rides the same FHIR route table and auth skip-list at 0.3-1.8s
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sut = new OpenEmrHealthCheck(new HttpClient(handler), Options.Create(_options), Readiness);

        await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://emr.example.org/apis/default/fhir/.well-known/smart-configuration");
    }

    [Fact]
    public async Task CheckHealthAsync_SmartDiscoveryReturnsServerError_ReturnsUnhealthy()
    {
        var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var sut = new OpenEmrHealthCheck(new HttpClient(handler), Options.Create(_options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_RequestThrows_ReturnsUnhealthyRatherThanPropagating()
    {
        // NFR-HEALTH-1: readiness must genuinely fail when the dependency is unreachable, not
        // return 200 unconditionally - and not crash the /ready endpoint either.
        var handler = new CapturingHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var sut = new OpenEmrHealthCheck(new HttpClient(handler), Options.Create(_options), Readiness);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_OpenEmrAcceptsTheConnectionAndNeverAnswers_GivesUpWithinTheProbeBudget()
    {
        // Same failure mode as the observability probe, same unbounded default: an unreachable
        // dependency must not stall readiness past its budget. OpenEMR is product-blocking, so here
        // the bounded answer is Unhealthy rather than Degraded.
        var sut = new OpenEmrHealthCheck(
            new HttpClient(new StallingHttpMessageHandler()), Options.Create(_options), ShortReadiness);

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
