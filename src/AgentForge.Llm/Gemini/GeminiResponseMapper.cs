namespace AgentForge.Llm.Gemini;

/// <summary>Maps a Gemini <c>generateContent</c> response to the provider-agnostic <see cref="LlmResponse"/>.</summary>
public static class GeminiResponseMapper
{
    /// <summary>
    /// Prefix of a call id this adapter minted because Gemini sent none. The request mapper leaves such ids off the
    /// wire, so Gemini is never handed an id it did not issue.
    /// </summary>
    public const string SynthesizedCallIdPrefix = "gemini-call-";

    private const decimal TokensPerMillion = 1_000_000m;

    /// <summary>
    /// Maps <paramref name="response"/>, computing <see cref="LlmUsage.EstimatedCostUsd"/> from the caller-supplied
    /// per-million-token pricing (LlmProviderOptions - zero on a free tier, which is then what is reported).
    /// </summary>
    public static LlmResponse Map(
        GeminiGenerateContentResponse response, decimal inputPricePerMillion, decimal outputPricePerMillion)
    {
        var candidate = response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
        var parts = candidate?.Content?.Parts ?? [];

        var content = string.Concat(parts.Where(p => p.Text is not null && p.Thought != true).Select(p => p.Text));

        var toolCalls = parts
            .Where(p => p.FunctionCall is not null)
            .Select(p => new LlmToolCall(
                string.IsNullOrEmpty(p.FunctionCall!.Id) ? SynthesizedCallIdPrefix + Guid.NewGuid().ToString("N") : p.FunctionCall.Id,
                p.FunctionCall.Name ?? throw new InvalidOperationException("Gemini functionCall part is missing its name."),
                p.FunctionCall.Args?.ToJsonString() ?? "{}",
                p.ThoughtSignature))
            .ToList();

        var inputTokens = response.UsageMetadata?.PromptTokenCount ?? 0;
        var outputTokens = (response.UsageMetadata?.CandidatesTokenCount ?? 0)
            + (response.UsageMetadata?.ThoughtsTokenCount ?? 0);
        var cost = inputTokens / TokensPerMillion * inputPricePerMillion
            + outputTokens / TokensPerMillion * outputPricePerMillion;

        return new LlmResponse(
            Content: content,
            ToolCalls: toolCalls,
            StopReason: MapStopReason(candidate?.FinishReason, toolCalls.Count > 0),
            Usage: new LlmUsage(inputTokens, outputTokens, cost));
    }

    // Gemini reports STOP on a turn that calls functions; the orchestrator's loop keys on ToolUse. Every block -
    // SAFETY, RECITATION, PROHIBITED_CONTENT and the rest - is Other, never a clean EndTurn.
    private static LlmStopReason MapStopReason(string? finishReason, bool hasToolCalls) => finishReason switch
    {
        "STOP" => hasToolCalls ? LlmStopReason.ToolUse : LlmStopReason.EndTurn,
        "MAX_TOKENS" => LlmStopReason.MaxTokens,
        _ => LlmStopReason.Other,
    };
}
