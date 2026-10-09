using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using AgentForge.Llm;
using AgentForge.Llm.Gemini;

namespace AgentForge.UnitTests.Llm.Gemini;

public sealed class GeminiRequestMapperTests
{
    // Refit's default serializer settings, which is what Program.cs puts on the wire for this client.
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static JsonNode Serialize(GeminiGenerateContentRequest request) =>
        JsonNode.Parse(JsonSerializer.Serialize(request, Wire))!;

    [Fact]
    public void Map_ValidRequest_MapsSystemPromptToSystemInstructionAndMaxTokensToGenerationConfig()
    {
        var request = new LlmRequest("You are a cardiology copilot.", [], MaxOutputTokens: 2048);

        var wire = GeminiRequestMapper.Map(request);

        wire.SystemInstruction!.Parts.Should().ContainSingle().Which.Text.Should().Be("You are a cardiology copilot.");
        wire.SystemInstruction.Role.Should().BeNull("Gemini's systemInstruction carries no role");
        wire.GenerationConfig.MaxOutputTokens.Should().Be(2048);
    }

    [Fact]
    public void Map_EmptySystemPrompt_OmitsSystemInstruction()
    {
        // An empty text part is rejected by generateContent; with no prompt there is nothing to send.
        var wire = GeminiRequestMapper.Map(new LlmRequest(string.Empty, []));

        wire.SystemInstruction.Should().BeNull();
        Serialize(wire).AsObject().ContainsKey("systemInstruction").Should().BeFalse();
    }

    [Fact]
    public void Map_Always_SerializedRequestCarriesExactlyTheDocumentedTopLevelFields()
    {
        // PROMPTS.md section 1 lists the Gemini envelope; temperature is absent because LlmRequest
        // carries none, the same as the Anthropic envelope.
        var request = new LlmRequest(
            "system",
            [LlmMessage.FromText(LlmRole.User, "hello")],
            Tools: [new LlmToolDefinition("get_labs", "Labs.", """{"type":"object","properties":{}}""")]);

        var json = Serialize(GeminiRequestMapper.Map(request));

        json.AsObject().Select(p => p.Key).Should().BeEquivalentTo(
            ["contents", "systemInstruction", "tools", "generationConfig"]);
        json["generationConfig"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["maxOutputTokens"]);
        json.ToJsonString().Should().NotContain("temperature");
    }

    [Fact]
    public void Map_MessagesWithBothRoles_MapsToUserAndModelWireRoles()
    {
        var request = new LlmRequest(
            "system",
            [
                LlmMessage.FromText(LlmRole.User, "Is her INR therapeutic?"),
                LlmMessage.FromText(LlmRole.Assistant, "Her most recent INR was 2.3."),
            ]);

        var wire = GeminiRequestMapper.Map(request);

        wire.Contents.Should().HaveCount(2);
        wire.Contents[0].Role.Should().Be("user");
        wire.Contents[0].Parts.Should().ContainSingle().Which.Text.Should().Be("Is her INR therapeutic?");
        wire.Contents[1].Role.Should().Be("model");
        wire.Contents[1].Parts.Should().ContainSingle().Which.Text.Should().Be("Her most recent INR was 2.3.");
    }

    [Fact]
    public void Map_TextContent_SerializesAsATextPartWithNoOtherFields()
    {
        var request = new LlmRequest("system", [LlmMessage.FromText(LlmRole.User, "hello")]);

        var part = Serialize(GeminiRequestMapper.Map(request))["contents"]![0]!["parts"]![0]!.AsObject();

        part.Select(p => p.Key).Should().BeEquivalentTo(["text"]);
        part["text"]!.GetValue<string>().Should().Be("hello");
    }

    [Fact]
    public void Map_ToolUseContent_MapsToAFunctionCallPartWithParsedArgs()
    {
        var request = new LlmRequest(
            "system",
            [new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", """{"patientId":"1"}""")])]);

        var part = GeminiRequestMapper.Map(request).Contents[0].Parts.Should().ContainSingle().Which;

        part.FunctionCall!.Id.Should().Be("call_1");
        part.FunctionCall.Name.Should().Be("get_labs");
        part.FunctionCall.Args!["patientId"]!.GetValue<string>().Should().Be("1");
    }

    [Fact]
    public void Map_ToolUseCarriesAReplayToken_SendsItBackAsTheThoughtSignatureOnTheSamePart()
    {
        // Gemini 3 answers 400 when a replayed functionCall part has lost the thoughtSignature it came with.
        var request = new LlmRequest(
            "system",
            [new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", "{}", ReplayToken: "c2lnbmF0dXJl")])]);

        var part = Serialize(GeminiRequestMapper.Map(request))["contents"]![0]!["parts"]![0]!;

        part["thoughtSignature"]!.GetValue<string>().Should().Be("c2lnbmF0dXJl");
        part["functionCall"]!["name"]!.GetValue<string>().Should().Be("get_labs");
    }

    [Fact]
    public void Map_ToolUseWithoutAReplayToken_OmitsTheThoughtSignatureField()
    {
        var request = new LlmRequest(
            "system",
            [new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", "{}")])]);

        var part = Serialize(GeminiRequestMapper.Map(request))["contents"]![0]!["parts"]![0]!.AsObject();

        part.Select(p => p.Key).Should().BeEquivalentTo(["functionCall"]);
    }

    [Fact]
    public void Map_ToolUseWithASynthesizedId_OmitsTheIdOnTheWire()
    {
        // When Gemini sent no id the response mapper minted one; replaying it would hand Gemini an id it never issued.
        var syntheticId = GeminiResponseMapper.SynthesizedCallIdPrefix + "abc";
        var request = new LlmRequest(
            "system",
            [
                new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent(syntheticId, "get_labs", "{}")]),
                new LlmMessage(LlmRole.User, [new LlmToolResultContent(syntheticId, "{}")]),
            ]);

        var json = Serialize(GeminiRequestMapper.Map(request));

        json["contents"]![0]!["parts"]![0]!["functionCall"]!.AsObject().ContainsKey("id").Should().BeFalse();
        json["contents"]![1]!["parts"]![0]!["functionResponse"]!.AsObject().ContainsKey("id").Should().BeFalse();
    }

    [Fact]
    public void Map_ToolResultContent_MapsToAFunctionResponseNamedAfterTheCallItAnswers()
    {
        // Gemini's functionResponse must carry the function's name, which LlmToolResultContent does not hold:
        // it is recovered from the functionCall with the same id earlier in the history.
        var request = new LlmRequest(
            "system",
            [
                new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", "{}")]),
                new LlmMessage(LlmRole.User, [new LlmToolResultContent("call_1", """{"labs":[]}""")]),
            ]);

        var wire = GeminiRequestMapper.Map(request);

        var response = wire.Contents[1].Parts.Should().ContainSingle().Which.FunctionResponse!;
        wire.Contents[1].Role.Should().Be("user");
        response.Id.Should().Be("call_1");
        response.Name.Should().Be("get_labs");
        response.Response["output"]!["labs"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Map_ToolResultIsAnError_WrapsItUnderTheErrorKey()
    {
        var request = new LlmRequest(
            "system",
            [
                new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", "{}")]),
                new LlmMessage(LlmRole.User, [new LlmToolResultContent("call_1", """{"message":"not found"}""", IsError: true)]),
            ]);

        var response = GeminiRequestMapper.Map(request).Contents[1].Parts[0].FunctionResponse!;

        response.Response.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["error"]);
        response.Response["error"]!["message"]!.GetValue<string>().Should().Be("not found");
    }

    [Theory]
    [InlineData("""[1,2]""")]
    [InlineData("\"plain\"")]
    [InlineData("not json at all")]
    public void Map_ToolResultIsNotAJsonObject_StillSendsAnObjectResponse(string resultJson)
    {
        // functionResponse.response is a protobuf Struct, so it must be a JSON object whatever the tool returned.
        var request = new LlmRequest(
            "system",
            [
                new LlmMessage(LlmRole.Assistant, [new LlmToolUseContent("call_1", "get_labs", "{}")]),
                new LlmMessage(LlmRole.User, [new LlmToolResultContent("call_1", resultJson)]),
            ]);

        var response = GeminiRequestMapper.Map(request).Contents[1].Parts[0].FunctionResponse!;

        response.Response.GetValueKind().Should().Be(JsonValueKind.Object);
        response.Response.AsObject().ContainsKey("output").Should().BeTrue();
    }

    [Fact]
    public void Map_ToolResultAnswersNoKnownCall_Throws()
    {
        var request = new LlmRequest(
            "system",
            [new LlmMessage(LlmRole.User, [new LlmToolResultContent("call_missing", "{}")])]);

        var act = () => GeminiRequestMapper.Map(request);

        act.Should().Throw<InvalidOperationException>().WithMessage("*call_missing*");
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("application/pdf")]
    public void Map_BinaryContent_MapsToInlineDataWithMimeTypeAndBase64Data(string mediaType)
    {
        LlmContent content = mediaType.StartsWith("image/", StringComparison.Ordinal)
            ? new LlmImageContent(mediaType, "QUJD")
            : new LlmDocumentContent(mediaType, "QUJD");
        var request = new LlmRequest("system", [new LlmMessage(LlmRole.User, [content, new LlmTextContent("Extract.")])]);

        var parts = Serialize(GeminiRequestMapper.Map(request))["contents"]![0]!["parts"]!.AsArray();

        parts.Should().HaveCount(2);
        parts[0]!["inlineData"]!["mimeType"]!.GetValue<string>().Should().Be(mediaType);
        parts[0]!["inlineData"]!["data"]!.GetValue<string>().Should().Be("QUJD");
        parts[1]!["text"]!.GetValue<string>().Should().Be("Extract.");
    }

    [Fact]
    public void Map_NoToolsOffered_SerializedRequestOmitsToolsField()
    {
        var wire = GeminiRequestMapper.Map(new LlmRequest("system", [], Tools: null));

        wire.Tools.Should().BeNull();
        Serialize(wire).AsObject().ContainsKey("tools").Should().BeFalse();
    }

    [Fact]
    public void Map_EmptyToolList_SerializedRequestOmitsToolsField()
    {
        // An empty functionDeclarations list is not "no tools" to Gemini; it is an invalid Tool.
        var wire = GeminiRequestMapper.Map(new LlmRequest("system", [], Tools: []));

        Serialize(wire).AsObject().ContainsKey("tools").Should().BeFalse();
    }

    [Fact]
    public void Map_ToolsOffered_MapsToOneToolOfFunctionDeclarationsWithTheJsonSchema()
    {
        var request = new LlmRequest(
            "system",
            [],
            Tools:
            [
                new LlmToolDefinition(
                    "get_labs",
                    "Fetches lab results for the patient in context.",
                    """{"type":"object","properties":{"patientId":{"type":"string"}},"required":["patientId"]}"""),
                new LlmToolDefinition("get_vitals", "Vitals.", """{"type":"object","properties":{}}"""),
            ]);

        var wire = GeminiRequestMapper.Map(request);

        var declarations = wire.Tools.Should().ContainSingle().Which.FunctionDeclarations;
        declarations.Should().HaveCount(2);
        declarations[0].Name.Should().Be("get_labs");
        declarations[0].Description.Should().Be("Fetches lab results for the patient in context.");
        declarations[0].ParametersJsonSchema["type"]!.GetValue<string>().Should().Be("object");
        declarations[0].ParametersJsonSchema["required"]![0]!.GetValue<string>().Should().Be("patientId");
    }

    [Fact]
    public void Map_ToolSchemaCarriesMetaKeywords_StripsThemAtEveryDepth()
    {
        // $schema, $id and $comment are annotations Gemini's JSON Schema subset does not list; they change nothing
        // about the shape, so they are removed rather than risk a 400 on every turn.
        var request = new LlmRequest(
            "system",
            [],
            Tools:
            [
                new LlmToolDefinition(
                    "t",
                    "d",
                    """{"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"x","type":"object","properties":{"q":{"$comment":"c","type":"string","pattern":"^a$"}}}"""),
            ]);

        var schema = GeminiRequestMapper.Map(request).Tools![0].FunctionDeclarations[0].ParametersJsonSchema;

        schema.ToJsonString().Should().NotContain("$schema").And.NotContain("$id").And.NotContain("$comment");
        schema["properties"]!["q"]!["pattern"]!.GetValue<string>().Should().Be("^a$");
    }

    [Fact]
    public void Map_CatalogToolSchemas_AllMapWithoutLosingAProperty()
    {
        // The real catalog, not a sample: every property the model is told about must survive the sanitizer.
        var request = new LlmRequest("system", [], Tools: AgentForge.Agent.McpToolCatalog.AllTools);

        var declarations = GeminiRequestMapper.Map(request).Tools![0].FunctionDeclarations;

        declarations.Select(d => d.Name).Should().Equal(AgentForge.Agent.McpToolCatalog.AllTools.Select(t => t.Name));
        foreach (var (declaration, tool) in declarations.Zip(AgentForge.Agent.McpToolCatalog.AllTools))
        {
            declaration.ParametersJsonSchema.ToJsonString().Should().Be(JsonNode.Parse(tool.InputJsonSchema)!.ToJsonString());
        }
    }
}
