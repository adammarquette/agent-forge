using System.Globalization;
using System.Text.Json;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;

namespace AgentForge.Agents.Ingestion;

/// <inheritdoc />
public sealed class DerivedFactMapper : IDerivedFactMapper
{
    /// <inheritdoc />
    public IReadOnlyList<DerivedFact> Map(DocumentExtractionResult extraction, string? documentReferenceId)
    {
        if (!extraction.Succeeded || extraction.CanonicalJson is not { } json)
        {
            return [];
        }

        // Empty string (not null) marks a pending citation — Citation.SourceId is a required column.
        var sourceId = documentReferenceId ?? string.Empty;
        return extraction.DocumentType switch
        {
            ClinicalDocumentType.LabPdf => MapLab(json, sourceId),
            ClinicalDocumentType.IntakeForm => MapIntake(json, sourceId),
            _ => [],
        };
    }

    private static List<DerivedFact> MapLab(string json, string sourceId)
    {
        var lab = JsonSerializer.Deserialize(json, DerivedFactJsonContext.Default.LabExtraction)
            ?? throw new JsonException("Canonical lab extraction did not deserialize.");

        var facts = new List<DerivedFact>(lab.Tests.Count);
        foreach (var test in lab.Tests)
        {
            facts.Add(new DerivedFact
            {
                FactType = DerivedFactType.LabResult,
                PayloadJson = JsonSerializer.Serialize(test, DerivedFactJsonContext.Default.LabTestResult),
                Citation = Cite(sourceId, test.Citation, test.TestName, test.Citation.Quote),
                ExtractionConfidence = ConfidenceFrom(test.Citation),
            });
        }

        return facts;
    }

    private static List<DerivedFact> MapIntake(string json, string sourceId)
    {
        var intake = JsonSerializer.Deserialize(json, DerivedFactJsonContext.Default.IntakeExtraction)
            ?? throw new JsonException("Canonical intake extraction did not deserialize.");

        var facts = new List<DerivedFact>
        {
            new()
            {
                FactType = DerivedFactType.IntakeDemographics,
                PayloadJson = JsonSerializer.Serialize(intake.Demographics, DerivedFactJsonContext.Default.IntakeDemographics),
                Citation = Cite(sourceId, intake.Citation, "demographics", intake.Citation.Quote),
                ExtractionConfidence = ConfidenceFrom(intake.Citation),
            },
        };

        if (intake.ChiefConcern is { } concern && !string.IsNullOrWhiteSpace(concern.Text))
        {
            facts.Add(TextFact(DerivedFactType.IntakeChiefConcern, "chief_concern", concern, sourceId));
        }

        foreach (var medication in intake.CurrentMedications)
        {
            facts.Add(new DerivedFact
            {
                FactType = DerivedFactType.IntakeMedication,
                PayloadJson = JsonSerializer.Serialize(medication, DerivedFactJsonContext.Default.IntakeMedication),
                Citation = Cite(sourceId, medication.Citation, medication.Name, medication.Citation.Quote),
                ExtractionConfidence = ConfidenceFrom(medication.Citation),
            });
        }

        foreach (var allergy in intake.Allergies)
        {
            facts.Add(TextFact(DerivedFactType.IntakeAllergy, "allergy", allergy, sourceId));
        }

        foreach (var item in intake.FamilyHistory)
        {
            facts.Add(TextFact(DerivedFactType.IntakeFamilyHistory, "family_history", item, sourceId));
        }

        return facts;
    }

    // Each free-text item cites and is scored on its OWN citation. They used to borrow the form-level one,
    // so they landed on the name line and inherited its `exact` - an invented allergy stored at 1.0.
    private static DerivedFact TextFact(string factType, string field, IntakeTextItem item, string sourceId) =>
        new()
        {
            FactType = factType,
            PayloadJson = JsonSerializer.Serialize(new TextFactPayload(item.Text), DerivedFactJsonContext.Default.TextFactPayload),
            Citation = Cite(sourceId, item.Citation, field, item.Citation.Quote),
            ExtractionConfidence = ConfidenceFrom(item.Citation),
        };

    // Grounding/locatability confidence for a derived fact, read off what the extractor FOUND rather than
    // off whether a box is present. A separate change (FR-OBS-W2-1), a separate change.
    //
    // It used to be `BoundingBox is not null`, and that quietly scored fabrication at full confidence: an
    // unlocatable quote kept the model's own estimated box, so a quote that is nowhere in the document was
    // indistinguishable here from one copied verbatim off it. A box the model drew is not evidence that the
    // text it encloses says what the citation claims.
    //
    // The three values and their meanings live on ExtractionConfidenceScore, because DocumentIngestionService
    // reads the outcome back off the stored number to meter it and the two must not drift. A separate change
    private static double ConfidenceFrom(ExtractionCitation citation) =>
        ExtractionConfidenceScore.For(citation.Match);

    private static Citation Cite(string sourceId, ExtractionCitation citation, string field, string? quote) => new()
    {
        SourceType = CitationSourceType.Derived,
        SourceId = sourceId,
        PageOrSection = citation.Page.ToString(CultureInfo.InvariantCulture),
        FieldOrChunkId = field,
        QuoteOrValue = quote,
        BoundingBox = citation.BoundingBox,
    };
}
