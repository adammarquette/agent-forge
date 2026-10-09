using System.Text.Json;
using FluentAssertions;
using AgentForge.Llm;
using AgentForge.Llm.Gemini;

namespace AgentForge.UnitTests.Llm.Gemini;

/// <summary>
/// Stubbed generateContent bodies, shaped after the response reference (ai.google.dev/api/generate-content,
/// read 2026-10-08) and parsed with Refit's default serializer settings, as the client parses them.
/// </summary>
public sealed class GeminiResponseMapperTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static GeminiGenerateContentResponse Parse(string json) =>
        JsonSerializer.Deserialize<GeminiGenerateContentResponse>(json, Wire)!;

    private static LlmResponse Map(string json, decimal input = 0.30m, decimal output = 2.50m) =>
        GeminiResponseMapper.Map(Parse(json), input, output);

    [Fact]
    public void Map_TextOnlyResponse_ExtractsText()
    {
        var mapped = Map("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Her most recent INR was 2.3."}]},"finishReason":"STOP","index":0}],
             "usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20,"totalTokenCount":120},
             "modelVersion":"gemini-test-model","responseId":"r1"}
            """);

        mapped.Content.Should().Be("Her most recent INR was 2.3.");
        mapped.ToolCalls.Should().BeEmpty();
        mapped.StopReason.Should().Be(LlmStopReason.EndTurn);
    }

    [Fact]
    public void Map_MultipleTextParts_ConcatenatesThem()
    {
        var mapped = Map("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Part one. "},{"text":"Part two."}]},"finishReason":"STOP"}]}
            """);

        mapped.Content.Should().Be("Part one. Part two.");
    }

    [Fact]
    public void Map_ThoughtParts_AreNotPartOfTheAnswer()
    {
        // A thought summary is the model's reasoning, not the answer the verifier and the clinician read.
        var mapped = Map("""
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Let me think about INR.","thought":true},{"text":"INR 2.3."}]},"finishReason":"STOP"}]}
            """);

        mapped.Content.Should().Be("INR 2.3.");
    }

    [Fact]
    public void Map_FunctionCallPart_MapsToLlmToolCallWithIdNameArgumentsAndReplayToken()
    {
        var mapped = Map("""
            {"candidates":[{"content":{"role":"model","parts":[
                {"functionCall":{"id":"fc_123","name":"get_labs","args":{"patientId":"1"}},"thoughtSignature":"c2ln"}
             ]},"finishReason":"STOP"}]}
            """);

        var call = mapped.ToolCalls.Should().ContainSingle().Which;
        call.Id.Should().Be("fc_123");
        call.ToolName.Should().Be("get_labs");
        call.ArgumentsJson.Should().Be("""{"patientId":"1"}""");
        call.ReplayToken.Should().Be("c2ln");
    }

    [Fact]
    public void Map_FunctionCallWithoutArgs_MapsToAnEmptyObject()
    {
        var mapped = Map("""
            {"candidates":[{"content":{"parts":[{"functionCall":{"id":"fc_1","name":"get_patient_summary"}}]},"finishReason":"STOP"}]}
            """);

        mapped.ToolCalls.Should().ContainSingle().Which.ArgumentsJson.Should().Be("{}");
    }

    [Fact]
    public void Map_FunctionCallsWithoutIds_SynthesizesADistinctIdForEach()
    {
        // generateContent may omit functionCall.id; the orchestrator pairs every result to its call by id, so two
        // parallel calls must not share one.
        var mapped = Map("""
            {"candidates":[{"content":{"parts":[
                {"functionCall":{"name":"get_labs","args":{}}},
                {"functionCall":{"name":"get_vitals","args":{}}}
             ]},"finishReason":"STOP"}]}
            """);

        mapped.ToolCalls.Should().HaveCount(2);
        mapped.ToolCalls.Should().OnlyContain(c => c.Id.StartsWith(GeminiResponseMapper.SynthesizedCallIdPrefix, StringComparison.Ordinal));
        mapped.ToolCalls.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Map_FunctionCallMissingItsName_Throws()
    {
        var act = () => Map("""{"candidates":[{"content":{"parts":[{"functionCall":{"id":"fc_1"}}]},"finishReason":"STOP"}]}""");

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("STOP", false, LlmStopReason.EndTurn)]
    [InlineData("STOP", true, LlmStopReason.ToolUse)]
    [InlineData("MAX_TOKENS", false, LlmStopReason.MaxTokens)]
    [InlineData("SAFETY", false, LlmStopReason.Other)]
    [InlineData("RECITATION", false, LlmStopReason.Other)]
    [InlineData("PROHIBITED_CONTENT", false, LlmStopReason.Other)]
    [InlineData("MALFORMED_FUNCTION_CALL", false, LlmStopReason.Other)]
    [InlineData("FINISH_REASON_UNSPECIFIED", false, LlmStopReason.Other)]
    [InlineData(null, false, LlmStopReason.Other)]
    public void Map_FinishReason_MapsToExpectedLlmStopReason(string? finishReason, bool withCall, LlmStopReason expected)
    {
        // Gemini reports STOP on a turn that calls a function, where Anthropic says tool_use; the orchestrator keys
        // its loop on ToolUse. SAFETY and RECITATION are blocks, never a clean answer, so they cannot be EndTurn.
        var parts = withCall ? """[{"functionCall":{"id":"f","name":"get_labs","args":{}}}]""" : """[{"text":"x"}]""";
        var reason = finishReason is null ? string.Empty : $""","finishReason":"{finishReason}" """;
        var mapped = Map($$"""{"candidates":[{"content":{"parts":{{parts}}}{{reason}}}]}""");

        mapped.StopReason.Should().Be(expected);
    }

    [Fact]
    public void Map_PromptBlockedWithNoCandidates_ReturnsEmptyContentStoppedForAnotherReason()
    {
        var mapped = Map("""
            {"promptFeedback":{"blockReason":"SAFETY"},"usageMetadata":{"promptTokenCount":12,"totalTokenCount":12}}
            """);

        mapped.Content.Should().BeEmpty();
        mapped.ToolCalls.Should().BeEmpty();
        mapped.StopReason.Should().Be(LlmStopReason.Other);
        mapped.Usage.InputTokens.Should().Be(12);
        mapped.Usage.OutputTokens.Should().Be(0);
    }

    [Fact]
    public void Map_Usage_ComputesCostFromConfiguredPerMillionTokenPricing()
    {
        var mapped = Map(
            """{"candidates":[],"usageMetadata":{"promptTokenCount":1000000,"candidatesTokenCount":500000,"totalTokenCount":1500000}}""",
            input: 0.30m,
            output: 2.50m);

        mapped.Usage.InputTokens.Should().Be(1_000_000);
        mapped.Usage.OutputTokens.Should().Be(500_000);
        mapped.Usage.EstimatedCostUsd.Should().Be(0.30m + 1.25m);
    }

    [Fact]
    public void Map_ThinkingTokens_CountAsOutputTokens()
    {
        // Google prices output "including thinking tokens" (ai.google.dev/gemini-api/docs/pricing, read 2026-10-08),
        // so leaving thoughtsTokenCount out would under-report cost on every thinking model.
        var mapped = Map(
            """{"candidates":[],"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":20,"thoughtsTokenCount":30,"totalTokenCount":60}}""");

        mapped.Usage.OutputTokens.Should().Be(50);
    }

    [Fact]
    public void Map_FreeTierPricesOfZero_ReportsZeroCost()
    {
        var mapped = Map(
            """{"candidates":[],"usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":1000}}""",
            input: 0m,
            output: 0m);

        mapped.Usage.EstimatedCostUsd.Should().Be(0m);
        mapped.Usage.InputTokens.Should().Be(1000);
    }

    [Fact]
    public void Map_NoUsageMetadata_ReportsZeroTokens()
    {
        var mapped = Map("""{"candidates":[{"content":{"parts":[{"text":"x"}]},"finishReason":"STOP"}]}""");

        mapped.Usage.Should().Be(new LlmUsage(0, 0, 0m));
    }
}
