using System.Net;
using System.Text;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Llm;
using AgentForge.Llm.Gemini;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.UnitTests.Llm.Gemini;

public sealed class GeminiLlmProviderTests
{
    private const string Model = "gemini-test-model";
    private const string ApiKey = "gemini-key-do-not-leak";

    private static readonly IOptions<LlmProviderOptions> Options = Microsoft.Extensions.Options.Options.Create(
        new LlmProviderOptions
        {
            Provider = LlmProviderKind.Gemini,
            ApiKey = ApiKey,
            Model = Model,
            InputPricePerMillionTokensUsd = 0.30m,
            OutputPricePerMillionTokensUsd = 2.50m,
        });

    private static GeminiGenerateContentResponse TextResponse(string text, int input = 10, int output = 5) => new(
        Candidates: [new GeminiCandidate(new GeminiContent("model", [new GeminiPart(Text: text)]), "STOP")],
        UsageMetadata: new GeminiUsageMetadata(input, output));

    [Fact]
    public async Task CompleteAsync_ValidRequest_CallsTheConfiguredModelWithTheMappedRequest()
    {
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        GeminiGenerateContentRequest? captured = null;
        string? capturedModel = null;
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .Invokes((string model, GeminiGenerateContentRequest req, CancellationToken _) =>
            {
                capturedModel = model;
                captured = req;
            })
            .Returns(Task.FromResult(TextResponse("Hello")));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var result = await provider.CompleteAsync(new LlmRequest("system prompt", []), CancellationToken.None);

        capturedModel.Should().Be(Model);
        captured!.SystemInstruction!.Parts[0].Text.Should().Be("system prompt");
        result.Content.Should().Be("Hello");
        result.Usage.InputTokens.Should().Be(10);
    }

    [Fact]
    public async Task CompleteAsync_ApiResponse_MapsUsingConfiguredPricing()
    {
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(TextResponse("x", 1_000_000, 1_000_000)));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var result = await provider.CompleteAsync(new LlmRequest("system", []), CancellationToken.None);

        result.Usage.EstimatedCostUsd.Should().Be(0.30m + 2.50m);
    }

    [Fact]
    public async Task CompleteAsync_Always_PassesCancellationTokenThrough()
    {
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(TextResponse("x")));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);
        using var cts = new CancellationTokenSource();

        await provider.CompleteAsync(new LlmRequest("system", []), cts.Token);

        A.CallTo(() => api.GenerateContentAsync(Model, A<GeminiGenerateContentRequest>._, cts.Token)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CompleteAsync_OverTheRealRefitClient_PostsGenerateContentForTheModelWithTheKeyInAHeaderOnly()
    {
        // The route and the auth as the host wires them, against a recorded-shape body and no network: the model is
        // a path segment ahead of ":generateContent", and the key travels in x-goog-api-key, never ?key=.
        string? body = null;
        var capturing = new CapturingHttpMessageHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RecordedFunctionCallResponse, Encoding.UTF8, "application/json"),
            };
        });
        var auth = new GeminiAuthHandler(Options) { InnerHandler = capturing };
        var api = RestService.For<IGeminiGenerativeLanguageApi>(
            new HttpClient(auth) { BaseAddress = new Uri(LlmProviderOptions.GeminiDefaultBaseUrl) });
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var result = await provider.CompleteAsync(
            new LlmRequest("system", [LlmMessage.FromText(LlmRole.User, "INR?")]), CancellationToken.None);

        var request = capturing.LastRequest!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-test-model:generateContent");
        request.RequestUri.Query.Should().BeEmpty();
        request.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be(ApiKey);
        body.Should().Contain("\"contents\"").And.Contain("\"generationConfig\"").And.NotContain(ApiKey);
        result.StopReason.Should().Be(LlmStopReason.ToolUse);
        result.ToolCalls.Should().ContainSingle().Which.ReplayToken.Should().Be("c2lnbmF0dXJl");
        result.Usage.Should().Be(new LlmUsage(42, 7, (42 * 0.30m + 7 * 2.50m) / 1_000_000m));
    }

    [Fact]
    public async Task CompleteAsync_ApiReturnsAnErrorResponse_ThrowsWithTheStatusAndErrorStatusButNotTheBody()
    {
        // a separate change item 5, for Gemini: the orchestrator logs this Message verbatim on fallback, and Google's
        // error.message can quote the request back. error.status is google.rpc.Code's fixed vocabulary.
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(HttpStatusCode.BadRequest, ErrorBodyEchoingThePrompt));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("400").And.Contain("INVALID_ARGUMENT");
        thrown.Message.Should().NotContain("Brekke").And.NotContain("ejection fraction").And.NotContain(ApiKey);
    }

    [Fact]
    public async Task CompleteAsync_ErrorBodyEchoesAPatientName_NeitherTheExceptionNorAnyLogLineCarriesIt()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(HttpStatusCode.BadRequest, ErrorBodyEchoingThePrompt));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().NotContainAny("Brekke", "Aaron");
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("error_status=INVALID_ARGUMENT", StringComparison.Ordinal)
            && line.Contains("provider=gemini", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ErrorStatusIsOutsideTheKnownVocabulary_ReportsItAsUnrecognized()
    {
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"x","status":"Aaron Brekke"}}"""));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().Contain("unrecognized").And.NotContain("Brekke");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"error":"flat string"}""")]
    [InlineData("""[1,2]""")]
    public async Task CompleteAsync_ErrorBodyIsNotGooglesShape_ReportsUnrecognized(string body)
    {
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(HttpStatusCode.BadGateway, body));
        var provider = new GeminiLlmProvider(api, Options, NullLogger<GeminiLlmProvider>.Instance);

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("unrecognized");
    }

    [Fact]
    public async Task CompleteAsync_CallSucceeds_LogsOneLineWithModelTokensCostAndStopReasonInsideTheCorrelationScope()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(TextResponse("Summary.", 1_000_000, 1_000_000)));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        using (loggerFactory.CreateLogger("Ingress").BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = "corr-1" }))
        {
            await provider.CompleteAsync(new LlmRequest("system", []), CancellationToken.None);
        }

        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains($"model={Model}", StringComparison.Ordinal)
            && line.Contains("in_tokens=1000000", StringComparison.Ordinal)
            && line.Contains("out_tokens=1000000", StringComparison.Ordinal)
            && line.Contains("cost_usd=2.8", StringComparison.Ordinal)
            && line.Contains("stop_reason=EndTurn", StringComparison.Ordinal)
            && line.Contains("latency_ms=", StringComparison.Ordinal)
            && line.Contains("outcome=ok", StringComparison.Ordinal)
            && line.Contains("provider=gemini", StringComparison.Ordinal)
            && line.Contains("CorrelationId=corr-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_CallSucceeds_LogsNeitherPromptNorCompletionTextNorKey()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(TextResponse("Ejection fraction 40 percent.")));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        await provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        logs.Lines.Should().NotBeEmpty();
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ApiReturnsAnErrorResponse_LogsTheFailedCallWithoutTheProviderErrorBody()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest,
                """{"error":{"code":400,"message":"generationConfig.maxOutputTokens: invalid","status":"INVALID_ARGUMENT"}}"""));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains($"model={Model}", StringComparison.Ordinal)
            && line.Contains("status=400", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal)
            && !line.Contains("maxOutputTokens", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ResilienceExhaustsBeforeAnyResponse_StillLogsTheFailedCall()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("attempt timed out"));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>("the caller's degrade path must still see the failure");
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains($"model={Model}", StringComparison.Ordinal)
            && line.Contains("reason=TimeoutException", StringComparison.Ordinal)
            && line.Contains("error_status=none", StringComparison.Ordinal)
            && line.Contains("latency_ms=", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ProviderErrorCarriesNoBody_LogsTheFailedCallAndRethrowsUnwrapped()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IGeminiGenerativeLanguageApi>();
        A.CallTo(() => api.GenerateContentAsync(A<string>._, A<GeminiGenerateContentRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(HttpStatusCode.ServiceUnavailable, string.Empty));
        var provider = new GeminiLlmProvider(api, Options, loggerFactory.CreateLogger<GeminiLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("status=503", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    private static LlmRequest ChartBearingRequest => new(
        "You are a cardiology copilot.",
        [new LlmMessage(LlmRole.User, [new LlmTextContent("Brief for Aaron Brekke, ejection fraction 40 percent.")])]);

    private static void ShouldCarryNoChartContent(CapturingLoggerProvider logs) =>
        logs.Lines.Should().OnlyContain(line =>
            !line.Contains("Brekke", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("Ejection fraction", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("cardiology copilot", StringComparison.OrdinalIgnoreCase)
            && !line.Contains(ApiKey, StringComparison.Ordinal));

    // Synthetic, shaped like a Google API 400 whose message quotes the request back.
    private const string ErrorBodyEchoingThePrompt =
        """{"error":{"code":400,"message":"contents[0]: 'Brief for Aaron Brekke, ejection fraction 40 percent.' is invalid","status":"INVALID_ARGUMENT"}}""";

    // Stubbed generateContent body: one functionCall part carrying a thoughtSignature, as Gemini 3 returns it.
    private const string RecordedFunctionCallResponse = """
        {
          "candidates": [
            {
              "content": {
                "role": "model",
                "parts": [
                  { "functionCall": { "id": "fc_1", "name": "get_labs", "args": {} }, "thoughtSignature": "c2lnbmF0dXJl" }
                ]
              },
              "finishReason": "STOP",
              "index": 0
            }
          ],
          "usageMetadata": { "promptTokenCount": 42, "candidatesTokenCount": 7, "totalTokenCount": 49 },
          "modelVersion": "gemini-test-model",
          "responseId": "resp-1"
        }
        """;

    private static async Task<ApiException> ApiExceptionFor(HttpStatusCode status, string body) =>
        await ApiException.Create(
            new HttpRequestMessage(
                HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-test-model:generateContent"),
            HttpMethod.Post,
            new HttpResponseMessage(status) { Content = new StringContent(body) },
            new RefitSettings());
}
