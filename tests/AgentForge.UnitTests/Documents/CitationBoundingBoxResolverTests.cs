using FluentAssertions;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;

namespace AgentForge.UnitTests.Documents;

/// <summary>
/// Unit tests for <see cref="CitationBoundingBoxResolver"/> — the pure matcher that decides whether an
/// extraction citation's quote is actually present in the source document (FR-CITE-2). It is the
/// deterministic backstop behind the extraction prompt's VERBATIM rule as well as the geometry resolver, so
/// it reports <b>three</b> outcomes, not a box-or-null: the quote was located exactly, it was searched for
/// and is not there, or there was no text to search. Guarded behavior: whitespace-insensitive matching,
/// per-page scoping, "not found" distinguished from "not checked", and no box on either negative outcome.
/// </summary>
public sealed class CitationBoundingBoxResolverTests
{
    // A lab row "Potassium 5.6 mmol/L" laid out left-to-right at the same vertical band on page 1.
    private static readonly PdfWord[] Row =
    [
        new(1, "Potassium", 0.10, 0.20, 0.15, 0.03),
        new(1, "5.6", 0.30, 0.20, 0.05, 0.03),
        new(1, "mmol/L", 0.40, 0.20, 0.08, 0.03),
    ];

    // A one-page document whose single page carries that row.
    private static readonly PdfTextLayer OnePage = new(Row, PageCount: 1);

    [Fact]
    public void Resolve_QuoteMatchesRow_ReportsExactAndTheUnionOfTheWordBoxes()
    {
        var located = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "Potassium 5.6 mmol/L");

        located.Match.Should().Be(CitationQuoteMatch.Exact);
        var box = located.BoundingBox;
        box.Should().NotBeNull();
        box!.Should().HaveCount(4);
        box[0].Should().BeApproximately(0.10, 1e-9);           // left-most x
        box[1].Should().BeApproximately(0.20, 1e-9);           // top y
        box[2].Should().BeApproximately(0.38, 1e-9);           // width: (0.40 + 0.08) - 0.10
        box[3].Should().BeApproximately(0.03, 1e-9);           // row height
    }

    [Fact]
    public void Resolve_IsWhitespaceInsensitive()
    {
        // Extra/irregular spacing in the model's quote must still match the same words.
        var located = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "  Potassium   5.6  mmol/L ");

        located.Match.Should().Be(CitationQuoteMatch.Exact);
        located.BoundingBox![0].Should().BeApproximately(0.10, 1e-9);
        located.BoundingBox[2].Should().BeApproximately(0.38, 1e-9);
    }

    [Fact]
    public void Resolve_MatchesASubRunOfTheRow()
    {
        // A shorter quote should box only the words it covers, not the whole row.
        var located = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "Potassium 5.6");

        located.Match.Should().Be(CitationQuoteMatch.Exact);
        located.BoundingBox![0].Should().BeApproximately(0.10, 1e-9);
        located.BoundingBox[2].Should().BeApproximately(0.25, 1e-9);   // (0.30 + 0.05) - 0.10
    }

    [Fact]
    public void Resolve_ScopesToTheCitedPage()
    {
        PdfWord[] twoPages =
        [
            new(1, "Potassium", 0.10, 0.90, 0.15, 0.03), new(1, "5.6", 0.30, 0.90, 0.05, 0.03),
            new(2, "Potassium", 0.10, 0.20, 0.15, 0.03), new(2, "5.6", 0.30, 0.20, 0.05, 0.03),
        ];

        var located = CitationBoundingBoxResolver.Resolve(new(twoPages, 2), page: 2, quote: "Potassium 5.6");

        located.Match.Should().Be(CitationQuoteMatch.Exact);
        located.BoundingBox![1].Should().BeApproximately(0.20, 1e-9);  // the page-2 copy, not page-1's y=0.90
    }

    [Fact]
    public void Resolve_QuoteNotInThePagesText_ReportsUnlocatableAndNoBox()
    {
        // The page's own glyphs were searched and the quote is not among them: the VERBATIM rule was broken.
        // This is the fabrication signal, and it must be a reported outcome rather than an absent box.
        var located = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "Sodium 139");

        located.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        located.BoundingBox.Should().BeNull();
    }

    [Fact]
    public void Resolve_QuoteNearlyMatches_IsStillUnlocatableUnderTheExactPolicy()
    {
        // The stated match floor is 1.0 on the folded key. A near miss - one token dropped - does not clear
        // it and must not claim a box. A separate change may add a *distinct* Fuzzy kind below this floor; it may not
        // lower this one, because Exact is the only evidence the model copied rather than composed.
        var located = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "Potassium 5.6 mmol/L (H)");

        located.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        located.BoundingBox.Should().BeNull();
    }

    [Fact]
    public void Resolve_NoTextLayerAtAll_ReportsUncheckedAndNoBox()
    {
        // A scan, an image-only or encrypted PDF, or a non-PDF: the document yields no text and reports no
        // pages. Nothing was searched, so nothing can be concluded - calling this unlocatable would libel a
        // legitimate citation. The box stays null on every negative outcome, which is the invariant
        // CitationQuoteLocation states. A separate change review NB1
        var located = CitationBoundingBoxResolver.Resolve(PdfTextLayer.None, page: 1, quote: "Potassium");

        located.Match.Should().Be(CitationQuoteMatch.Unchecked);
        located.BoundingBox.Should().BeNull();
    }

    [Fact]
    public void Resolve_PageInsideTheDocumentThatCarriesNoGlyphs_ReportsUncheckedAndNoBox()
    {
        // A scanned insert in an otherwise digital PDF: page 2 exists but has no text layer of its own. The
        // quote genuinely cannot be checked, so this stays Unchecked even though the document has glyphs.
        var mixed = new PdfTextLayer(Row, PageCount: 3);

        var located = CitationBoundingBoxResolver.Resolve(mixed, page: 2, quote: "Potassium");

        located.Match.Should().Be(CitationQuoteMatch.Unchecked);
        located.BoundingBox.Should().BeNull();
    }

    [Fact]
    public void Resolve_PageTheDocumentDoesNotHave_ReportsUnlocatable()
    {
        // a separate change review, blocking finding 1. A citation naming page 3 of a two-page document is
        // not "we could not check" - the page is not in the document, so the quote cannot be in it either.
        // Scoring this Unchecked put a model that fabricates the page number ALONGSIDE the quote into the
        // same bucket as every scan, which is the one bucket the unlocatable/exact ratio cannot see into.
        var twoPages = new PdfTextLayer(Row, PageCount: 2);

        var located = CitationBoundingBoxResolver.Resolve(twoPages, page: 3, quote: "Potassium 5.6");

        located.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        located.BoundingBox.Should().BeNull();
    }

    [Fact]
    public void Resolve_PageNumberBelowOne_ReportsUnlocatable()
    {
        // Pages are 1-based in the contract. Zero or negative names no page in any document.
        CitationBoundingBoxResolver.Resolve(OnePage, page: 0, quote: "Potassium")
            .Match.Should().Be(CitationQuoteMatch.Unlocatable);
    }

    [Fact]
    public void Resolve_EmptyQuote_ReportsUnlocatableWhetherOrNotThePageHasText()
    {
        // An empty quote grounds nothing in either case, so a missing text layer must not upgrade it to
        // "not checked" - which would score it 0.5 instead of 0.0 downstream. A separate change review
        var withText = CitationBoundingBoxResolver.Resolve(OnePage, page: 1, quote: "   ");
        var withoutText = CitationBoundingBoxResolver.Resolve(PdfTextLayer.None, page: 1, quote: "   ");

        withText.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        withText.BoundingBox.Should().BeNull();
        withoutText.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        withoutText.BoundingBox.Should().BeNull();
    }
}
