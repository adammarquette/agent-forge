using System.Text.Json.Nodes;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.OpenApi;

/// <summary>
/// The contract test is only as useful as what it says when it goes red. These pin the three drifts the
/// requirement names - a route the code serves that the spec lacks, a route the spec keeps that the code
/// dropped, and a changed shape - and that each one is <i>named</i>, not just detected.
/// </summary>
public sealed class OpenApiDocumentDiffTests
{
    private const string Spec = """
        {
          "openapi": "3.0.4",
          "paths": {
            "/evidence/ask": { "post": { "operationId": "Ask" } },
            "/patient": { "get": { "operationId": "Patient" } }
          },
          "components": { "schemas": { "Answer": { "type": "object", "properties": { "text": { "type": "string" } } } } }
        }
        """;

    [Fact]
    public void Compare_IdenticalDocuments_ReportsNothing()
    {
        OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), JsonNode.Parse(Spec)).Should().BeEmpty();
    }

    [Fact]
    public void Compare_ImplementationServesARouteTheSpecLacks_NamesTheRouteAsMissingFromTheSpec()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["paths"]!["/documents/ingest"] = JsonNode.Parse("""{ "post": { "operationId": "Ingest" } }""");

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should()
            .Be("route served by the implementation but missing from the committed spec: POST /documents/ingest");
    }

    [Fact]
    public void Compare_SpecKeepsARouteTheImplementationDropped_NamesTheRouteAsNoLongerServed()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["paths"]!.AsObject().Remove("/patient");

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should()
            .Be("route in the committed spec that the implementation no longer serves: GET /patient");
    }

    [Fact]
    public void Compare_DriftInBothDirectionsAndAShapeChange_ReportsEveryOne()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["paths"]!.AsObject().Remove("/patient");
        generated["paths"]!["/documents/ingest"] = JsonNode.Parse("""{ "post": { "operationId": "Ingest" } }""");
        generated["components"]!["schemas"]!["Answer"]!["properties"]!["text"]!["type"] = "integer";

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().HaveCount(3);
        differences.Should().Contain(d => d.EndsWith("POST /documents/ingest", StringComparison.Ordinal))
            .And.Contain(d => d.EndsWith("GET /patient", StringComparison.Ordinal))
            .And.Contain(d => d.StartsWith("$.components.schemas.Answer.properties.text.type", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_SameRouteGainsAMethod_NamesTheOperationNotTheWholePath()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["paths"]!["/patient"]!["delete"] = JsonNode.Parse("""{ "operationId": "Forget" }""");

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should().EndWith(": DELETE /patient");
    }

    [Fact]
    public void Compare_SchemaPropertyChangesType_NamesThePathAndBothValues()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["components"]!["schemas"]!["Answer"]!["properties"]!["text"]!["type"] = "integer";

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should()
            .Be("$.components.schemas.Answer.properties.text.type: committed spec has \"string\", implementation has \"integer\"");
    }

    [Fact]
    public void Compare_OperationGainsAField_NamesItAsMissingFromTheSpec()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["paths"]!["/evidence/ask"]!["post"]!["deprecated"] = true;

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should()
            .Be("$.paths['/evidence/ask'].post.deprecated: in the implementation, missing from the committed spec (implementation: true)");
    }

    [Fact]
    public void Compare_SchemaLosesAProperty_NamesItAsNoLongerInTheImplementation()
    {
        var generated = JsonNode.Parse(Spec)!;
        generated["components"]!["schemas"]!["Answer"]!["properties"]!.AsObject().Remove("text");

        var differences = OpenApiDocumentDiff.Compare(JsonNode.Parse(Spec), generated);

        differences.Should().ContainSingle().Which.Should()
            .StartWith("$.components.schemas.Answer.properties.text: in the committed spec, no longer in the implementation");
    }
}
