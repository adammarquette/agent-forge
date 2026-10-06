using FakeItEasy;
using FluentAssertions;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;

namespace AgentForge.UnitTests.Documents;

/// <summary>
/// Unit tests for <see cref="DocumentExtractor"/>. The guarded failure mode is the trust core: raw model
/// output that does not satisfy the strict schema must be rejected, not persisted (ARCHITECTURE-DOCUMENTS.md §3).
/// </summary>
public sealed class DocumentExtractorTests
{
    private readonly ILlmProvider _llm = A.Fake<ILlmProvider>();
    private readonly IPdfWordReader _pdfReader = A.Fake<IPdfWordReader>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly CapturingLogger<DocumentExtractor> _logger = new();

    private DocumentExtractor CreateSut()
    {
        // Default: no PDF geometry, so citation boxes are left as the model gave them. Tests that exercise
        // box resolution override this after construction (last FakeItEasy config wins).
        A.CallTo(() => _pdfReader.ReadTextLayer(A<ReadOnlyMemory<byte>>._)).Returns(PdfTextLayer.None);
        return new(_llm, _pdfReader, _metrics, _logger);
    }

    private void SetupModelReturns(string content, LlmUsage? usage = null) =>
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmResponse(content, [], LlmStopReason.EndTurn, usage ?? new LlmUsage(0, 0, 0m)));

    [Fact]
    public async Task ExtractAsync_WhenModelReturnsValidLabJson_Succeeds()
    {
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":"2.0-3.0",
            "collection_date":"2026-07-01","abnormal_flag":false,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null}}]}
            """);

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.CanonicalJson.Should().NotBeNull().And.Contain("INR");
    }

    [Fact]
    public async Task ExtractAsync_WhenLabResultOmitsRequiredCitation_RejectsAndPersistsNothing()
    {
        // The required 'citation' is missing - unschematized VLM output must not pass the gate.
        SetupModelReturns("""{"tests":[{"test_name":"INR","value":"2.5"}]}""");

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.CanonicalJson.Should().BeNull();
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WhenPdfWordsLocateTheQuote_ReplacesTheEstimateWithTheExactBox()
    {
        var sut = CreateSut();
        SetupPdfText(1, new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null}}]}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        // The model's null box is replaced by the union of the located words (top-left corner at 0.10, 0.20).
        result.CanonicalJson.Should().NotContain("\"bounding_box\":null").And.Contain("\"bounding_box\":[0.1,0.2,");
    }

    [Fact]
    public async Task ExtractAsync_WhenModelReturnsNoJson_Rejects()
    {
        SetupModelReturns("I'm sorry, I could not read the document.");

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WhenExtractionSucceeds_RecordsTheCallsTokensAndCost()
    {
        // the intake-extractor's multimodal call is the third LLM path and spends real
        // money, so a completed document-upload turn must reach the same counter the chat and evidence turns
        // feed; otherwise the per-query cost decomposition in REQUIREMENTS.md §15.1 is missing a term.
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null}}]}
            """,
            new LlmUsage(4211, 318, 0.0174m));

        await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        A.CallTo(() => _metrics.RecordLlmUsage(4211, 318, 0.0174m)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExtractAsync_WhenOutputFailsTheSchemaGate_StillRecordsTheCallsTokensAndCost()
    {
        // The spend happens at the provider, not at the gate: a rejected extraction costs exactly as much as an
        // accepted one. Metering only the happy path would under-report the true cost per document-upload turn.
        SetupModelReturns("""{"tests":[{"test_name":"INR","value":"2.5"}]}""", new LlmUsage(3900, 42, 0.0123m));

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        A.CallTo(() => _metrics.RecordLlmUsage(3900, 42, 0.0123m)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExtractAsync_WhenModelReturnsNoJson_StillRecordsTheCallsTokensAndCost()
    {
        SetupModelReturns("I'm sorry, I could not read the document.", new LlmUsage(1200, 18, 0.0038m));

        await CreateSut().ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        A.CallTo(() => _metrics.RecordLlmUsage(1200, 18, 0.0038m)).MustHaveHappenedOnceExactly();
    }

    // --- The VERBATIM grounding rule's deterministic backstop ---------------------------------
    // The extraction prompt's strongest rule is that every "quote" is copied verbatim from the document.
    // Nothing enforced it: a quote the resolver could not find left the model's own citation - quote, page
    // and the model's *estimated* box - in place, so a fabricated quote was un-located, not rejected, and
    // reached the clinician looking like any other cited fact. These pin the backstop.

    private const string FabricatedQuoteWithModelBox =
        """
        {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
        "collection_date":null,"abnormal_flag":null,
        "citation":{"page":1,"quote":"INR 2.5 critical","bounding_box":[0.7,0.8,0.1,0.02]}}]}
        """;

    // The document the fake reader returns: its words AND how many pages it has. The page count is not
    // decoration - a citation naming a page outside it is a fabricated citation, not an unreadable one.
    private void SetupPdfText(int pageCount, params PdfWord[] words) =>
        A.CallTo(() => _pdfReader.ReadTextLayer(A<ReadOnlyMemory<byte>>._)).Returns(new PdfTextLayer(words, pageCount));

    private void SetupPdfWords(params PdfWord[] words) => SetupPdfText(1, words);

    [Fact]
    public async Task ExtractAsync_WhenTheQuoteIsNotInThePdfText_DiscardsTheModelsBoxAndMarksItUnlocatable()
    {
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(FabricatedQuoteWithModelBox);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        // The box is the claim "this quote is *here*". The quote is not in the page's text, so the claim is
        // rejected rather than kept: a highlight around text that does not say what the citation says is
        // strictly worse than no highlight.
        result.CanonicalJson.Should().NotContain("0.7").And.Contain("\"match\":\"unlocatable\"");
    }

    [Fact]
    public async Task ExtractAsync_WhenTheModelAssertsItsOwnMatchQuality_TheAssertionIsOverwritten()
    {
        // Provenance is stamped below the model, never accepted from it - the same reasoning that keeps
        // authorization out of the prompt. A model that learns the field exists must not be
        // able to launder a fabricated quote by claiming it matched.
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5 critical","bounding_box":null,"match":"exact"}}]}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.CanonicalJson.Should().Contain("\"match\":\"unlocatable\"").And.NotContain("\"match\":\"exact\"");
    }

    [Fact]
    public async Task ExtractAsync_WhenTheModelSendsAnUnrecognisedMatch_StampsItAnywayRatherThanRejecting()
    {
        // `match` is absent from the prompt shape, so anything the model puts
        // there is a guess - and the extractor overwrites it regardless. Rejecting the whole document over a
        // value that was about to be discarded would lose every good fact on it to one invented token. The
        // same read also lets this build tolerate canonical JSON from a build that knows a member it does
        // not, which is what adding a fuzzy member will create.
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null,"match":"fuzzy"}}]}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.RejectionReason.Should().BeNull();
        result.CanonicalJson.Should().Contain("\"match\":\"exact\"").And.NotContain("fuzzy");
    }

    [Fact]
    public async Task ExtractAsync_WhenTheQuoteIsLocatedInThePdfText_MarksItExact()
    {
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null}}]}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.CanonicalJson.Should().Contain("\"match\":\"exact\"").And.Contain("\"bounding_box\":[0.1,0.2,");
    }

    [Fact]
    public async Task ExtractAsync_WhenTheCitedPageIsNotInTheDocument_MarksItUnlocatableAndDiscardsTheBox()
    {
        // a separate change review, blocking finding 1 - reproduced as the reviewer described it. A
        // two-page digital PDF, a citation claiming page 3. Scoring it Unchecked kept the model's box, left
        // confidence at 0.5, logged nothing, and counted it in the same bucket as every scan.
        var sut = CreateSut();
        SetupPdfText(2, new(2, "INR", 0.10, 0.20, 0.06, 0.03), new(2, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":3,"quote":"INR 2.5","bounding_box":[0.7,0.8,0.1,0.02]}}]}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.CanonicalJson.Should().Contain("\"match\":\"unlocatable\"").And.NotContain("0.7");
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unchecked")).MustNotHaveHappened();
        _logger.Lines.Should().ContainSingle(line => line.Contains("1 of 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_WhenThereIsNoPageText_MarksItUncheckedAndKeepsTheModelsEstimate()
    {
        // A scan or an image carries no glyphs to check against. Nothing was searched, so the citation is
        // neither vindicated nor impeached and the model's estimate is the best available overlay. The quote
        // carries the result it cites, so nothing but the missing text layer decides the outcome.
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5 critical","bounding_box":[0.7,0.8,0.1,0.02]}}]}
            """);

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "image/png", CancellationToken.None);

        result.CanonicalJson.Should().Contain("\"match\":\"unchecked\"").And.Contain("0.7");
    }

    [Fact]
    public async Task ExtractAsync_WhenAQuoteIsUnlocatable_CountsItAndLogsWithoutTheQuote()
    {
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(FabricatedQuoteWithModelBox);

        await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappenedOnceExactly();
        _logger.Lines.Should().ContainSingle(line => line.Contains("1 of 1", StringComparison.Ordinal));
        // The quote is free text off a clinical document; counting it is the point, echoing it into logs is
        // a PHI violation (CONVENTIONS.md section 7).
        _logger.Lines.Should().NotContain(line => line.Contains("INR 2.5 critical", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAsync_WhenEveryQuoteIsLocated_LogsNothingAndCountsOnlyExactMatches()
    {
        var sut = CreateSut();
        SetupPdfWords(new(1, "INR", 0.10, 0.20, 0.06, 0.03), new(1, "2.5", 0.20, 0.20, 0.05, 0.03));
        SetupModelReturns(
            """
            {"tests":[{"test_name":"INR","value":"2.5","unit":null,"reference_range":null,
            "collection_date":null,"abnormal_flag":null,
            "citation":{"page":1,"quote":"INR 2.5","bounding_box":null}}]}
            """);

        await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        A.CallTo(() => _metrics.RecordCitationQuoteMatch("exact")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustNotHaveHappened();
        _logger.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_IntakeForm_StampsEveryCitationIncludingTheFormLevelOne()
    {
        // Per-medication and the form-level (demographics) citation are both grounding gates, so neither may
        // ship unstamped. The free-text items' own citations are the next test.
        var sut = CreateSut();
        SetupPdfWords(new(1, "Intake", 0.10, 0.10, 0.10, 0.03), new(1, "form", 0.22, 0.10, 0.08, 0.03));
        SetupModelReturns(
            """
            {"demographics":{"full_name":"Jane Synthetic","date_of_birth":null,"sex":null},
            "chief_concern":null,
            "current_medications":[{"name":"Metoprolol","dose":"25mg",
              "citation":{"page":1,"quote":"Metoprolol 25mg","bounding_box":[0.31,0.4,0.2,0.02]}}],
            "allergies":[],"family_history":[],
            "citation":{"page":1,"quote":"Intake form","bounding_box":null}}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.CanonicalJson.Should().Contain("\"match\":\"exact\"")           // the form-level quote is present
            .And.Contain("\"match\":\"unlocatable\"")                          // the medication quote is not
            .And.NotContain("0.31");                                           // and its estimated box is gone
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExtractAsync_IntakeForm_StampsEachFreeTextItemOnItsOwnQuoteAndPage()
    {
        // the chief concern, each allergy and each family-history item are searched for on
        // their OWN page, so an invented allergy comes out unlocatable even though the name line is found.
        var sut = CreateSut();
        SetupPdfText(
            2,
            new(1, "Name:", 0.10, 0.10, 0.08, 0.03), new(1, "Jane", 0.20, 0.10, 0.06, 0.03),
            new(1, "Palpitations", 0.10, 0.20, 0.15, 0.03),
            new(2, "Penicillin", 0.10, 0.50, 0.12, 0.03));
        SetupModelReturns(
            """
            {"demographics":{"full_name":"Jane Synthetic","date_of_birth":null,"sex":null},
            "chief_concern":{"text":"Palpitations","citation":{"page":1,"quote":"Palpitations","bounding_box":null}},
            "current_medications":[],
            "allergies":[
              {"text":"Penicillin","citation":{"page":2,"quote":"Penicillin","bounding_box":null}},
              {"text":"Latex","citation":{"page":2,"quote":"Latex","bounding_box":[0.4,0.6,0.1,0.02],"match":"exact"}}],
            "family_history":[{"text":"Penicillin","citation":{"page":1,"quote":"Penicillin","bounding_box":null}}],
            "citation":{"page":1,"quote":"Name: Jane","bounding_box":null}}
            """);

        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.ChiefConcern!.Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(intake.ChiefConcern.Citation.BoundingBox).Should().Equal(0.10, 0.20, 0.15, 0.03);
        intake.Allergies[0].Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(intake.Allergies[0].Citation.BoundingBox).Should().Equal(0.10, 0.50, 0.12, 0.03);
        intake.Allergies[1].Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable, "no 'Latex' is printed, whatever the model claims");
        intake.Allergies[1].Citation.BoundingBox.Should().BeNull();
        intake.FamilyHistory[0].Citation.Match.Should().Be(
            CitationQuoteMatch.Unlocatable, "'Penicillin' is printed on page 2, not on the page this item cites");
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("exact")).MustHaveHappened(3, Times.Exactly);
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappened(2, Times.Exactly);
    }

    private static IEnumerable<double> Rounded(double[]? box) => (box ?? []).Select(v => Math.Round(v, 9));

    // A two-page form whose page 2 prints one allergy and one medication line - the real quotes an invented
    // fact borrows below.
    private void SetupAllergyAndMedicationPage() =>
        SetupPdfText(
            2,
            new(1, "Name:", 0.10, 0.10, 0.08, 0.03), new(1, "Jane", 0.20, 0.10, 0.06, 0.03),
            new(2, "Lisinopril", 0.10, 0.50, 0.12, 0.03), new(2, "(angioedema)", 0.23, 0.50, 0.15, 0.03),
            new(2, "Warfarin", 0.10, 0.60, 0.10, 0.03), new(2, "5", 0.21, 0.60, 0.02, 0.03),
            new(2, "mg", 0.24, 0.60, 0.03, 0.03), new(2, "daily", 0.28, 0.60, 0.06, 0.03));

    private static string IntakeWith(string allergies, string medications) =>
        $$$"""
        {"demographics":{"full_name":"Jane Synthetic","date_of_birth":null,"sex":null},
        "chief_concern":null,
        "current_medications":[{{{medications}}}],
        "allergies":[{{{allergies}}}],
        "family_history":[],
        "citation":{"page":1,"quote":"Name: Jane","bounding_box":null}}
        """;

    [Fact]
    public async Task ExtractAsync_IntakeItemWhoseTextIsNotInItsRealQuote_IsUnlocatableWithNoBox()
    {
        // Given an invented allergy paired with a quote that IS printed
        // on its cited page, and a faithful allergy on the same quote
        var sut = CreateSut();
        SetupAllergyAndMedicationPage();
        SetupModelReturns(IntakeWith(
            allergies: """
                {"text":"Latex (anaphylaxis)","citation":{"page":2,"quote":"Lisinopril (angioedema)","bounding_box":null,"match":"exact"}},
                {"text":"Lisinopril (angioedema)","citation":{"page":2,"quote":"Lisinopril (angioedema)","bounding_box":null}}
                """,
            medications: string.Empty));

        // When the form is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then the invented one is "not found" - no box, whatever the quote's own location - and the
        // faithful one keeps its exact match and its glyph box
        result.Succeeded.Should().BeTrue();
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.Allergies[0].Citation.Match.Should().Be(
            CitationQuoteMatch.Unlocatable, "the quote is printed, but it does not say 'Latex (anaphylaxis)'");
        intake.Allergies[0].Citation.BoundingBox.Should().BeNull("a box would point the clinician at a line that says something else");
        intake.Allergies[1].Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(intake.Allergies[1].Citation.BoundingBox).Should().Equal(0.10, 0.50, 0.28, 0.03);
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("""{"text":null,"citation":{"page":2,"quote":"Lisinopril (angioedema)","bounding_box":null}}""", "")]
    [InlineData("""{"text":"Lisinopril (angioedema)","citation":{"page":2,"quote":null,"bounding_box":null}}""", "")]
    [InlineData("", """{"name":null,"dose":"5 mg daily","citation":{"page":2,"quote":"Warfarin 5 mg daily","bounding_box":null}}""")]
    [InlineData("", """{"name":"Warfarin","dose":null,"citation":{"page":2,"quote":null,"bounding_box":null}}""")]
    public async Task ExtractAsync_WhenARequiredTextOrQuoteIsJsonNull_RejectsAtTheSchemaGate(string allergies, string medications)
    {
        // a separate change review N2 - Given a reply whose required text, name or quote is an explicit null.
        // A required member that is present-but-null satisfies `required`, so without nullable enforcement
        // it reached the matchers and threw a NullReferenceException the JsonException gate never caught.
        var sut = CreateSut();
        SetupAllergyAndMedicationPage();
        SetupModelReturns(IntakeWith(allergies, medications));

        // When the form is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then it is refused like any other schema failure, and nothing is left to persist
        result.Succeeded.Should().BeFalse();
        result.CanonicalJson.Should().BeNull();
        result.RejectionReason.Should().StartWith("Extracted JSON failed schema validation");
    }

    [Fact]
    public async Task ExtractAsync_ChiefConcernAndFamilyHistoryWhoseTextIsNotInTheirRealQuote_AreUnlocatable()
    {
        // Given a chief concern and a family-history item, each citing a printed line it does not say
        var sut = CreateSut();
        SetupAllergyAndMedicationPage();
        SetupModelReturns(
            """
            {"demographics":{"full_name":"Jane Synthetic","date_of_birth":null,"sex":null},
            "chief_concern":{"text":"Chest pain","citation":{"page":2,"quote":"Lisinopril (angioedema)","bounding_box":null}},
            "current_medications":[],"allergies":[],
            "family_history":[{"text":"Father - MI at 50","citation":{"page":2,"quote":"Warfarin 5 mg daily","bounding_box":null}}],
            "citation":{"page":1,"quote":"Name: Jane","bounding_box":null}}
            """);

        // When the form is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then both are "not found", and the demographics line, which is not a text item, is untouched
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.ChiefConcern!.Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        intake.ChiefConcern.Citation.BoundingBox.Should().BeNull();
        intake.FamilyHistory[0].Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        intake.FamilyHistory[0].Citation.BoundingBox.Should().BeNull();
        intake.Citation.Match.Should().Be(CitationQuoteMatch.Exact);
    }

    [Theory]
    [InlineData("Apixaban", "\"5 mg daily\"")]     // an invented name on a real line
    [InlineData("Warfarin", "\"50 mg daily\"")]    // the real name with an invented dose
    public async Task ExtractAsync_MedicationWhoseNameOrDoseIsNotInItsRealQuote_IsUnlocatableWithNoBox(string name, string dose)
    {
        // Given a medication citing the printed "Warfarin 5 mg daily" line, but naming or
        // dosing something that line does not say
        var sut = CreateSut();
        SetupAllergyAndMedicationPage();
        SetupModelReturns(IntakeWith(
            allergies: string.Empty,
            medications: $$$"""
                {"name":"{{{name}}}","dose":{{{dose}}},"citation":{"page":2,"quote":"Warfarin 5 mg daily","bounding_box":[0.1,0.6,0.3,0.03]}}
                """));

        // When the form is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then the medication is "not found" and carries no box
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.CurrentMedications[0].Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        intake.CurrentMedications[0].Citation.BoundingBox.Should().BeNull();
    }

    [Theory]
    [InlineData("\"5 mg daily\"")]
    [InlineData("null")]
    public async Task ExtractAsync_MedicationWhoseNameAndDoseAreInItsQuote_StaysExact(string dose)
    {
        // Given a faithful medication, with or without a dose - an absent dose claims nothing to check
        var sut = CreateSut();
        SetupAllergyAndMedicationPage();
        SetupModelReturns(IntakeWith(
            allergies: string.Empty,
            medications: $$$"""
                {"name":"Warfarin","dose":{{{dose}}},"citation":{"page":2,"quote":"Warfarin 5 mg daily","bounding_box":null}}
                """));

        // When the form is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then it keeps its exact match and the glyph box
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.CurrentMedications[0].Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(intake.CurrentMedications[0].Citation.BoundingBox).Should().Equal(0.10, 0.60, 0.24, 0.03);
    }

    [Fact]
    public async Task ExtractAsync_WhenThereIsNoPageTextAndAnItemsTextIsNotInItsQuote_IsStillUnlocatable()
    {
        // Given a scan - nothing to search the quote against - and an allergy whose text its own quote
        // does not carry. The mismatch is between two strings the model sent, so no text layer is needed
        // to see it, and "unchecked" (0.5, the model's box kept) would pass off an invented fact as unread.
        SetupModelReturns(IntakeWith(
            allergies: """
                {"text":"Latex (anaphylaxis)","citation":{"page":1,"quote":"Lisinopril (angioedema)","bounding_box":[0.7,0.8,0.1,0.02]}},
                {"text":"Lisinopril (angioedema)","citation":{"page":1,"quote":"Lisinopril (angioedema)","bounding_box":[0.1,0.5,0.3,0.03]}}
                """,
            medications: string.Empty));

        // When the form is extracted
        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1 }, "image/png", CancellationToken.None);

        // Then the mismatched item is unlocatable with no box, and the faithful one stays unchecked
        var intake = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.IntakeExtraction)!;
        intake.Allergies[0].Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        intake.Allergies[0].Citation.BoundingBox.Should().BeNull();
        intake.Allergies[1].Citation.Match.Should().Be(CitationQuoteMatch.Unchecked);
        intake.Allergies[1].Citation.BoundingBox.Should().Equal(0.1, 0.5, 0.3, 0.03);
    }

    [Theory]
    [InlineData("""{"chief_concern":{"text":"palpitations"},"allergies":[],"family_history":[]}""")]
    [InlineData("""{"chief_concern":null,"allergies":[{"text":"penicillin"}],"family_history":[]}""")]
    [InlineData("""{"chief_concern":null,"allergies":[],"family_history":[{"text":"father MI at 60"}]}""")]
    [InlineData("""{"chief_concern":"palpitations","allergies":["penicillin"],"family_history":["father MI at 60"]}""")]
    public async Task ExtractAsync_WhenAnIntakeFreeTextItemCarriesNoCitationOfItsOwn_RejectsAndPersistsNothing(string items)
    {
        // the last case is the shape before it: bare strings that could only borrow the
        // form-level citation. Every intake fact now carries its own, and one without it fails the gate.
        var json = items.TrimEnd('}') + """
            ,"demographics":{"full_name":"Jane Synthetic"},"current_medications":[],
            "citation":{"page":1,"quote":"Name: Jane Synthetic","bounding_box":null}}
            """;
        SetupModelReturns(json);

        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.IntakeForm, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.CanonicalJson.Should().BeNull();
        result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    // --- Lab results checked against their own quote ---------------------------------

    // A lab page printing two rows, "Sodium 136 mmol/L 135-145" and "LDL 168 mg/dL" - the real quotes the
    // results below borrow.
    private void SetupLabRowsPage() =>
        SetupPdfWords(
            new(1, "Sodium", 0.10, 0.20, 0.08, 0.03), new(1, "136", 0.20, 0.20, 0.04, 0.03),
            new(1, "mmol/L", 0.25, 0.20, 0.07, 0.03), new(1, "135-145", 0.34, 0.20, 0.08, 0.03),
            new(1, "LDL", 0.10, 0.30, 0.05, 0.03), new(1, "168", 0.20, 0.30, 0.04, 0.03),
            new(1, "mg/dL", 0.25, 0.30, 0.06, 0.03));

    private static string LabRow(string testName, string value, string unit, string quote, string box = "null") =>
        $$$"""
        {"test_name":"{{{testName}}}","value":"{{{value}}}","unit":"{{{unit}}}","reference_range":null,
        "collection_date":null,"abnormal_flag":null,
        "citation":{"page":1,"quote":"{{{quote}}}","bounding_box":{{{box}}},"match":"exact"}}
        """;

    [Theory]
    [InlineData("Potassium", "136", "mmol/L")]    // the value, filed under another analyte
    [InlineData("Sodium", "135", "mmol/L")]       // a value read off the reference range
    [InlineData("Sodium", "163", "mmol/L")]       // an invented value
    [InlineData("Sodium", "136", "mg/dL")]        // an invented unit
    public async Task ExtractAsync_LabResultWhoseAnalyteValueOrUnitIsNotInItsRealQuote_IsUnlocatableWithNoBox(
        string testName, string value, string unit)
    {
        // Given a result citing the printed "Sodium 136 mmol/L 135-145" row, claiming a row it does not print,
        // beside the faithful result on the same quote
        var sut = CreateSut();
        SetupLabRowsPage();
        SetupModelReturns($$"""
            {"tests":[
            {{LabRow(testName, value, unit, "Sodium 136 mmol/L 135-145", "[0.1,0.2,0.3,0.03]")}},
            {{LabRow("Sodium", "136", "mmol/L", "Sodium 136 mmol/L 135-145")}}]}
            """);

        // When the report is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then the claimed row is "not found", with no box - whatever the model said about its own match - and
        // the faithful one keeps its exact match and its glyph box
        result.Succeeded.Should().BeTrue();
        var lab = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.LabExtraction)!;
        lab.Tests[0].Citation.Match.Should().Be(
            CitationQuoteMatch.Unlocatable, "the quote is printed, but it does not print this result");
        lab.Tests[0].Citation.BoundingBox.Should().BeNull("a box would point the clinician at a row that says something else");
        lab.Tests[1].Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(lab.Tests[1].Citation.BoundingBox).Should().Equal(0.10, 0.20, 0.32, 0.03);
        A.CallTo(() => _metrics.RecordCitationQuoteMatch("unlocatable")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExtractAsync_LabResultNamedInFullOnAnAbbreviatedRow_StaysExact()
    {
        // Given "LDL Cholesterol" citing the printed "LDL 168 mg/dL" row - an abbreviation in the reviewed table
        var sut = CreateSut();
        SetupLabRowsPage();
        SetupModelReturns($$"""{"tests":[{{LabRow("LDL Cholesterol", "168", "mg/dL", "LDL 168 mg/dL")}}]}""");

        // When the report is extracted
        var result = await sut.ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1, 2, 3 }, "application/pdf", CancellationToken.None);

        // Then it is found, with the row's glyph box
        var lab = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.LabExtraction)!;
        lab.Tests[0].Citation.Match.Should().Be(CitationQuoteMatch.Exact);
        Rounded(lab.Tests[0].Citation.BoundingBox).Should().Equal(0.10, 0.30, 0.21, 0.03);
    }

    [Fact]
    public async Task ExtractAsync_LabReport_AsksForEachQuoteToPrintItsRowFromTheNameThroughTheUnit()
    {
        // Given any lab report. The lab rule checks the name, the value and the unit against the quote, so a
        // prompt that let the model quote only the value would turn every faithful result into "not found".
        SetupModelReturns("""{"tests":[]}""");

        // When it is extracted
        await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "application/pdf", CancellationToken.None);

        // Then the model is asked to quote the whole row
        A.CallTo(() => _llm.CompleteAsync(
                A<LlmRequest>.That.Matches(r =>
                    r.SystemPrompt.Contains("the \"quote\" copies that row as printed, from the test name through", StringComparison.Ordinal)
                    && r.SystemPrompt.Contains("the value and, when the row prints one, the unit.", StringComparison.Ordinal)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExtractAsync_WhenThereIsNoPageTextAndALabResultIsNotInItsQuote_IsStillUnlocatable()
    {
        // Given a scanned report - nothing to search the quote against - and a result its own quote does not
        // print. The mismatch is between two strings the model sent, so "unchecked" would pass it off as unread.
        SetupModelReturns($$"""
            {"tests":[
            {{LabRow("Potassium", "136", "mmol/L", "Sodium 136 mmol/L 135-145", "[0.7,0.8,0.1,0.02]")}},
            {{LabRow("Sodium", "136", "mmol/L", "Sodium 136 mmol/L 135-145", "[0.1,0.2,0.3,0.03]")}}]}
            """);

        // When the report is extracted
        var result = await CreateSut().ExtractAsync(
            ClinicalDocumentType.LabPdf, new byte[] { 1 }, "image/png", CancellationToken.None);

        // Then the mismatched result is unlocatable with no box, and the faithful one stays unchecked
        var lab = System.Text.Json.JsonSerializer.Deserialize(
            result.CanonicalJson!, DocumentExtractionJsonContext.Default.LabExtraction)!;
        lab.Tests[0].Citation.Match.Should().Be(CitationQuoteMatch.Unlocatable);
        lab.Tests[0].Citation.BoundingBox.Should().BeNull();
        lab.Tests[1].Citation.Match.Should().Be(CitationQuoteMatch.Unchecked);
        lab.Tests[1].Citation.BoundingBox.Should().Equal(0.1, 0.2, 0.3, 0.03);
    }
}
