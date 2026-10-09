using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Api.LlmProviders;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Llm.Anthropic;
using AgentForge.Llm.Gemini;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.LlmProviders;

/// <summary>
/// The host's own registration (<see cref="LlmProviderRegistration.AddLlmProvider"/>), so these pin what Program.cs
/// boots with rather than a container the test assembles.
/// </summary>
public sealed class LlmProviderRegistrationTests
{
    private static ServiceProvider Build(
        Dictionary<string, string?> llm, HttpMessageHandler? primary = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:ApiKey"] = "registration-test-key",
            ["Llm:Model"] = "test-model",
            ["Llm:InputPricePerMillionTokensUsd"] = "1",
            ["Llm:OutputPricePerMillionTokensUsd"] = "2",
        };
        foreach (var (key, value) in llm)
        {
            settings[$"Llm:{key}"] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var correlation = A.Fake<ICorrelationIdAccessor>();
        A.CallTo(() => correlation.CorrelationId).Returns("corr-registration");
        services.AddSingleton(correlation);
        services.AddLlmProvider(configuration);
        // Every client's primary handler is replaced, registered last so it runs after the Refit registration's
        // own builder actions: ConfigureHttpClientDefaults runs first and is overridden, which let a call out.
        // With no handler given, a request that escapes still goes nowhere routable.
        var terminal = primary ?? new CapturingHttpMessageHandler(_ => throw new InvalidOperationException("No network in unit tests."));
        services.ConfigureAll<HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = terminal));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void AddLlmProvider_NoProviderConfigured_ResolvesTheAnthropicProvider()
    {
        using var container = Build([]);
        using var scope = container.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILlmProvider>().Should().BeOfType<AnthropicLlmProvider>();
    }

    [Theory]
    [InlineData("Anthropic", typeof(AnthropicLlmProvider))]
    [InlineData("Gemini", typeof(GeminiLlmProvider))]
    [InlineData("gemini", typeof(GeminiLlmProvider))]
    public void AddLlmProvider_ProviderConfigured_ResolvesTheMatchingProvider(string provider, Type expected)
    {
        using var container = Build(new() { ["Provider"] = provider });
        using var scope = container.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILlmProvider>().Should().BeOfType(expected);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("7")]
    public void AddLlmProvider_UnknownProvider_FailsStartupValidationNamingTheOption(string provider)
    {
        // ValidateOnStart is the boot path: the host runs IStartupValidator before it serves anything, so an
        // unknown provider crash-loops loudly instead of booting into Anthropic with the wrong key.
        using var container = Build(new() { ["Provider"] = provider });

        var act = () => container.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<Exception>().Which.Message.Should().Contain("Provider");
    }

    [Fact]
    public async Task AddLlmProvider_GeminiConfigured_SendsTheCallToGeminiWithTheGoogKeyAndTheCorrelationId()
    {
        // The whole wired chain - auth handler, correlation id, the resilience handler and the default base URL -
        // against a canned body: proves the Gemini client is wired the way the Anthropic one is.
        var primary = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"candidates":[{"content":{"parts":[{"text":"ok"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var container = Build(new() { ["Provider"] = "Gemini" }, primary);
        using var scope = container.CreateScope();

        var response = await scope.ServiceProvider.GetRequiredService<ILlmProvider>()
            .CompleteAsync(new LlmRequest("system", [LlmMessage.FromText(LlmRole.User, "hi")]), CancellationToken.None);

        response.Content.Should().Be("ok");
        var request = primary.LastRequest!;
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent");
        request.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be("registration-test-key");
        request.Headers.Contains("x-api-key").Should().BeFalse("the Anthropic key header must not go to Google");
        request.Headers.GetValues(CorrelationIdHandler.HeaderName).Should().ContainSingle().Which.Should().Be("corr-registration");
    }

    [Fact]
    public async Task AddLlmProvider_AnthropicConfigured_SendsTheCallToAnthropicWithItsKeyHeader()
    {
        var primary = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"m","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var container = Build([], primary);
        using var scope = container.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ILlmProvider>()
            .CompleteAsync(new LlmRequest("system", []), CancellationToken.None);

        var request = primary.LastRequest!;
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.anthropic.com/v1/messages");
        request.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("registration-test-key");
        request.Headers.Contains("x-goog-api-key").Should().BeFalse();
    }

    [Fact]
    public void AddLlmProvider_Always_BindsTheValidatedOptions()
    {
        using var container = Build(new() { ["Provider"] = "Gemini" });

        var options = container.GetRequiredService<IOptions<LlmProviderOptions>>().Value;

        options.Provider.Should().Be(LlmProviderKind.Gemini);
        options.BaseUrl.Should().Be(LlmProviderOptions.GeminiDefaultBaseUrl);
    }
}
