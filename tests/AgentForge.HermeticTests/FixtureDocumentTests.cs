using System.Text.RegularExpressions;
using AgentForge.Agents.Ingestion;
using AgentForge.GenerateFixtureDocuments;
using AgentForge.HermeticTests.Support;
using FluentAssertions;
using UglyToad.PdfPig;

namespace AgentForge.HermeticTests;

/// <summary>
/// Holds the committed fixture set - documents and their manifests - to its generator, and each manifest to
/// its document. <b>Failure mode guarded (regression):</b> a fixture hand-edited, re-exported or regenerated
/// by a different tool drifts from the strings the scripted model quotes, and the pipeline test then fails for
/// a reason that has nothing to do with the pipeline - or, worse, a pinned sha256 is updated to
/// bytes nobody can reproduce. Separate changes
/// </summary>
public sealed partial class FixtureDocumentTests
{
    public static TheoryData<string> Files() => new(FixtureDocuments.Files.Select(f => f.FileName));

    public static TheoryData<string> TextLayerDocuments() => new(
        FixtureDocuments.All.Where(d => d.Manifest.HasTextLayer).Select(d => d.FileName));

    public static TheoryData<string> Documents() => new(FixtureDocuments.All.Select(d => d.FileName));

    [Theory]
    [MemberData(nameof(Files))]
    public async Task CommittedFixture_ComparedWithTheGenerator_IsByteIdentical(string fileName)
    {
        var committed = await File.ReadAllBytesAsync(HermeticPipeline.FixturePath(fileName));

        var generated = FixtureDocuments.Files.Single(d => d.FileName == fileName).Render();

        committed.Should().Equal(generated,
            "regenerate with `dotnet run --project tools/GenerateFixtureDocuments` and re-pin its sha256");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Render_CalledTwice_IsByteIdentical(string fileName)
    {
        var file = FixtureDocuments.Files.Single(d => d.FileName == fileName);

        file.Render().Should().Equal(file.Render(), "no clock, randomness or machine state may reach the bytes");
    }

    [Fact]
    public void LabPanel_ReadByPdfPig_HasOnePageWhoseTextCarriesEveryRowVerbatim()
    {
        using var document = PdfDocument.Open(SyntheticLabPanel.Render());

        var text = string.Join(' ', document.GetPage(1).GetWords().Select(w => w.Text));

        document.NumberOfPages.Should().Be(1);
        foreach (var row in SyntheticLabPanel.Rows)
        {
            text.Should().Contain(row.Quote, "a verbatim quote of the row has to be findable in the text layer");
        }
    }

    /// <summary>
    /// The set covers what asks of it: both document types in number, and each imperfect variant the
    /// resolver is brittle to at least once.
    /// </summary>
    [Fact]
    public void DocumentSet_Composition_CoversBothTypesAndEveryImperfectVariant()
    {
        var manifests = FixtureDocuments.All.Select(d => d.Manifest).ToList();

        manifests.Count(m => m.DocumentType == DocumentManifest.LabPdf).Should().BeGreaterThanOrEqualTo(6);
        manifests.Count(m => m.DocumentType == DocumentManifest.IntakeForm).Should().BeGreaterThanOrEqualTo(4);
        manifests.Select(m => m.Layout).Should().Contain(
            [DocumentLayout.Scanned, DocumentLayout.MultiPage, DocumentLayout.SplitUnits, DocumentLayout.TwoColumn, DocumentLayout.WrappedTable]);
        manifests.Where(m => m.Layout == DocumentLayout.Scanned).Select(m => m.DocumentType).Should()
            .Contain([DocumentManifest.LabPdf, DocumentManifest.IntakeForm], "a scan of each document type");
        manifests.Where(m => m.PageCount > 1).Select(m => m.DocumentType).Should()
            .Contain([DocumentManifest.LabPdf, DocumentManifest.IntakeForm], "a multi-page document of each type");
        FixtureDocuments.All.Select(d => d.FileName).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant):</b> a manifest that promises a span the document does not print,
    /// or prints on another page - every downstream count would then be measuring the manifest, not the
    /// pipeline. Read token by token off the page's glyphs in drawing order, not off PdfPig's word grouping -
    /// which is what the resolver reads, and what several layouts in the set exist to trip up.
    /// </summary>
    [Theory]
    [MemberData(nameof(TextLayerDocuments))]
    public void Manifest_OfATextLayerDocument_EveryQuoteIsPrintedOnItsPage(string fileName)
    {
        var fixture = FixtureDocuments.All.Single(d => d.FileName == fileName);
        using var document = PdfDocument.Open(fixture.Render());

        document.NumberOfPages.Should().Be(fixture.Manifest.PageCount);
        foreach (var fact in fixture.Manifest.Facts)
        {
            var printed = string.Concat(document.GetPage(fact.Page).Letters.Select(l => l.Value)).Replace(" ", string.Empty, StringComparison.Ordinal);
            foreach (var token in fact.Quote.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                printed.Should().Contain(token, $"'{fact.Quote}' is printed on page {fact.Page} of {fileName}");
            }
        }
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant):</b> a scan that is not one - a raster document PdfPig can read
    /// text from would score its quotes as located, and the set would have no <c>unchecked</c> case at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Manifest_HasTextLayer_AgreesWithWhatPdfPigReads(string fileName)
    {
        var fixture = FixtureDocuments.All.Single(d => d.FileName == fileName);
        if (fixture.MediaType != "application/pdf")
        {
            fixture.Manifest.HasTextLayer.Should().BeFalse("an image upload has no text layer");
            return;
        }

        using var document = PdfDocument.Open(fixture.Render());
        var words = Enumerable.Range(1, document.NumberOfPages).Sum(p => document.GetPage(p).GetWords().Count());

        (words > 0).Should().Be(fixture.Manifest.HasTextLayer);
        if (!fixture.Manifest.HasTextLayer)
        {
            fixture.Manifest.Facts.Should().OnlyContain(f => f.Region != null && f.Region.Count == 4,
                "a scan's manifest carries where each span really sits, since nothing can be read back from it");
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Manifest_EveryFact_IsAFactTypeTheMapperEmitsWithANonEmptyQuoteOnARealPage(string fileName)
    {
        var manifest = FixtureDocuments.All.Single(d => d.FileName == fileName).Manifest;

        manifest.Facts.Should().NotBeEmpty();
        manifest.Facts.Should().OnlyContain(f => DerivedFactType.All.Contains(f.FactType));
        manifest.Facts.Should().OnlyContain(f => f.Quote.Trim().Length > 0 && f.Page >= 1 && f.Page <= manifest.PageCount);
        manifest.Facts.Select(f => (f.FactType, f.Quote)).Should().OnlyHaveUniqueItems(
            "each fact is found again by its type and quote");
        manifest.DocumentType.Should().BeOneOf(DocumentManifest.LabPdf, DocumentManifest.IntakeForm);
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant: synthetic data only):</b> something in the set that could be a real
    /// person - every patient is a seeded <c>Demo</c> patient, and every MRN sits in the <c>SYN-</c>
    /// namespace no real system issues.
    /// </summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Manifest_Patient_IsObviouslySynthetic(string fileName)
    {
        var patient = FixtureDocuments.All.Single(d => d.FileName == fileName).Manifest.Patient;

        patient.Name.Should().StartWith("Demo ");
        SyntheticMrn().IsMatch(patient.Mrn).Should().BeTrue($"'{patient.Mrn}' is in the SYN- namespace");
        SyntheticCohort.Patients.Should().Contain(patient, "every document belongs to the fixed cohort");
    }

    [GeneratedRegex(@"^SYN-\d{4}(-\d{2})?$")]
    private static partial Regex SyntheticMrn();
}
