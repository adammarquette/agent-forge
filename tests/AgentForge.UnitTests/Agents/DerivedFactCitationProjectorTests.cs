using FluentAssertions;
using AgentForge.Agents;
using AgentForge.Data.Entities;

namespace AgentForge.UnitTests.Agents;

/// <summary>
/// Unit tests for <see cref="DerivedFactCitationProjector"/> — the read-side that turns persisted
/// <see cref="DerivedFact"/> rows into click-to-source citations for the production overlay (FR-CITE-2,
/// a separate change). Each citation carries the OpenEMR <c>DocumentReference</c> id so the client can fetch the
/// source PDF, plus the page + bounding box. Guarded behavior: the DocumentReference id resolves (document
/// navigation preferred, citation SourceId fallback); a fact with no resolvable document or no value is
/// skipped rather than surfaced as an unfetchable citation.
/// </summary>
public sealed class DerivedFactCitationProjectorTests
{
    private static DerivedFact Fact(
        string idHex8, string factType, string? sourceId, string? docRefOnDocument,
        string? page, string? quote, double[]? bbox, string? fieldOrChunkId = null) =>
        new()
        {
            Id = new Guid(idHex8 + "-0000-0000-0000-000000000000"),
            FactType = factType,
            PayloadJson = "{}",
            Document = docRefOnDocument is null ? null : new IngestedDocument
            {
                PatientId = "p1",
                ContentHash = "h",
                OpenEmrDocumentReferenceId = docRefOnDocument,
            },
            Citation = new Citation
            {
                SourceType = CitationSourceType.Derived,
                SourceId = sourceId ?? "",
                PageOrSection = page,
                FieldOrChunkId = fieldOrChunkId,
                QuoteOrValue = quote,
                BoundingBox = bbox,
            },
        };

    [Fact]
    public void Project_FactWithDocRefPageBboxAndValue_ReturnsAFetchableCitation()
    {
        var facts = new[]
        {
            Fact("abcdef12", "lab.result", sourceId: "docref-9", docRefOnDocument: null,
                page: "2", quote: "Potassium 5.6 (H)", bbox: [0.1, 0.2, 0.3, 0.03]),
        };

        var citations = DerivedFactCitationProjector.Project(facts);

        var c = citations.Should().ContainSingle().Subject;
        c.FactId.Should().Be("abcdef12");            // the [Derived/<slug>] id (first 8 of the fact id)
        c.SourceDocumentId.Should().Be("docref-9");  // so the client can GET /evidence/document/docref-9
        c.Page.Should().Be(2);
        c.BoundingBox.Should().Equal(0.1, 0.2, 0.3, 0.03);
        c.Quote.Should().Be("Potassium 5.6 (H)");
    }

    [Fact]
    public void Project_PrefersTheDocumentReferenceIdOnTheDocument_OverCitationSourceId()
    {
        var facts = new[]
        {
            Fact("11111111", "lab.result", sourceId: "citation-src", docRefOnDocument: "doc-authoritative",
                page: "1", quote: "INR 3.8", bbox: [0, 0, 1, 0.02]),
        };

        DerivedFactCitationProjector.Project(facts).Single().SourceDocumentId.Should().Be("doc-authoritative");
    }

    [Fact]
    public void Project_MissingPage_DefaultsToPageOne()
    {
        var facts = new[] { Fact("22222222", "lab.result", "d", null, page: null, quote: "x", bbox: null) };

        DerivedFactCitationProjector.Project(facts).Single().Page.Should().Be(1);
    }

    [Fact]
    public void Project_SkipsFactsWithNoResolvableDocumentOrNoValue()
    {
        var facts = new[]
        {
            Fact("33333333", "lab.result", sourceId: "", docRefOnDocument: null, page: "1", quote: "has value", bbox: null),
            Fact("44444444", "lab.result", sourceId: "d", docRefOnDocument: null, page: "1", quote: null, bbox: null),
        };

        DerivedFactCitationProjector.Project(facts).Should().BeEmpty();
    }

    // The Week 2 brief's minimum citation shape {source_type, source_id, page_or_section, field_or_chunk_id,
    // quote_or_value} is what the entity persists; the wire citation must carry it too.
    [Fact]
    public void Project_PersistedFact_CarriesTheBriefsFiveFieldCitationShapeFromTheEntity()
    {
        var facts = new[]
        {
            Fact("abcdef12", "lab.result", sourceId: "docref-9", docRefOnDocument: null,
                page: "2", quote: "Potassium 5.6 (H)", bbox: null, fieldOrChunkId: "Potassium"),
        };

        var c = DerivedFactCitationProjector.Project(facts).Single();

        c.SourceType.Should().Be(CitationSourceType.Derived);
        c.SourceId.Should().Be("docref-9");
        c.PageOrSection.Should().Be("2");
        c.FieldOrChunkId.Should().Be("Potassium");
        c.QuoteOrValue.Should().Be("Potassium 5.6 (H)");
    }

    [Fact]
    public void Project_SourceId_IsTheSameResolvedDocumentIdTheOverlayFetches()
    {
        // A pending citation persists SourceId "" until the DocumentReference id is known on the document row;
        // source_id must name the resolved source, never the empty placeholder.
        var facts = new[]
        {
            Fact("11111111", "lab.result", sourceId: "", docRefOnDocument: "doc-authoritative",
                page: "1", quote: "INR 3.8", bbox: null),
        };

        var c = DerivedFactCitationProjector.Project(facts).Single();

        c.SourceId.Should().Be("doc-authoritative").And.Be(c.SourceDocumentId);
    }

    [Fact]
    public void Project_EntityWithoutPageOrField_StillFillsEveryFieldOfTheShape()
    {
        var facts = new[] { Fact("22222222", "lab.result", "d", null, page: null, quote: "x", bbox: null) };

        var c = DerivedFactCitationProjector.Project(facts).Single();

        c.PageOrSection.Should().Be("1", "it falls back to the page the overlay opens");
        c.FieldOrChunkId.Should().Be("lab.result", "it falls back to the fact type the Field column carries");
    }
}
