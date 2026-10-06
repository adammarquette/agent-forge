using System.Net;
using System.Text.Json;
using AgentForge.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Epic 10's acceptance criterion - "/ready fails when any one dependency is down while /health
/// still succeeds" - proven for the four checks this class exercises end-to-end: OpenEMR, the LLM
/// provider, observability, and the vector index; Program.cs also tags a fifth, reranker, whose
/// unhealthy/healthy/degraded cases are unit-tested. Each check exercised here gets an unhealthy case (503,
/// naming that check) and a healthy case (200). Nothing mocked (CONVENTIONS.md §8.2): the two unreachable-host cases,
/// OpenEMR's (<see cref="HealthEndpointQaFixture"/>) and the vector index's, point at a real,
/// deliberately unresolvable host and fail on a real connection attempt; the OpenEMR, LLM-provider
/// and observability peers of every other case are a real local HTTP server this test owns
/// (<see cref="StubHttpServer"/>) instead of the live third party, still a genuine network round
/// trip; only the healthy vector-index case hits a real Postgres
/// (<see cref="VectorIndexQaConnectionString"/>), which the background
/// <c>DataStoreStartupService</c> migrates for real once <see cref="ReadyProbeFactory"/> supplies the
/// connection string early enough for Program.cs's <c>weekTwoEnabled</c> read to see it - so that
/// case polls <c>/ready</c> rather than assuming migration is done by the time the host answers its
/// first request. A separate change narrowed this summary to the OpenEMR leg alone pending this widening
/// </summary>
[Trait("Metric", "M5-Degradation")]
public sealed class HealthEndpointReadinessTests : IClassFixture<HealthEndpointQaFixture>
{
    private readonly HealthEndpointQaFixture _fixture;

    public HealthEndpointReadinessTests(HealthEndpointQaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetHealth_OpenEmrDependencyUnreachable_StillReturnsOkSinceItIsLivenessOnly()
    {
        // /health checks nothing but "is the process up" (Program.cs's Predicate = _ => false) -
        // it must not flap just because a downstream dependency happens to be unreachable.
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetReady_OpenEmrDependencyUnreachable_ReturnsServiceUnavailableNamingOpenEmr()
    {
        // REQUIREMENTS.md §13.1's "Dependency down" row: /ready must fail readiness so traffic isn't routed
        // to an instance that can't actually serve OpenEMR-backed requests.
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().ContainCheck("openemr", "Unhealthy", description => description.Should().Contain("OpenEMR"));
    }

    [Fact]
    public async Task GetReady_OpenEmrReachable_ReturnsOk()
    {
        // The healthy counterpart to the case above: a reachable SMART discovery document is Healthy.
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        using var factory = new ReadyProbeFactory(HealthyBaselineConfiguration(openEmr, llm));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().ContainCheck("openemr", "Healthy");
    }

    [Fact]
    public async Task GetReady_LlmProviderRespondsServerError_ReturnsServiceUnavailableNamingLlmProvider()
    {
        // LlmProviderHealthCheck: any non-2xx, non-429 status from the model lookup is Unhealthy -
        // "model calls will fail" (LlmProviderHealthCheck.cs). The 500 comes from a stub this test
        // owns (StubHttpServer), not the live provider - a real HTTP round trip against a peer we
        // control instead of the third party (CONVENTIONS.md §8.2).
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status500InternalServerError);
        using var factory = new ReadyProbeFactory(HealthyBaselineConfiguration(openEmr, llm));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().ContainCheck("llm-provider", "Unhealthy", description => description.Should().Contain("LLM provider"));
    }

    [Fact]
    public async Task GetReady_LlmProviderAcceptsModelLookup_ReturnsOk()
    {
        // The healthy counterpart to the case above: a 2xx model lookup is Healthy.
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        using var factory = new ReadyProbeFactory(HealthyBaselineConfiguration(openEmr, llm));

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().ContainCheck("llm-provider", "Healthy");
    }

    [Fact]
    public async Task GetReady_ObservabilityBackendRespondsServerError_ReturnsServiceUnavailableNamingObservability()
    {
        // ObservabilityHealthCheck: once Observability:PrometheusHealthUrl is configured, anything
        // but a success status is Unhealthy - "setting it is a commitment" (DEPLOYMENT.md).
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        using var observability = new StubHttpServer(StatusCodes.Status500InternalServerError);
        var configuration = new Dictionary<string, string?>(HealthyBaselineConfiguration(openEmr, llm))
        {
            ["Observability:PrometheusHealthUrl"] = observability.BaseUrl,
        };
        using var factory = new ReadyProbeFactory(configuration);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().ContainCheck("observability", "Unhealthy", description => description.Should().Contain("Prometheus"));
    }

    [Fact]
    public async Task GetReady_ObservabilityBackendReachable_ReturnsOk()
    {
        // The healthy counterpart to the case above: a configured, reachable Prometheus is Healthy.
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        using var observability = new StubHttpServer(StatusCodes.Status200OK);
        var configuration = new Dictionary<string, string?>(HealthyBaselineConfiguration(openEmr, llm))
        {
            ["Observability:PrometheusHealthUrl"] = observability.BaseUrl,
        };
        using var factory = new ReadyProbeFactory(configuration);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().ContainCheck("observability", "Healthy");
    }

    [Fact]
    public async Task GetReady_VectorIndexConnectionStringUnreachable_ReturnsServiceUnavailableNamingVectorIndex()
    {
        // VectorIndexHealthCheck: "configured and not usable" is Unhealthy, exactly like OpenEMR
        // (VectorIndexHealthCheck.cs remarks) - an unreachable host is the plainest case. The
        // connection string is a literal here, not VectorIndexQaConnectionString.Resolve(), and
        // ReadyProbeFactory supplies it via UseSetting - which wins over even an ambient
        // AgentForgeData__ConnectionString environment variable (confirmed empirically) - so this
        // case does not depend on what, if anything, the process environment sets for that variable.
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        var configuration = new Dictionary<string, string?>(HealthyBaselineConfiguration(openEmr, llm))
        {
            ["AgentForgeData:ConnectionString"] =
                "Host=vector-index-unreachable.agentforge-qa-fault-injection.invalid;Port=5432;Database=agentforge;Username=agentforge;Password=agentforge;Timeout=2",
        };
        using var factory = new ReadyProbeFactory(configuration);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ready");
        using var body = await ParseReadyBodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        // Pins the probe's own reading: the pending-migration branch would keep this Unhealthy even if
        // Read() stopped mapping an unreachable host to Unhealthy. Either wording of that one arm is
        // correct - the connection failure sometimes lands after the 2s budget expires.
        body.Should().ContainCheck("vector-index", "Unhealthy", description => description.Should().MatchRegex(
            UnreachableVectorIndexDescription,
            "an unreachable host is either refused outright or outlasts the probe budget, and both are the same Read() arm"));
    }

    [Fact]
    public async Task GetReady_VectorIndexAvailable_ReturnsOk()
    {
        // Available means the real QA Postgres answered with the vector extension, guideline_chunks
        // and its HNSW index all present. Program.cs's Week 2 wiring - including
        // DataStoreStartupService, the background hosted service that runs this build's migrations
        // - only turns on when weekTwoEnabled reads true, which needs the connection string
        // supplied early enough for Program.cs's own pre-Build() read to see it: ReadyProbeFactory
        // does this via UseSetting, since a ConfigureAppConfiguration override applies too late for
        // that read. The migration then runs asynchronously, so /ready can still answer 503 for a
        // request or two after the host starts serving, until it completes. Poll rather than assume.
        var connectionString = VectorIndexQaConnectionString.Resolve();
        using var openEmr = new StubHttpServer(StatusCodes.Status200OK);
        using var llm = new StubHttpServer(StatusCodes.Status200OK);
        var configuration = new Dictionary<string, string?>(HealthyBaselineConfiguration(openEmr, llm))
        {
            ["AgentForgeData:ConnectionString"] = connectionString,
        };
        using var factory = new ReadyProbeFactory(configuration);
        using var client = factory.CreateClient();

        HttpResponseMessage response;
        JsonDocument body;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            response = await client.GetAsync("/ready");
            body = await ParseReadyBodyAsync(response);
            if (response.StatusCode == HttpStatusCode.OK || DateTime.UtcNow >= deadline)
            {
                break;
            }

            body.Dispose();
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        using (response)
        using (body)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK, "this build's migrations should have completed well within the poll budget");
            body.Should().ContainCheck("vector-index", "Healthy");
        }
    }

    /// <summary>
    /// The two descriptions <c>VectorIndexHealthCheck.Read()</c>'s unreachable arm can give under the default
    /// 2s budget, anchored so any other wording - another arm's, or a reworded one - still fails.
    /// </summary>
    private const string UnreachableVectorIndexDescription =
        @"^Vector index \(Postgres/pgvector\) (unreachable|no answer within 2s)\. ";

    /// <summary>
    /// OpenEMR reachable (the stub answers every path 200) and the LLM provider reachable, so a test
    /// targeting a different check does not also fail on these two. Observability and the vector
    /// index are left unconfigured, which is <c>Degraded</c> - still HTTP 200 - rather than absent
    /// from the baseline (ObservabilityHealthCheck.cs / VectorIndexHealthCheck.cs remarks).
    /// </summary>
    private static Dictionary<string, string?> HealthyBaselineConfiguration(StubHttpServer openEmr, StubHttpServer llm) => new()
    {
        ["OpenEmr:BaseUrl"] = openEmr.BaseUrl,
        ["OpenEmr:Site"] = "default",
        ["OpenEmr:ClientId"] = "qa-integration-test-client",
        ["OpenEmr:Scopes:0"] = "patient/Patient.read",
        ["OpenEmr:AllowInsecureHttpForLocalDevelopment"] = "true",
        ["Bff:PublicBaseUrl"] = "https://bff-integration-test.invalid",
        ["Llm:ApiKey"] = "readiness-integration-test-key",
        ["Llm:Model"] = "readiness-integration-test-model",
        ["Llm:BaseUrl"] = llm.BaseUrl,
        ["Llm:InputPricePerMillionTokensUsd"] = "0",
        ["Llm:OutputPricePerMillionTokensUsd"] = "0",
        ["Cohere:ApiKey"] = string.Empty,
    };

    private static async Task<JsonDocument> ParseReadyBodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

/// <summary>
/// Assertions against <c>ReadinessResponse</c>'s <c>/ready</c> body shape
/// (<c>{"status": "...", "checks": [{"name", "status", "description"}, ...]}</c>) - by check name
/// rather than by one word, since the body exists precisely so a caller can tell dependencies apart
/// (CONVENTIONS.md §8.2).
/// </summary>
internal static class ReadyBodyAssertionExtensions
{
    public static ReadyBodyAssertions Should(this JsonDocument body) => new(body);
}

internal sealed class ReadyBodyAssertions(JsonDocument body)
{
    public void ContainCheck(string checkName, string expectedStatus, Action<string>? describe = null)
    {
        var checks = body.RootElement.GetProperty("checks");
        foreach (var check in checks.EnumerateArray())
        {
            if (check.GetProperty("name").GetString() != checkName)
            {
                continue;
            }

            check.GetProperty("status").GetString().Should().Be(expectedStatus, $"the '{checkName}' check should report {expectedStatus}");
            describe?.Invoke(check.GetProperty("description").GetString() ?? string.Empty);
            return;
        }

        throw new Xunit.Sdk.XunitException($"/ready body named no '{checkName}' check: {body.RootElement}");
    }
}
