using System.Text.Json;
using AgentForge.Agents;
using AgentForge.Data.Entities;
using FluentAssertions;

namespace AgentForge.UnitTests.Agents;

/// <summary>
/// Guards the wire form of <see cref="DocumentCitation"/> as the evidence answer and the chat payload send it:
/// it carries the Week 2 brief's minimum citation shape <c>{source_type, source_id, page_or_section,
/// field_or_chunk_id, quote_or_value}</c> (FR-CITE-1), spelled in the API's camelCase, with
/// <c>sourceType</c> as a string token rather than an enum ordinal - and the change that added it is additive,
/// so every field the existing clients read is still there under its old name.
/// </summary>
public sealed class DocumentCitationWireShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(DocumentCitation citation) =>
        JsonDocument.Parse(JsonSerializer.Serialize(citation, Web)).RootElement.Clone();

    private static DocumentCitation Sample() =>
        new("INR", "INR", "3.4", 1, [0.1, 0.2, 0.3, 0.04], "INR 3.4 (H)", "docref-9")
        {
            SourceType = CitationSourceType.Derived,
            SourceId = "docref-9",
            PageOrSection = "1",
            FieldOrChunkId = "INR",
            QuoteOrValue = "INR 3.4 (H)",
        };

    [Fact]
    public void Serialize_Citation_CarriesAllFiveFieldsOfTheBriefsShape()
    {
        var json = Serialize(Sample());

        json.GetProperty("sourceType").GetString().Should().Be("derived");
        json.GetProperty("sourceId").GetString().Should().Be("docref-9");
        json.GetProperty("pageOrSection").GetString().Should().Be("1");
        json.GetProperty("fieldOrChunkId").GetString().Should().Be("INR");
        json.GetProperty("quoteOrValue").GetString().Should().Be("INR 3.4 (H)");
    }

    [Theory]
    [InlineData(CitationSourceType.Fhir, "fhir")]
    [InlineData(CitationSourceType.Derived, "derived")]
    [InlineData(CitationSourceType.Guideline, "guideline")]
    public void Serialize_SourceType_IsTheLowercaseTokenNotTheOrdinal(CitationSourceType type, string token)
    {
        Serialize(Sample() with { SourceType = type }).GetProperty("sourceType").GetString().Should().Be(token);
    }

    [Fact]
    public void Serialize_Citation_KeepsEveryFieldTheExistingClientsRead()
    {
        // index.html and evidence.html read these; renaming or dropping one breaks the overlay.
        var names = Serialize(Sample()).EnumerateObject().Select(p => p.Name);

        names.Should().Contain(["factId", "field", "value", "page", "boundingBox", "quote", "sourceDocumentId"]);
    }

    [Fact]
    public void Deserialize_WireForm_RoundTrips()
    {
        var original = Sample();

        var back = JsonSerializer.Deserialize<DocumentCitation>(JsonSerializer.Serialize(original, Web), Web);

        back!.SourceType.Should().Be(original.SourceType);
        back.SourceId.Should().Be(original.SourceId);
        back.QuoteOrValue.Should().Be(original.QuoteOrValue);
    }
}
