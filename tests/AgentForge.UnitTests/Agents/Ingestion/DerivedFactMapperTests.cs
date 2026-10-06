using FluentAssertions;
using AgentForge.Agents.Ingestion;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Llm;

namespace AgentForge.UnitTests.Agents.Ingestion;

/// <summary>
/// Drives <see cref="DerivedFactMapper"/> — the per-schema citation shaping (ARCHITECTURE-DOCUMENTS.md §7). Each
/// extracted fact becomes one <see cref="DerivedFact"/> citing the OpenEMR DocumentReference, carrying the
/// page/quote/bbox from the extraction. A rejected extraction maps to nothing; a pending citation gets an
/// empty source id.
/// </summary>
public sealed class DerivedFactMapperTests
{
    private static readonly LlmUsage NoUsage = new(0, 0, 0m);

    private const string LabJson =
        """
        {"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","bounding_box":[0.1,0.2,0.3,0.4],"match":"exact"}}]}
        """;

    private const string IntakeJson =
        """
        {"demographics":{"full_name":"Jane Synthetic","date_of_birth":"1970-01-01","sex":"F"},"chief_concern":{"text":"palpitations","citation":{"page":1,"quote":"Reason for visit: palpitations","match":"exact"}},"current_medications":[{"name":"Metoprolol","dose":"25mg","citation":{"page":2,"quote":"Metoprolol 25mg"}}],"allergies":[{"text":"penicillin","citation":{"page":2,"quote":"Penicillin - hives","bounding_box":[0.1,0.5,0.2,0.02],"match":"exact"}}],"family_history":[{"text":"father MI at 60","citation":{"page":2,"quote":"Father: MI at 60","match":"exact"}}],"citation":{"page":1,"quote":"Name: Jane Synthetic","match":"exact"}}
        """;

    private static DocumentExtractionResult Lab(string json = LabJson) =>
        DocumentExtractionResult.Ok(ClinicalDocumentType.LabPdf, json, NoUsage);

    private static DocumentExtractionResult Intake(string json = IntakeJson) =>
        DocumentExtractionResult.Ok(ClinicalDocumentType.IntakeForm, json, NoUsage);

    [Fact]
    public void Map_WhenExtractionRejected_ReturnsEmpty()
    {
        var rejected = DocumentExtractionResult.Rejected(ClinicalDocumentType.LabPdf, "schema violation");

        new DerivedFactMapper().Map(rejected, "dr-1").Should().BeEmpty();
    }

    [Fact]
    public void Map_LabExtraction_ProducesOneFactPerTest_WithCitation()
    {
        var facts = new DerivedFactMapper().Map(Lab(), "dr-1");

        facts.Should().ContainSingle();
        var fact = facts[0];
        fact.FactType.Should().Be("lab.result");
        fact.PayloadJson.Should().Contain("Potassium").And.Contain("5.8");
        fact.Citation.SourceType.Should().Be(CitationSourceType.Derived);
        fact.Citation.SourceId.Should().Be("dr-1");
        fact.Citation.PageOrSection.Should().Be("1");
        fact.Citation.FieldOrChunkId.Should().Be("Potassium");
        fact.Citation.QuoteOrValue.Should().Be("K+ 5.8 (H)");
        fact.Citation.BoundingBox.Should().Equal(0.1, 0.2, 0.3, 0.4);
    }

    [Fact]
    public void Map_WhenTheExtractorLocatedTheQuote_SetsFullExtractionConfidence()
    {
        // match=exact is the extractor's own finding that the quote is in the source document's text, which
        // is what full grounding confidence means (FR-OBS-W2-1 per-encounter telemetry).
        var facts = new DerivedFactMapper().Map(Lab(), "dr-1");

        facts[0].ExtractionConfidence.Should().Be(1.0);
    }

    [Fact]
    public void Map_WhenTheQuoteCouldNotBeChecked_SetsReducedExtractionConfidence()
    {
        // A scan or an image: no page text to check the quote against, so page-level confidence.
        const string unchecked_ =
            """{"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","match":"unchecked"}}]}""";

        var facts = new DerivedFactMapper().Map(Lab(unchecked_), "dr-1");

        facts[0].ExtractionConfidence.Should().Be(0.5);
    }

    [Fact]
    public void Map_WhenTheQuoteWasNotFoundInTheSource_FloorsTheExtractionConfidence()
    {
        // the fabrication gap, measured on the fact rather than argued about. Confidence
        // was read off `BoundingBox is not null`, and an unlocatable quote KEPT the model's estimated box -
        // so a quote that is nowhere in the document scored 1.0, identical to a verbatim one. Locatability
        // is the extractor's finding now, and "I searched the page and this text is not on it" is the
        // floor, not the ceiling.
        const string fabricated =
            """{"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","bounding_box":[0.1,0.2,0.3,0.4],"match":"unlocatable"}}]}""";

        var facts = new DerivedFactMapper().Map(Lab(fabricated), "dr-1");

        facts[0].ExtractionConfidence.Should().Be(0.0);
    }

    [Fact]
    public void Map_WhenTheCitationCarriesNoMatchAtAll_DoesNotInferConfidenceFromTheBox()
    {
        // The absent field means "nothing has looked", which is exactly `unchecked`. It must never be read
        // as an endorsement, and a bounding box on its own is not one: the model supplies an estimated box
        // on every citation it can see, fabricated or not.
        const string noMatchField =
            """{"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","bounding_box":[0.1,0.2,0.3,0.4]}}]}""";

        var facts = new DerivedFactMapper().Map(Lab(noMatchField), "dr-1");

        facts[0].ExtractionConfidence.Should().Be(0.5);
    }

    [Fact]
    public void Map_WhenTheMatchIsAValueThisBuildDoesNotKnow_TreatsItAsUnchecked()
    {
        // a separate change review - forward compatibility, the direction that matters once adds a
        // fuzzy member: canonical JSON written by a newer build must not throw here, and an unknown grading
        // must not be read as an endorsement. "Nothing looked" is the safe reading of a word we do not know.
        const string futureMatch =
            """{"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","bounding_box":[0.1,0.2,0.3,0.4],"match":"fuzzy"}}]}""";

        var facts = new DerivedFactMapper().Map(Lab(futureMatch), "dr-1");

        facts.Should().ContainSingle();
        facts[0].ExtractionConfidence.Should().Be(0.5);
    }

    [Fact]
    public void Map_WhenCitationPending_SetsEmptySourceId()
    {
        var facts = new DerivedFactMapper().Map(Lab(), documentReferenceId: null);

        facts.Should().ContainSingle();
        facts[0].Citation.SourceId.Should().BeEmpty();
    }

    [Fact]
    public void Map_IntakeExtraction_ProducesFactsForEachSection()
    {
        var facts = new DerivedFactMapper().Map(Intake(), "dr-9");

        facts.Select(f => f.FactType).Should().BeEquivalentTo(
            "intake.demographics", "intake.chief_concern", "intake.medication", "intake.allergy", "intake.family_history");
        facts.Should().OnlyContain(f => f.Citation.SourceId == "dr-9");
    }

    [Fact]
    public void Map_IntakeMedication_UsesItsOwnCitationPageAndName()
    {
        var facts = new DerivedFactMapper().Map(Intake(), "dr-9");

        var medication = facts.Single(f => f.FactType == "intake.medication");
        medication.Citation.FieldOrChunkId.Should().Be("Metoprolol");
        medication.Citation.PageOrSection.Should().Be("2");
        medication.PayloadJson.Should().Contain("Metoprolol");
    }

    [Fact]
    public void Map_IntakeChiefConcern_WhenAbsent_IsOmitted()
    {
        const string noConcern =
            """
            {"demographics":{"full_name":"Jane Synthetic"},"current_medications":[],"allergies":[],"family_history":[],"citation":{"page":1,"quote":"header"}}
            """;

        var facts = new DerivedFactMapper().Map(Intake(noConcern), "dr-9");

        facts.Should().ContainSingle().Which.FactType.Should().Be("intake.demographics");
    }

    [Fact]
    public void Map_IntakeAllergy_CitesItsOwnQuotePageAndBoxRatherThanTheFormLevelOne()
    {
        // an allergy used to ride on the form-level citation, which quotes the name line,
        // so click-to-source for it landed on the patient's name, and on page 1 whatever page it was printed on.
        var facts = new DerivedFactMapper().Map(Intake(), "dr-9");

        var allergy = facts.Single(f => f.FactType == "intake.allergy");
        allergy.Citation.FieldOrChunkId.Should().Be("allergy");
        allergy.Citation.QuoteOrValue.Should().Be("Penicillin - hives");
        allergy.Citation.PageOrSection.Should().Be("2");
        allergy.Citation.BoundingBox.Should().Equal(0.1, 0.5, 0.2, 0.02);
        allergy.PayloadJson.Should().Contain("penicillin");
    }

    [Fact]
    public void Map_IntakeChiefConcernAndFamilyHistory_EachCiteTheirOwnQuoteAndPage()
    {
        var facts = new DerivedFactMapper().Map(Intake(), "dr-9");

        var concern = facts.Single(f => f.FactType == "intake.chief_concern");
        concern.Citation.QuoteOrValue.Should().Be("Reason for visit: palpitations");
        concern.Citation.PageOrSection.Should().Be("1");
        concern.PayloadJson.Should().Contain("palpitations");
        var history = facts.Single(f => f.FactType == "intake.family_history");
        history.Citation.QuoteOrValue.Should().Be("Father: MI at 60");
        history.Citation.PageOrSection.Should().Be("2");
        history.Citation.BoundingBox.Should().BeNull("its own citation carried no box, and the name line's is not its box");
    }

    [Theory]
    [InlineData("unlocatable", 0.0)]
    [InlineData("unchecked", 0.5)]
    public void Map_IntakeTextItem_TakesItsConfidenceFromItsOwnCitationNotTheFormLevelOne(string match, double expected)
    {
        // a separate change review N1 - these three fact types inherited the form-level citation's
        // `exact`, so an allergy the model invented was stored at 1.0 ("quote found") when only the name line
        // had been checked. Here the name line is found and each item's own quote is not.
        var json = $$$"""
            {"demographics":{"full_name":"Jane Synthetic"},"chief_concern":{"text":"palpitations","citation":{"page":1,"quote":"palpitations","match":"{{{match}}}"}},"current_medications":[],"allergies":[{"text":"sulfa","citation":{"page":1,"quote":"Sulfa - rash","match":"{{{match}}}"}}],"family_history":[{"text":"mother CHF","citation":{"page":1,"quote":"Mother: CHF","match":"{{{match}}}"}}],"citation":{"page":1,"quote":"Name: Jane Synthetic","match":"exact"}}
            """;

        var facts = new DerivedFactMapper().Map(Intake(json), "dr-9");

        facts.Single(f => f.FactType == "intake.demographics").ExtractionConfidence.Should().Be(1.0);
        facts.Where(f => f.FactType is "intake.chief_concern" or "intake.allergy" or "intake.family_history")
            .Should().HaveCount(3).And.OnlyContain(f => f.ExtractionConfidence == expected);
    }
}
