using System.Net;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Llm;
using AgentForge.Llm.Anthropic;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.UnitTests.Llm.Anthropic;

public sealed class AnthropicLlmProviderTests
{
    private static readonly IOptions<LlmProviderOptions> Options = Microsoft.Extensions.Options.Options.Create(
        new LlmProviderOptions
        {
            ApiKey = "k",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        });

    [Fact]
    public async Task CompleteAsync_ValidRequest_MapsRequestWithConfiguredModelBeforeCallingApi()
    {
        var api = A.Fake<IAnthropicMessagesApi>();
        AnthropicMessageRequest? captured = null;
        var wireResponse = new AnthropicMessageResponse(
            "msg_1", [new AnthropicContentBlock("text", "Hello", null, null, null)], "end_turn", new AnthropicUsage(10, 5));
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .Invokes((AnthropicMessageRequest req, CancellationToken _) => captured = req)
            .Returns(Task.FromResult(wireResponse));
        var provider = new AnthropicLlmProvider(api, Options, NullLogger<AnthropicLlmProvider>.Instance);

        var result = await provider.CompleteAsync(new LlmRequest("system prompt", []), CancellationToken.None);

        captured!.Model.Should().Be("claude-sonnet-5");
        captured.System.Should().Be("system prompt");
        result.Content.Should().Be("Hello");
        result.Usage.InputTokens.Should().Be(10);
    }

    [Fact]
    public async Task CompleteAsync_ApiResponse_MapsUsingConfiguredPricing()
    {
        var api = A.Fake<IAnthropicMessagesApi>();
        var wireResponse = new AnthropicMessageResponse(
            "msg_2", [], "end_turn", new AnthropicUsage(1_000_000, 1_000_000));
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(wireResponse));
        var provider = new AnthropicLlmProvider(api, Options, NullLogger<AnthropicLlmProvider>.Instance);

        var result = await provider.CompleteAsync(new LlmRequest("system", []), CancellationToken.None);

        result.Usage.EstimatedCostUsd.Should().Be(3.00m + 15.00m);
    }

    [Fact]
    public async Task CompleteAsync_Always_PassesCancellationTokenThrough()
    {
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AnthropicMessageResponse("msg_3", [], "end_turn", new AnthropicUsage(1, 1))));
        var provider = new AnthropicLlmProvider(api, Options, NullLogger<AnthropicLlmProvider>.Instance);
        using var cts = new CancellationTokenSource();

        await provider.CompleteAsync(new LlmRequest("system", []), cts.Token);

        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, cts.Token)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CompleteAsync_ApiReturnsAnErrorResponse_ThrowsWithTheStatusAndErrorTypeButNotTheBody()
    {
        // Refit's ApiException.Message is only the status line, and the body used to be folded in so a real
        // invalid-request failure was diagnosable from AgentOrchestrator's fallback log (REQUIREMENTS.md §13.1). But the
        // body's "message" can quote the request back, and that log renders this exception's Message verbatim,
        // which put prompt text one line away. Anthropic's error.type is a fixed vocabulary and the
        // request-id header an opaque token: together they keep the failure diagnosable without echoing the request.
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest, ErrorBodyEchoingThePrompt, requestId: SyntheticRequestId));
        var provider = new AnthropicLlmProvider(api, Options, NullLogger<AnthropicLlmProvider>.Instance);

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("400").And.Contain(InvalidRequestError).And.Contain(SyntheticRequestId);
        thrown.Message.Should().NotContain("Brekke").And.NotContain("ejection fraction");
    }

    [Fact]
    public async Task CompleteAsync_ErrorBodyEchoesAPatientName_NeitherTheExceptionNorAnyLogLineCarriesIt()
    {
        // The failure path as the orchestrator sees it: the provider's own log line, plus the exception message
        // its fallback line renders. A synthetic patient name in the body must reach neither.
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest, ErrorBodyEchoingThePrompt, requestId: SyntheticRequestId));
        var provider = new AnthropicLlmProvider(api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().NotContainAny("Brekke", "Aaron");
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains(InvalidRequestError, StringComparison.Ordinal)
            && line.Contains(SyntheticRequestId, StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ErrorTypeIsOutsideTheKnownVocabulary_ReportsItAsUnrecognized()
    {
        // error.type is safe to log only because Anthropic's set is fixed; free text in that field is not trusted.
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"Aaron Brekke","message":"x"}}"""));
        var provider = new AnthropicLlmProvider(api, Options, NullLogger<AnthropicLlmProvider>.Instance);

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Message.Should().Contain("unrecognized").And.NotContain("Brekke");
    }

    [Fact]
    public async Task CompleteAsync_CallSucceeds_LogsOneLineWithModelTokensCostAndStopReasonInsideTheCorrelationScope()
    {
        // FR-OBS-1 requires every LLM interaction to produce a correlation-tagged record. Token use
        // was recorded as a *metric* only, and metrics carry no correlation id, so no model call was
        // reconstructable from logs on any path - including the chat path, whose scope was open the
        // whole time. A separate change
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AnthropicMessageResponse(
                "msg_9", [new AnthropicContentBlock("text", "Summary.", null, null, null)], "end_turn",
                new AnthropicUsage(1_000_000, 1_000_000))));
        var provider = new AnthropicLlmProvider(
            api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        using (loggerFactory.CreateLogger("Ingress").BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = "corr-1" }))
        {
            await provider.CompleteAsync(new LlmRequest("system", []), CancellationToken.None);
        }

        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("model=claude-sonnet-5", StringComparison.Ordinal)
            && line.Contains("in_tokens=1000000", StringComparison.Ordinal)
            && line.Contains("out_tokens=1000000", StringComparison.Ordinal)
            && line.Contains("cost_usd=18", StringComparison.Ordinal)
            && line.Contains("stop_reason=EndTurn", StringComparison.Ordinal)
            && line.Contains("latency_ms=", StringComparison.Ordinal)
            && line.Contains("CorrelationId=corr-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_CallSucceeds_LogsNeitherPromptNorCompletionText()
    {
        // The LLM hop is the highest-risk place in the codebase to log a payload: a request carries
        // chart content and the response carries the synthesized narrative. Counts, ids and outcomes
        // only - no prompt, no completion (CONVENTIONS.md §7, NFR-SEC-1).
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AnthropicMessageResponse(
                "msg_10", [new AnthropicContentBlock("text", "Ejection fraction 40 percent.", null, null, null)],
                "end_turn", new AnthropicUsage(10, 5))));
        var provider = new AnthropicLlmProvider(
            api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        await provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        logs.Lines.Should().NotBeEmpty();
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ApiReturnsAnErrorResponse_LogsTheFailedCallWithoutTheProviderErrorBody()
    {
        // A failed call is still an LLM interaction, and it is the one most worth finding in a
        // trace - but the provider's error body can echo request fields, so only the status, the
        // model, the reason and the latency are logged. The request is chart-bearing here for the
        // same reason the success test's is: the failure path is where the next author reaches for
        // "log the request so we can debug it", and that guard has to bind on this path too.
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(
                HttpStatusCode.BadRequest, """{"error":{"message":"max_tokens: field required"}}"""));
        var provider = new AnthropicLlmProvider(
            api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("model=claude-sonnet-5", StringComparison.Ordinal)
            && line.Contains("status=400", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal)
            && !line.Contains("max_tokens", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ResilienceExhaustsBeforeAnyResponse_StillLogsTheFailedCall()
    {
        // The Polly pipeline on this client (Program.cs) surfaces attempt-timeout, total-timeout and
        // circuit-open as its own exception types, never as an ApiException - so a failure filtered
        // on ApiException leaves the whole degrade path (REQUIREMENTS.md §13.1) with no record at
        // all, which is the one place a trace is most worth having. A separate change
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException("attempt timed out"));
        var provider = new AnthropicLlmProvider(
            api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>("the caller's degrade path must still see the failure");
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("model=claude-sonnet-5", StringComparison.Ordinal)
            && line.Contains("reason=TimeoutException", StringComparison.Ordinal)
            && line.Contains("latency_ms=", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    [Fact]
    public async Task CompleteAsync_ProviderErrorCarriesNoBody_LogsTheFailedCallAndRethrowsUnwrapped()
    {
        // A 5xx with an empty body is the other half of the same hole: the message-folding filter
        // does not match it, so it produced no record either. The exception stays a
        // Refit ApiException here - there is no body to fold into a message.
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var api = A.Fake<IAnthropicMessagesApi>();
        A.CallTo(() => api.CreateMessageAsync(A<AnthropicMessageRequest>._, A<CancellationToken>._))
            .ThrowsAsync(await ApiExceptionFor(HttpStatusCode.ServiceUnavailable, string.Empty));
        var provider = new AnthropicLlmProvider(
            api, Options, loggerFactory.CreateLogger<AnthropicLlmProvider>());

        var act = () => provider.CompleteAsync(ChartBearingRequest, CancellationToken.None);

        await act.Should().ThrowAsync<ApiException>();
        logs.Lines.Should().ContainSingle(line =>
            line.Contains("llm.call", StringComparison.Ordinal)
            && line.Contains("status=503", StringComparison.Ordinal)
            && line.Contains("outcome=error", StringComparison.Ordinal));
        ShouldCarryNoChartContent(logs);
    }

    // Synthetic, and deliberately shaped like the real thing: a cardiology system prompt plus a
    // patient-named, value-bearing question. Every assertion below reads it back out of the logs.
    private static LlmRequest ChartBearingRequest => new(
        "You are a cardiology copilot.",
        [new LlmMessage(LlmRole.User, [new LlmTextContent("Brief for Aaron Brekke, ejection fraction 40 percent.")])]);

    private static void ShouldCarryNoChartContent(CapturingLoggerProvider logs) =>
        logs.Lines.Should().OnlyContain(line =>
            !line.Contains("Brekke", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("Ejection fraction", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("cardiology copilot", StringComparison.OrdinalIgnoreCase));

    private const string InvalidRequestError = "invalid_request_error";
    private const string SyntheticRequestId = "req_011SyntheticGl679";

    // Synthetic, shaped like an Anthropic 400 whose message quotes the request back.
    private const string ErrorBodyEchoingThePrompt =
        $$$"""{"type":"error","error":{"type":"{{{InvalidRequestError}}}","message":"messages.0: 'Brief for Aaron Brekke, ejection fraction 40 percent.' is invalid"}}""";

    private static async Task<ApiException> ApiExceptionFor(HttpStatusCode status, string body, string? requestId = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (requestId is not null)
        {
            response.Headers.Add("request-id", requestId);
        }

        return await ApiException.Create(
            new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"),
            HttpMethod.Post,
            response,
            new RefitSettings());
    }
}
