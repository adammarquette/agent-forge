using System.Text.Json.Serialization;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// Wire-format response body from <c>generateContent</c>. Every field is optional on the wire: a prompt the API
/// blocks comes back with <see cref="PromptFeedback"/> and no candidates at all.
/// </summary>
public sealed record GeminiGenerateContentResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates = null,
    [property: JsonPropertyName("usageMetadata")] GeminiUsageMetadata? UsageMetadata = null,
    [property: JsonPropertyName("promptFeedback")] GeminiPromptFeedback? PromptFeedback = null,
    [property: JsonPropertyName("modelVersion")] string? ModelVersion = null,
    [property: JsonPropertyName("responseId")] string? ResponseId = null);

/// <summary>One candidate answer; only the first is read, since no request asks for more.</summary>
public sealed record GeminiCandidate(
    [property: JsonPropertyName("content")] GeminiContent? Content = null,
    [property: JsonPropertyName("finishReason")] string? FinishReason = null);

/// <summary>
/// Token accounting. <see cref="ThoughtsTokenCount"/> is billed as output (ai.google.dev/gemini-api/docs/pricing,
/// read 2026-10-08: output price "including thinking tokens").
/// </summary>
public sealed record GeminiUsageMetadata(
    [property: JsonPropertyName("promptTokenCount")] int PromptTokenCount = 0,
    [property: JsonPropertyName("candidatesTokenCount")] int CandidatesTokenCount = 0,
    [property: JsonPropertyName("thoughtsTokenCount")] int ThoughtsTokenCount = 0);

/// <summary>Why the prompt itself was blocked, when it was.</summary>
public sealed record GeminiPromptFeedback(
    [property: JsonPropertyName("blockReason")] string? BlockReason = null);
