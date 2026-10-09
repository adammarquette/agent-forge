using System.Net;
using FluentAssertions;
using AgentForge.Llm;
using AgentForge.Llm.Gemini;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Llm.Gemini;

public sealed class GeminiAuthHandlerTests
{
    private const string ApiKey = "gemini-test-key-123";

    private static IOptions<LlmProviderOptions> Options => Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions
    {
        Provider = LlmProviderKind.Gemini,
        ApiKey = ApiKey,
        Model = "gemini-test-model",
        InputPricePerMillionTokensUsd = 0m,
        OutputPricePerMillionTokensUsd = 0m,
    });

    [Fact]
    public async Task SendAsync_Always_AttachesTheKeyAsTheGoogApiKeyHeader()
    {
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new GeminiAuthHandler(Options) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(
                HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-test-model:generateContent"),
            CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
    }

    [Fact]
    public async Task SendAsync_Always_KeepsTheKeyOutOfTheUrl()
    {
        // Google also accepts the key as a ?key= query parameter, and a URL reaches access logs, traces and
        // exception messages that a header does not. The header is the only place it may travel.
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new GeminiAuthHandler(Options) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models/gemini-test-model"),
            CancellationToken.None);

        capturing.LastRequest!.RequestUri!.ToString().Should().NotContain(ApiKey).And.NotContain("key=");
    }

    [Fact]
    public async Task SendAsync_RequestAlreadyCarriesAKeyHeader_ReplacesItRatherThanSendingTwo()
    {
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new GeminiAuthHandler(Options) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models/m");
        request.Headers.Add("x-goog-api-key", "stale");

        await invoker.SendAsync(request, CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
    }
}
