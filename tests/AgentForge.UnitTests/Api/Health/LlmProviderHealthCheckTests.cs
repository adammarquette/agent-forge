using System.Diagnostics;
using System.Net;
using FluentAssertions;
using AgentForge.Api.Health;
using AgentForge.Llm;
using AgentForge.Llm.Anthropic;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

public sealed class LlmProviderHealthCheckTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private const string ApiKey = "test-key-do-not-leak";
    private const string BaseUrl = "https://api.anthropic.example";

    private readonly LlmProviderOptions _options = new()
    {
        ApiKey = ApiKey,
        Model = "test-model",
        BaseUrl = BaseUrl,
        InputPricePerMillionTokensUsd = 0,
        OutputPricePerMillionTokensUsd = 0,
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

    /// <summary>
    /// The check as the host wires it: the same <see cref="AnthropicAuthHandler"/> every model call
    /// goes through, in front of a canned provider.
    /// </summary>
    private LlmProviderHealthCheck CreateSut(
        HttpMessageHandler provider, LlmProviderOptions? options = null, IOptions<ReadinessOptions>? readiness = null)
    {
        var llmOptions = Options.Create(options ?? _options);
        var authenticated = new AnthropicAuthHandler(llmOptions) { InnerHandler = provider };
        return new LlmProviderHealthCheck(new HttpClient(authenticated), llmOptions, readiness ?? Readiness);
    }

    private static CapturingHttpMessageHandler Responding(HttpStatusCode status) =>
        new(_ => new HttpResponseMessage(status));

    [Fact]
    public async Task CheckHealthAsync_ProviderKnowsTheConfiguredModel_ReturnsHealthy()
    {
        var sut = CreateSut(Responding(HttpStatusCode.OK));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Theory]
    [InlineData("https://api.anthropic.example")]
    [InlineData("https://api.anthropic.example/")]
    public async Task CheckHealthAsync_Always_AsksForTheConfiguredModelWithTheConfiguredKey(string baseUrl)
    {
        // Regression guard: the probe used to GET the bare base URL, which the real
        // provider answers 404 on even when correctly configured, so no status it returned could
        // tell a working deployment from a broken one. The model lookup is authenticated and
        // token-free, and answers 200 only when the base URL, the key and the model all resolve.
        var provider = Responding(HttpStatusCode.OK);
        var sut = CreateSut(provider, new LlmProviderOptions
        {
            ApiKey = ApiKey,
            Model = "test-model",
            BaseUrl = baseUrl,
            InputPricePerMillionTokensUsd = 0,
            OutputPricePerMillionTokensUsd = 0,
        });

        await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        var request = provider.LastRequest!;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Be("https://api.anthropic.example/v1/models/test-model");
        request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
        request.Headers.Contains("anthropic-version").Should().BeTrue();
    }

    [Fact]
    public async Task CheckHealthAsync_ModelIdContainsReservedCharacters_EscapesItAsOnePathSegment()
    {
        // A model id is configuration, not a path: a '/' in it must not address a different route.
        var provider = Responding(HttpStatusCode.OK);
        var sut = CreateSut(provider, new LlmProviderOptions
        {
            ApiKey = ApiKey,
            Model = "vendor/model name",
            BaseUrl = BaseUrl,
            InputPricePerMillionTokensUsd = 0,
            OutputPricePerMillionTokensUsd = 0,
        });

        await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        provider.LastRequest!.RequestUri!.AbsoluteUri
            .Should().Be("https://api.anthropic.example/v1/models/vendor%2Fmodel%20name");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData((HttpStatusCode)529)]
    public async Task CheckHealthAsync_ProviderAnswersWithAFailureStatus_ReturnsUnhealthy(HttpStatusCode status)
    {
        // a configured dependency that answers but cannot serve is Unhealthy (ARCHITECTURE.md
        // D17). 401/403 is a rejected key, 404 a wrong base URL or unknown model, 5xx/529 a provider
        // outage - every clinical turn fails in each case, so /ready must not say otherwise.
        var sut = CreateSut(Responding(status));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task CheckHealthAsync_ProviderIsRateLimiting_ReturnsDegradedNotHealthyNorUnhealthy()
    {
        // A 429 comes after authentication and routing succeeded: the key, URL and model are proven,
        // and the provider is throttling an account every instance shares - so shedding this instance
        // relieves nothing, but calling it Healthy would hide the throttle. D17 records the ruling.
        var sut = CreateSut(Responding(HttpStatusCode.TooManyRequests));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CheckHealthAsync_AnyAnswer_DescriptionNamesNeitherTheKeyNorTheEndpoint(HttpStatusCode status)
    {
        // /ready's body is served to unauthenticated callers.
        var sut = CreateSut(Responding(status));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Description.Should().NotContain(ApiKey).And.NotContain("anthropic.example");
    }

    [Fact]
    public async Task CheckHealthAsync_RequestThrows_ReturnsUnhealthyRatherThanPropagating()
    {
        var sut = CreateSut(new CapturingHttpMessageHandler(_ => throw new HttpRequestException("connection refused")));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ProviderAcceptsTheConnectionAndNeverAnswers_GivesUpWithinTheProbeBudget()
    {
        // The third instance of the same class of defect: every /ready probe inherited
        // HttpClient.Timeout's 100s default, so any one silent dependency could hold the endpoint
        // open far past any readiness deadline.
        var sut = CreateSut(new StallingHttpMessageHandler(), readiness: ShortReadiness);

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
