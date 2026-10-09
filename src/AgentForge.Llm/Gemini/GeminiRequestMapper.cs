using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentForge.Llm.Gemini;

/// <summary>Maps the provider-agnostic <see cref="LlmRequest"/> to Gemini's <c>generateContent</c> wire format.</summary>
public static class GeminiRequestMapper
{
    // Annotation-only keywords Gemini's JSON Schema subset does not list; dropping them changes no shape.
    private static readonly string[] UnsupportedSchemaKeywords = ["$schema", "$id", "$comment"];

    /// <summary>Maps <paramref name="request"/>. The model is not part of the body; it is the URL's path segment.</summary>
    public static GeminiGenerateContentRequest Map(LlmRequest request)
    {
        // functionResponse must name its function, which LlmToolResultContent does not carry: recover it from the
        // call it answers. Ids are unique per call, so the last one seen wins harmlessly.
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var toolUse in request.Messages.SelectMany(m => m.Content).OfType<LlmToolUseContent>())
        {
            callNames[toolUse.Id] = toolUse.ToolName;
        }

        return new GeminiGenerateContentRequest(
            Contents: [.. request.Messages.Select(m => MapMessage(m, callNames))],
            SystemInstruction: string.IsNullOrEmpty(request.SystemPrompt)
                ? null
                : new GeminiContent(Role: null, [new GeminiPart(Text: request.SystemPrompt)]),
            Tools: request.Tools is null || request.Tools.Count == 0
                ? null
                : [new GeminiTool([.. request.Tools.Select(MapTool)])],
            GenerationConfig: new GeminiGenerationConfig(request.MaxOutputTokens));
    }

    private static GeminiContent MapMessage(LlmMessage message, Dictionary<string, string> callNames) =>
        new(MapRole(message.Role), [.. message.Content.Select(c => MapContent(c, callNames))]);

    private static string MapRole(LlmRole role) => role switch
    {
        LlmRole.User => "user",
        LlmRole.Assistant => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown LlmRole."),
    };

    private static GeminiPart MapContent(LlmContent content, Dictionary<string, string> callNames) => content switch
    {
        LlmTextContent text => new GeminiPart(Text: text.Text),
        LlmToolUseContent toolUse => new GeminiPart(
            FunctionCall: new GeminiFunctionCall(
                WireId(toolUse.Id),
                toolUse.ToolName,
                JsonNode.Parse(toolUse.ArgumentsJson)
                    ?? throw new InvalidOperationException($"Tool use '{toolUse.Id}' has a null ArgumentsJson.")),
            ThoughtSignature: toolUse.ReplayToken),
        LlmToolResultContent toolResult => new GeminiPart(
            FunctionResponse: new GeminiFunctionResponse(
                WireId(toolResult.ToolUseId),
                callNames.TryGetValue(toolResult.ToolUseId, out var name)
                    ? name
                    : throw new InvalidOperationException(
                        $"Tool result '{toolResult.ToolUseId}' answers no function call in the history."),
                WrapResult(toolResult))),
        LlmImageContent image => new GeminiPart(InlineData: new GeminiBlob(image.MediaType, image.Base64Data)),
        LlmDocumentContent document => new GeminiPart(InlineData: new GeminiBlob(document.MediaType, document.Base64Data)),
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown LlmContent type."),
    };

    // An id this adapter minted was never Gemini's, so it is not handed back to Gemini.
    private static string? WireId(string id) =>
        id.StartsWith(GeminiResponseMapper.SynthesizedCallIdPrefix, StringComparison.Ordinal) ? null : id;

    // response is a protobuf Struct, so any result - an array, a string, invalid JSON - is wrapped in an object, under
    // the "output" or "error" key Google's function-calling reference recommends.
    private static JsonObject WrapResult(LlmToolResultContent toolResult)
    {
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(toolResult.ResultJson);
        }
        catch (JsonException)
        {
            value = JsonValue.Create(toolResult.ResultJson);
        }

        return new JsonObject { [toolResult.IsError ? "error" : "output"] = value };
    }

    private static GeminiFunctionDeclaration MapTool(LlmToolDefinition tool) => new(
        Name: tool.Name,
        Description: tool.Description,
        ParametersJsonSchema: Sanitize(
            JsonNode.Parse(tool.InputJsonSchema)
                ?? throw new InvalidOperationException($"Tool '{tool.Name}' has a null InputJsonSchema.")));

    private static JsonNode Sanitize(JsonNode schema)
    {
        switch (schema)
        {
            case JsonObject obj:
                foreach (var keyword in UnsupportedSchemaKeywords)
                {
                    obj.Remove(keyword);
                }

                foreach (var (_, child) in obj)
                {
                    if (child is not null)
                    {
                        Sanitize(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Sanitize(child);
                    }
                }

                break;
        }

        return schema;
    }
}
