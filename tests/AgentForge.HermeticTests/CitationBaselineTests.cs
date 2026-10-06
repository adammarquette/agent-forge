using System.Globalization;
using System.Text;
using AgentForge.Agents.Ingestion;
using AgentForge.Data.Entities;
using AgentForge.GenerateFixtureDocuments;
using AgentForge.HermeticTests.Support;
using FluentAssertions;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace AgentForge.HermeticTests;

/// <summary>Where a fact's click-to-source citation lands, judged against the manifest span it should cite.</summary>
public enum CitationLanding
{
    /// <summary>A box, and the glyphs inside it are exactly the fact's own span.</summary>
    Box,

    /// <summary>A box, but around other text - the fact shares a citation that quotes something else.</summary>
    BoxElsewhere,

    /// <summary>No box: the quote was searched for and not located, so click-to-source opens the page.</summary>
    PageLevel,

    /// <summary>No text layer to check against; the model's own estimate is the only overlay.</summary>
    Unchecked,
}

/// <summary>
/// The citation baseline <c>a separate change</c> is measured against. Every document in the set goes through real
/// extraction - the schema gate, PdfPig's text layer and <c>CitationBoundingBoxResolver</c> - with a model that
/// quotes each manifest span verbatim, and each stored fact is scored by where its citation actually lands:
/// on its own glyphs, on somebody else's, at page level, or unchecked. A model this faithful puts every miss
/// on the layout and the resolver, so the counts are the resolver's, not the model's.
/// <para>
/// <b>Failure mode guarded (regression, FR-CITE-2):</b> the count of facts that reach a box moving without
/// anyone deciding it should. It is pinned: a change that boxes more updates the
/// pin deliberately, and one that boxes fewer fails here first.
/// </para>
/// </summary>
public sealed class CitationBaselineTests(ITestOutputHelper output)
{
    /// <summary>
    /// The pinned baseline: per document, how many facts land in each <see cref="CitationLanding"/>. Over the
    /// set, 44 of 66 facts land on their own glyphs, none on another span's box, 3 at page level and 19
    /// unchecked - so 44 of the 47 facts on a text layer are boxed on their own span. The misses: the two
    /// troponin rows whose names wrap (the row is not one contiguous word run) and the eGFR row whose unit
    /// carries a superscript. First measured 2026-09-25 at 37 / 7 / 3 / 19; the 7 boxed elsewhere were every
    /// intake chief concern, allergy and family-history item, which cited only through the form-level (name
    /// line) citation until each got its own.
    /// </summary>
    private static readonly Dictionary<string, (int Box, int BoxElsewhere, int PageLevel, int Unchecked)> Baseline = new()
    {
        ["synthetic-lab-panel.pdf"] = (4, 0, 0, 0),
        ["lab-lipid-panel.pdf"] = (5, 0, 0, 0),
        ["lab-bmp-two-column.pdf"] = (8, 0, 0, 0),
        ["lab-cardiac-markers-wrapped.pdf"] = (3, 0, 2, 0),
        ["lab-inr-warfarin-multipage.pdf"] = (5, 0, 0, 0),
        ["lab-hba1c-split-units.pdf"] = (3, 0, 1, 0),
        ["lab-lipid-panel-scanned.pdf"] = (0, 0, 0, 5),
        ["synthetic-intake-form.png"] = (0, 0, 0, 6),
        ["intake-form-digital.pdf"] = (7, 0, 0, 0),
        ["intake-form-multipage.pdf"] = (9, 0, 0, 0),
        ["intake-form-scanned.pdf"] = (0, 0, 0, 8),
    };

    [Fact]
    public async Task ExtractAndResolve_WholeSet_LandsEachFactWhereTheBaselineSays()
    {
        await using var harness = await DocumentSetHarness.CreateAsync();
        var observed = new Dictionary<string, (int Box, int BoxElsewhere, int PageLevel, int Unchecked)>();
        var report = new StringBuilder("document | layout | box | box elsewhere | page level | unchecked\n");
        var misses = new StringBuilder("Facts on a text layer that did not land on their own span:\n");

        foreach (var document in FixtureDocuments.All)
        {
            (await harness.IngestAsync(document)).Status.Should().Be(DocumentIngestionStatus.Ingested);
            var stored = (await harness.StoredAsync(document))!;
            var bytes = await DocumentSetHarness.ReadAsync(document);
            var landings = document.Manifest.Facts
                .Select(f => Landing(bytes, f, stored.DerivedFacts.Single(d => d.FactType == f.FactType && d.Citation.QuoteOrValue == f.Quote)))
                .ToList();
            var counts = (
                landings.Count(l => l == CitationLanding.Box),
                landings.Count(l => l == CitationLanding.BoxElsewhere),
                landings.Count(l => l == CitationLanding.PageLevel),
                landings.Count(l => l == CitationLanding.Unchecked));
            observed[document.FileName] = counts;
            report.Append(CultureInfo.InvariantCulture,
                $"{document.FileName} | {document.Manifest.Layout} | {counts.Item1} | {counts.Item2} | {counts.Item3} | {counts.Item4}\n");
            foreach (var (fact, landing) in document.Manifest.Facts.Zip(landings))
            {
                if (landing is CitationLanding.PageLevel or CitationLanding.BoxElsewhere)
                {
                    misses.Append(CultureInfo.InvariantCulture, $"  {document.FileName}: {landing} - {fact.FactType} '{fact.Quote}'\n");
                }
            }
        }

        var total = observed.Values.Aggregate((0, 0, 0, 0), (a, c) => (a.Item1 + c.Box, a.Item2 + c.BoxElsewhere, a.Item3 + c.PageLevel, a.Item4 + c.Unchecked));
        report.Append(CultureInfo.InvariantCulture, $"TOTAL | | {total.Item1} | {total.Item2} | {total.Item3} | {total.Item4}\n");
        output.WriteLine(report.ToString());
        output.WriteLine(misses.ToString());

        observed.Should().BeEquivalentTo(Baseline,
            "the citation baseline moved - if that was the point of the change, re-pin it and say so in the MR");
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant):</b> the scoring itself. A box counts as landing on its fact only
    /// if PdfPig's own glyphs inside it spell the manifest span - checked independently of the resolver that
    /// drew it, so a resolver that boxed the wrong row cannot score itself a hit.
    /// </summary>
    [Fact]
    public void Landing_BoxAroundAnotherRow_IsNotScoredAsABoxOnTheFact()
    {
        var lab = FixtureDocuments.All.Single(d => d.FileName == SyntheticLabPanel.FileName);
        var bytes = lab.Render();
        var potassium = lab.Manifest.Facts[0];
        var creatinine = lab.Manifest.Facts[1];
        var creatinineBox = BoxOf(bytes, creatinine);

        var landing = Landing(bytes, potassium, new DerivedFact
        {
            FactType = potassium.FactType,
            PayloadJson = "{}",
            ExtractionConfidence = 1.0,
            Citation = new Citation
            {
                SourceType = CitationSourceType.Derived,
                SourceId = "x",
                PageOrSection = "1",
                QuoteOrValue = potassium.Quote,
                BoundingBox = creatinineBox,
            },
        });

        landing.Should().Be(CitationLanding.BoxElsewhere);
    }

    private static CitationLanding Landing(byte[] bytes, ExpectedFact expected, DerivedFact fact)
    {
        switch (fact.ExtractionConfidence)
        {
            case 0.5:
                return CitationLanding.Unchecked;
            case 0.0:
                fact.Citation.BoundingBox.Should().BeNull("an unlocatable quote must not keep a box");
                return CitationLanding.PageLevel;
        }

        fact.ExtractionConfidence.Should().Be(1.0);
        fact.Citation.BoundingBox.Should().NotBeNull("an exact match carries its glyph box");
        return Folded(WordsInBox(bytes, expected.Page, fact.Citation.BoundingBox!)) == Folded(expected.Quote)
            ? CitationLanding.Box
            : CitationLanding.BoxElsewhere;
    }

    // The union of the glyph boxes of the words spelling the quote, straight from PdfPig: the scoring's red control.
    private static double[] BoxOf(byte[] pdf, ExpectedFact fact)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(fact.Page);
        var tokens = fact.Quote.Split(' ');
        var words = page.GetWords().ToList();
        var start = Enumerable.Range(0, words.Count - tokens.Length + 1)
            .First(i => tokens.Select((t, k) => words[i + k].Text == t).All(b => b));
        var run = words.Skip(start).Take(tokens.Length).ToList();
        var left = run.Min(w => w.BoundingBox.Left);
        var right = run.Max(w => w.BoundingBox.Right);
        var top = run.Max(w => w.BoundingBox.Top);
        var bottom = run.Min(w => w.BoundingBox.Bottom);
        return [left / page.Width, (page.Height - top) / page.Height, (right - left) / page.Width, (top - bottom) / page.Height];
    }

    // Independent of the production resolver: PdfPig's words on the page whose centres fall inside the box.
    private static string WordsInBox(byte[] pdf, int pageNumber, double[] box)
    {
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(pageNumber);
        var words = page.GetWords().Where(w =>
        {
            var cx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2 / page.Width;
            var cy = (page.Height - ((w.BoundingBox.Top + w.BoundingBox.Bottom) / 2)) / page.Height;
            return cx >= box[0] && cx <= box[0] + box[2] && cy >= box[1] && cy <= box[1] + box[3];
        });
        return string.Concat(words.Select(w => w.Text));
    }

    // Order- and spacing-insensitive: the same glyphs, however the text layer happens to sequence them.
    private static string Folded(string text) =>
        new([.. text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).Order()]);
}
