using System.Text.RegularExpressions;
using AgentForge.Agents;
using AgentForge.Agents.Ingestion;
using AgentForge.Data.Entities;
using AgentForge.GenerateFixtureDocuments;
using FluentAssertions;
using UglyToad.PdfPig;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// The rulings on one run, a stage at a time. Every <c>because</c> starts with the stage it guards in
/// brackets, so a red control can show not only that the run failed but which stage it failed at.
/// </summary>
internal static class PipelineChecks
{
    public const string Hermetic = "[hermetic]";
    public const string Ingest = "[ingest]";
    public const string Extract = "[extract]";
    public const string Persist = "[persist]";
    public const string Retrieve = "[retrieve]";
    public const string Cite = "[cite]";
    public const string Critic = "[critic]";
    public const string Phi = "[phi]";

    private const int IntakeFactCount = 6; // demographics, chief concern, two medications, allergy, family history

    // Every printed row, plus the one the VLM invents (CannedReplies.InventedLabRow).
    private static int LabFactCount => SyntheticLabPanel.Rows.Count + 1;

    private static readonly Regex CitationToken = new(
        @"\[([A-Za-z]+)/([A-Za-z0-9\-\.]+)\]", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Every stage, in pipeline order; throws at the first that does not hold.</summary>
    public static void AssertGroundedAndCited(PipelineOutcome o)
    {
        AssertHermetic(o);
        AssertIngested(o);
        AssertExtracted(o);
        AssertPersisted(o);
        AssertRetrieved(o);
        AssertCited(o);
        AssertCriticked(o);
        AssertNoPhi(o);
    }

    private static void AssertHermetic(PipelineOutcome o)
    {
        o.NetworkActivity.Should().BeEmpty(
            $"{Hermetic} the run must make no HTTP request, socket connect or DNS lookup (every one starts a runtime activity)");
        o.LlmProviderType.Assembly.Should().BeSameAs(typeof(PipelineChecks).Assembly,
            $"{Hermetic} the only model in the composition is this test's script, never a real provider");
        o.EmbeddingProviderType.Name.Should().Be("DisabledEmbeddingProvider",
            $"{Hermetic} with no Cohere key the real registration must choose the no-op embedder");
        o.RerankerType.Name.Should().Be("DisabledReranker",
            $"{Hermetic} with no Cohere key the real registration must choose the no-op reranker");
    }

    private static void AssertIngested(PipelineOutcome o)
    {
        o.LabIngest.Status.Should().Be(DocumentIngestionStatus.Ingested, $"{Ingest} the lab fixture is new");
        o.LabIngest.FactCount.Should().Be(LabFactCount, $"{Ingest} one fact per reported result row");
        o.IntakeIngest.Status.Should().Be(DocumentIngestionStatus.Ingested, $"{Ingest} the intake fixture is new");
        o.IntakeIngest.FactCount.Should().Be(IntakeFactCount, $"{Ingest} every intake item becomes a fact");
        o.LabReingest.Status.Should().Be(DocumentIngestionStatus.AlreadyIngested,
            $"{Ingest} the same bytes are never extracted twice (W2-D3)");
        o.DocumentHashes.Should().Equal(
            [HermeticPipeline.Sha256(o.LabBytes), HermeticPipeline.Sha256(o.IntakeBytes)],
            $"{Ingest} the model saw each fixture's own bytes exactly once - the re-ingest made no call");
    }

    private static void AssertExtracted(PipelineOutcome o)
    {
        o.LabDocument.Should().NotBeNull($"{Extract} the lab was persisted under its content hash");
        foreach (var row in SyntheticLabPanel.Rows)
        {
            var fact = o.LabDocument!.DerivedFacts.Should()
                .ContainSingle(f => f.Citation.FieldOrChunkId == row.Test, $"{Extract} one fact for {row.Test}").Subject;
            fact.ExtractionConfidence.Should().Be(1.0,
                $"{Extract} the quote for {row.Test} must be located verbatim in the PDF's text layer");
            fact.Citation.BoundingBox.Should().NotBeNull($"{Extract} a located quote carries its glyph box");
            WordsInBox(o.LabBytes, fact.Citation.BoundingBox!).Should().Be(row.Quote,
                $"{Extract} the box on {row.Test} must enclose exactly the printed row it quotes");
        }

        var invented = o.LabDocument!.DerivedFacts.Should().ContainSingle(
            f => f.Citation.FieldOrChunkId == CannedReplies.InventedLabRow.Test,
            $"{Extract} a row the model invented still ships as a fact, marked").Subject;
        invented.ExtractionConfidence.Should().Be(0.0,
            $"{Extract} a quote searched for and not found in the text layer is unlocatable");
        invented.Citation.BoundingBox.Should().BeNull(
            $"{Extract} an unlocatable quote loses the model's box, so click-to-source cannot highlight text that says something else");

        o.IntakeDocument.Should().NotBeNull($"{Extract} the intake form was persisted under its content hash");
        var medication = o.IntakeDocument!.DerivedFacts.Should().ContainSingle(
            f => f.FactType == DerivedFactType.IntakeMedication && f.Citation.QuoteOrValue == SyntheticIntakeForm.Spironolactone,
            $"{Extract} the spironolactone line is a medication fact").Subject;
        medication.ExtractionConfidence.Should().Be(0.5,
            $"{Extract} an image has no text layer, so its quote is unchecked, neither corroborated nor impeached");
        medication.Citation.BoundingBox.Should().Equal(SyntheticIntakeForm.BoxOf(SyntheticIntakeForm.Spironolactone),
            $"{Extract} with nothing to check against, the model's box is kept - and on this form it is the true one");
    }

    private static void AssertPersisted(PipelineOutcome o)
    {
        o.PatientFacts.Should().HaveCount(LabFactCount + IntakeFactCount,
            $"{Persist} the patient read returns every fact both documents produced, and no duplicate from the re-ingest");
        o.PatientFacts.Select(f => f.Document?.OpenEmrDocumentReferenceId).Distinct().Should().BeEquivalentTo(
            [HermeticPipeline.LabDocumentReferenceId, HermeticPipeline.IntakeDocumentReferenceId],
            $"{Persist} every fact comes back with the OpenEMR DocumentReference it was derived from");
        o.PatientFacts.Should().OnlyContain(f => f.Citation.SourceType == CitationSourceType.Derived,
            $"{Persist} document facts are source_type derived");
    }

    private static void AssertRetrieved(PipelineOutcome o) =>
        o.Result.Evidence.Select(e => e.ChunkId).Should().Equal(
            [InMemoryGuidelineCorpus.PotassiumChunk.ChunkId],
            $"{Retrieve} the question selects the potassium guideline chunk and only it");

    private static void AssertCited(PipelineOutcome o)
    {
        var tokens = CitationToken.Matches(o.Result.Answer).Select(m => (Type: m.Groups[1].Value, Id: m.Groups[2].Value)).ToList();
        tokens.Should().NotBeEmpty($"{Cite} the answer carries machine-readable citations");
        foreach (var (type, id) in tokens)
        {
            var resolves = type switch
            {
                "Derived" => o.Result.DocumentCitations.Any(c => c.FactId == id),
                "Guideline" => o.Result.Evidence.Any(e => e.ChunkId == id),
                _ => false,
            };
            resolves.Should().BeTrue($"{Cite} [{type}/{id}] must resolve to a citation the result carries");
        }

        var potassium = CitedDocument(o, tokens, SyntheticLabPanel.Rows[0].Quote);
        potassium.SourceDocumentId.Should().Be(HermeticPipeline.LabDocumentReferenceId,
            $"{Cite} the potassium value resolves to the lab's DocumentReference");
        potassium.Page.Should().Be(1, $"{Cite} on the page it was printed on");
        WordsInBox(o.LabBytes, potassium.BoundingBox ?? []).Should().Be(SyntheticLabPanel.Rows[0].Quote,
            $"{Cite} and to the region of the fixture that prints it (FR-CITE-2)");

        var spironolactone = CitedDocument(o, tokens, SyntheticIntakeForm.Spironolactone);
        spironolactone.SourceDocumentId.Should().Be(HermeticPipeline.IntakeDocumentReferenceId,
            $"{Cite} the medication resolves to the intake form's DocumentReference");
        spironolactone.BoundingBox.Should().Equal(SyntheticIntakeForm.BoxOf(SyntheticIntakeForm.Spironolactone),
            $"{Cite} and to where the form prints it");

        tokens.Should().Contain(("Guideline", InMemoryGuidelineCorpus.PotassiumChunk.ChunkId),
            $"{Cite} the recommendation cites the retrieved guideline chunk");
    }

    private static void AssertCriticked(PipelineOutcome o)
    {
        o.Result.SuppressedClaims.Select(c => c.Line).Should().Contain(CannedReplies.FabricatedClaim,
            $"{Critic} an uncited clinical value is suppressed");
        o.Result.Answer.Should().NotContain(CannedReplies.FabricatedClaim, $"{Critic} and never ships");
        o.Result.Answer.Split('\n').Should().HaveCount(3, $"{Critic} while the three cited statements ship intact");
    }

    private static void AssertNoPhi(PipelineOutcome o)
    {
        o.Logs.Should().NotBeEmpty($"{Phi} the capture has to have seen the run's logs for their absence of PHI to mean anything");
        o.SpanText.Should().NotBeEmpty($"{Phi} and the recorder the run's spans");
        foreach (var sentinel in PhiSentinels(o))
        {
            o.Logs.Should().NotContain(l => sentinel.IsCarriedBy(l),
                $"{Phi} no log may carry '{sentinel.Value}' (CONVENTIONS.md section 7)");
            o.SpanText.Should().NotContain(s => sentinel.IsCarriedBy(s),
                $"{Phi} no span may carry '{sentinel.Value}' (ARCHITECTURE-DOCUMENTS.md section 12)");
        }
    }

    // Identifiers, each name part on its own, what the documents say, what the clinician asked and what the
    // model drafted.
    private static IEnumerable<LogSentinel> PhiSentinels(PipelineOutcome o) =>
        new[]
        {
            SyntheticCohort.PatientName, SyntheticCohort.BirthDate, SyntheticCohort.Mrn, HermeticPipeline.PatientId,
            HermeticPipeline.Question, SyntheticIntakeForm.Spironolactone, SyntheticIntakeForm.ChiefConcern,
            SyntheticIntakeForm.FamilyHistory, "5.9 mmol/L",
        }
        .Concat(SyntheticLabPanel.Rows.Append(CannedReplies.InventedLabRow).Select(r => r.Quote))
        .Concat((o.Draft ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        .Select(LogSentinel.Verbatim)
        .Concat(LogSentinel.NamePartsOf(SyntheticCohort.PatientName));

    private static DocumentCitation CitedDocument(
        PipelineOutcome o, List<(string Type, string Id)> tokens, string quote)
    {
        var cited = o.Result.DocumentCitations.Where(c => c.Quote == quote).ToList();
        cited.Should().ContainSingle($"{Cite} the result carries one click-to-source citation quoting '{quote}'");
        tokens.Should().Contain(("Derived", cited[0].FactId), $"{Cite} and the answer cites it by its token");
        return cited[0];
    }

    // Independent of the production resolver: PdfPig's own words whose centres fall inside the box.
    private static string WordsInBox(byte[] pdf, double[] box)
    {
        if (box.Length != 4)
        {
            return string.Empty;
        }

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        var words = page.GetWords().Where(w =>
        {
            var cx = (w.BoundingBox.Left + w.BoundingBox.Right) / 2 / page.Width;
            var cy = (page.Height - ((w.BoundingBox.Top + w.BoundingBox.Bottom) / 2)) / page.Height;
            return cx >= box[0] && cx <= box[0] + box[2] && cy >= box[1] && cy <= box[1] + box[3];
        });
        return string.Join(' ', words.Select(w => w.Text));
    }
}
