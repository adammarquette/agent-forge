using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// Wire-format request body for Gemini's <c>models/{model}:generateContent</c> (ai.google.dev/api/generate-content,
/// read 2026-10-08). The model is a path segment, not a field. Deliberately has no <c>temperature</c>: no
/// <see cref="LlmRequest"/> carries one, matching the Anthropic envelope (PROMPTS.md section 1).
/// </summary>
public sealed record GeminiGenerateContentRequest(
    [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
    [property: JsonPropertyName("systemInstruction"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiContent? SystemInstruction,
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GeminiTool>? Tools,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

/// <summary>One turn - <c>user</c> or <c>model</c> - or, with no role, the system instruction.</summary>
public sealed record GeminiContent(
    [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role,
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

/// <summary>
/// One part of a turn, in both directions. Exactly one data field is set; the rest are omitted from the JSON.
/// <see cref="ThoughtSignature"/> rides on the part it was returned with, and Gemini 3 rejects a replayed
/// <c>functionCall</c> part without it.
/// </summary>
public sealed record GeminiPart(
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonPropertyName("inlineData"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiBlob? InlineData = null,
    [property: JsonPropertyName("functionCall"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiFunctionCall? FunctionCall = null,
    [property: JsonPropertyName("functionResponse"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeminiFunctionResponse? FunctionResponse = null,
    [property: JsonPropertyName("thought"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Thought = null,
    [property: JsonPropertyName("thoughtSignature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThoughtSignature = null);

/// <summary>Inline media bytes - an image or a PDF - base64-encoded.</summary>
public sealed record GeminiBlob(
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] string Data);

/// <summary>A function call the model made. <see cref="Id"/> may be absent on a response.</summary>
public sealed record GeminiFunctionCall(
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("args"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Args);

/// <summary>A function's result, answering the call of the same name (and id, when it had one).</summary>
/// <param name="Id">The id of the call answered; omitted when Gemini issued none.</param>
/// <param name="Name">The function's name, as declared.</param>
/// <param name="Response">A JSON object - the API types it as a protobuf <c>Struct</c>.</param>
public sealed record GeminiFunctionResponse(
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("response")] JsonNode Response);

/// <summary>One tool: the function declarations the model may call.</summary>
public sealed record GeminiTool(
    [property: JsonPropertyName("functionDeclarations")] IReadOnlyList<GeminiFunctionDeclaration> FunctionDeclarations);

/// <summary>
/// One function, its parameters given in JSON Schema form (<c>parametersJsonSchema</c>, mutually exclusive with the
/// OpenAPI-subset <c>parameters</c>), so the catalog's schemas travel as they are.
/// </summary>
public sealed record GeminiFunctionDeclaration(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parametersJsonSchema")] JsonNode ParametersJsonSchema);

/// <summary>Generation settings: only the output bound, as the request carries nothing else.</summary>
public sealed record GeminiGenerationConfig(
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens);
