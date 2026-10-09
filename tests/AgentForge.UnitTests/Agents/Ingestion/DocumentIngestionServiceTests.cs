using FakeItEasy;
using FluentAssertions;
using AgentForge.Agents.Ingestion;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Llm;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Agents.Ingestion;

/// <summary>
/// Drives <see cref="DocumentIngestionService"/> — the pre-visit ingestion. The document is already in
/// OpenEMR (the front desk uploaded it natively), so the sidecar only extracts + persists facts citing the
/// supplied DocumentReference id. Verifies content-hash idempotency (skip re-work), the extraction-rejected
/// degrade path (persist nothing), and the happy path (persist facts with the source-document lineage).
/// reference: ARCHITECTURE-DOCUMENTS.md §4
/// </summary>
public sealed class DocumentIngestionServiceTests
{
    private readonly IDocumentExtractor _extractor = A.Fake<IDocumentExtractor>();
    private readonly IDerivedFactStore _store = A.Fake<IDerivedFactStore>();
    private readonly IDerivedFactMapper _mapper = A.Fake<IDerivedFactMapper>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();

    public DocumentIngestionServiceTests()
    {
        A.CallTo(() => _store.FindByContentHashAsync(A<string>._, A<CancellationToken>._))
            .Returns((IngestedDocument?)null);
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Ok(ClinicalDocumentType.LabPdf, "{\"tests\":[]}", new LlmUsage(0, 0, 0m)));
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact> { Fact(), Fact() });
    }

    private DocumentIngestionService Service() =>
        new(_extractor, _store, _mapper, _metrics, TimeProvider.System, A.Fake<ILogger<DocumentIngestionService>>());

    private static DerivedFact Fact(double? confidence = null, string factType = DerivedFactType.LabResult) => new()
    {
        FactType = factType,
        PayloadJson = "{}",
        Citation = new Citation { SourceType = CitationSourceType.Derived, SourceId = "dr-1" },
        ExtractionConfidence = confidence,
    };

    private static DocumentIngestionRequest Request() => new()
    {
        PatientId = "p-1",
        DocumentReferenceId = "dr-1",
        DocumentType = ClinicalDocumentType.LabPdf,
        Content = [1, 2, 3],
        MediaType = "application/pdf",
    };

    [Fact]
    public async Task IngestAsync_WhenContentAlreadyIngested_ReturnsAlreadyIngested_AndSkipsExtraction()
    {
        var existing = new IngestedDocument
        {
            PatientId = "p-1",
            ContentHash = ContentHash.Compute([1, 2, 3]),
            OpenEmrDocumentReferenceId = "dr-1",
            DerivedFacts = [Fact()],
        };
        A.CallTo(() => _store.FindByContentHashAsync(A<string>._, A<CancellationToken>._)).Returns(existing);

        var result = await Service().IngestAsync(Request());

        result.Status.Should().Be(DocumentIngestionStatus.AlreadyIngested);
        result.FactCount.Should().Be(1);
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _store.AddAsync(A<IngestedDocument>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _metrics.RecordDocumentIngestion("already_ingested", A<TimeSpan>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_WhenExtractionThrows_RecordsErrorOutcome_AndRethrows()
    {
        // a throwing extraction previously recorded nothing at all, so a total extractor
        // outage moved neither the ingestion p95 nor the failure-rate ratio - only the ingestion
        // rate, to zero, which is indistinguishable from nobody uploading. The "error" outcome closes
        // that: the counter (and the duration histogram, on the same call) now sees the attempt.
        var thrown = new InvalidOperationException("provider unreachable");
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(thrown);

        var act = () => Service().IngestAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider unreachable");
        A.CallTo(() => _store.AddAsync(A<IngestedDocument>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _metrics.RecordDocumentIngestion("error", A<TimeSpan>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordDocumentIngestion(A<string>.That.Not.IsEqualTo("error"), A<TimeSpan>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task IngestAsync_WhenExtractionCancelled_DoesNotRecordErrorOutcome_AndPropagatesCancellation()
    {
        // Cancellation is the caller giving up, not the extractor failing - AgentOrchestrator's
        // existing RecordAgentTurn precedent draws the same line (`ex is not OperationCanceledException`).
        // Counting a cancelled request as an ingestion "error" would make the ratio and the histogram
        // move on client behaviour rather than extractor health.
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Service().IngestAsync(Request());

        await act.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => _metrics.RecordDocumentIngestion(A<string>._, A<TimeSpan>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task IngestAsync_WhenPersistThrows_RecordsErrorOutcome_AndRethrows()
    {
        // a separate change (review note): a throw from the store AFTER a successful extraction is
        // equally invisible today - the counter is written only on the three RETURNING paths, and
        // persistence sits between extraction and the "ingested" write. Same outcome, same blind spot.
        var thrown = new InvalidOperationException("database unavailable");
        A.CallTo(() => _store.AddAsync(A<IngestedDocument>._, A<CancellationToken>._)).ThrowsAsync(thrown);

        var act = () => Service().IngestAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("database unavailable");
        A.CallTo(() => _metrics.RecordDocumentIngestion("error", A<TimeSpan>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordDocumentIngestion("ingested", A<TimeSpan>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task IngestAsync_WhenExtractionRejected_ReturnsRejected_AndDoesNotPersist()
    {
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Rejected(ClinicalDocumentType.LabPdf, "schema violation"));

        var result = await Service().IngestAsync(Request());

        result.Status.Should().Be(DocumentIngestionStatus.ExtractionRejected);
        A.CallTo(() => _store.AddAsync(A<IngestedDocument>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => _metrics.RecordDocumentIngestion("extraction_rejected", A<TimeSpan>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_OnSuccess_PersistsFactsCitingTheSuppliedDocumentReference()
    {
        IngestedDocument? persisted = null;
        A.CallTo(() => _store.AddAsync(A<IngestedDocument>._, A<CancellationToken>._))
            .Invokes((IngestedDocument d, CancellationToken _) => persisted = d);

        var result = await Service().IngestAsync(Request());

        result.Status.Should().Be(DocumentIngestionStatus.Ingested);
        result.DocumentReferenceId.Should().Be("dr-1");
        result.FactCount.Should().Be(2);

        persisted.Should().NotBeNull();
        persisted!.ContentHash.Should().Be(ContentHash.Compute([1, 2, 3]));
        persisted.OpenEmrDocumentReferenceId.Should().Be("dr-1");
        persisted.DerivedFacts.Should().HaveCount(2);
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, "dr-1")).MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordDocumentIngestion("ingested", A<TimeSpan>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_OnSuccess_RecordsTheLocatedFractionOfCheckableCitationsOnce()
    {
        // FR-OBS-W2-2's "extraction confidence per document": ONE observation per ingested document, so the
        // histogram's population is documents and a 500-fact lab cannot outvote fifty intake forms.
        // The value is the LOCATED FRACTION over the citations that could be checked - here three located
        // and one absent, so 0.75 - and not the mean of the per-fact scores, which would be 0.7 and would
        // mean nothing (see the ordering case below).
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact> { Fact(1.0), Fact(1.0), Fact(1.0), Fact(0.0), Fact(0.5) });

        await Service().IngestAsync(Request());

        A.CallTo(() => _metrics.RecordExtractionConfidence("lab_pdf", 0.75)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_WhenNoCitationCouldBeChecked_RecordsNoConfidenceButStillCountsEveryFact()
    {
        // A scan has no text layer, so every citation resolves `unchecked` and there is nothing to be
        // confident about. "Nothing could be checked" is a different KIND of answer from "some quotes were
        // not found", so it is not a number on this axis at all: the document leaves the histogram rather
        // than landing at a value that would sort it against partly-fabricated documents. It stays fully
        // visible on the field-outcome counter, and the gap against
        // agentforge_document_ingestions_total{outcome="ingested"} is how many such documents there were.
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact> { Fact(0.5), Fact(0.5), Fact(0.5) });

        await Service().IngestAsync(Request());

        ConfidenceCalls().Should().BeEmpty();
        FieldOutcomeCalls().Should().HaveCount(3).And.OnlyContain(call => call.Outcome == "unchecked");
    }

    [Fact]
    public async Task IngestAsync_WhenAbsentQuotesSitAmongUncheckableOnes_ScoresOnlyTheCheckableOnes()
    {
        // The ordering guard, and the reason this is a located fraction rather than a mean. This document
        // has ONE citation that could be checked and it was not found - every quote the extractor could
        // verify was absent - so it scores 0.0, the fabrication floor. Averaging the per-fact scores instead
        // would score it 0.4 (one 0.0 plus four 0.5s), lifting it ABOVE a wholly unverifiable scan and
        // burying the only fact the metric is here to surface.
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact> { Fact(0.0), Fact(0.5), Fact(0.5), Fact(0.5), Fact(0.5) });

        await Service().IngestAsync(Request());

        A.CallTo(() => _metrics.RecordExtractionConfidence("lab_pdf", 0.0)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_OnSuccess_RecordsOneFieldOutcomePerDerivedFact()
    {
        // The denominator the pass rate needs: every fact is counted under its field, whatever it scored.
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact>
            {
                Fact(1.0, DerivedFactType.LabResult),
                Fact(0.0, DerivedFactType.LabResult),
                Fact(0.5, DerivedFactType.IntakeAllergy),
            });

        await Service().IngestAsync(Request());

        A.CallTo(() => _metrics.RecordExtractionFieldOutcome(DerivedFactType.LabResult, "exact"))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordExtractionFieldOutcome(DerivedFactType.LabResult, "unlocatable"))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordExtractionFieldOutcome(DerivedFactType.IntakeAllergy, "unchecked"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task IngestAsync_WhenAFactCarriesAScoreTheCurrentRuleCannotProduce_LeavesItOutOfBoth()
    {
        // The forward-only correction: facts written before the scoring rule changed keep their old
        // value and are never backfilled. Such a score has no outcome under the current rule, so it must not
        // be rounded into one - it leaves the field counter alone and stays out of the located fraction's
        // numerator and denominator alike, rather than being silently charged to `unchecked`.
        A.CallTo(() => _mapper.Map(A<DocumentExtractionResult>._, A<string?>._))
            .Returns(new List<DerivedFact> { Fact(1.0), Fact(0.75) });

        await Service().IngestAsync(Request());

        A.CallTo(() => _metrics.RecordExtractionConfidence("lab_pdf", 1.0)).MustHaveHappenedOnceExactly();
        FieldOutcomeCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task IngestAsync_WhenExtractionRejected_RecordsNeitherExtractionSignal()
    {
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Rejected(ClinicalDocumentType.LabPdf, "schema violation"));

        await Service().IngestAsync(Request());

        ConfidenceCalls().Should().BeEmpty();
        FieldOutcomeCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_WhenContentAlreadyIngested_RecordsNeitherExtractionSignal()
    {
        // Idempotency reaches the metrics too: the same bytes re-posted must not re-count facts that are
        // already in the population, or the pass rate moves without any extraction having happened.
        var existing = new IngestedDocument
        {
            PatientId = "p-1",
            ContentHash = ContentHash.Compute([1, 2, 3]),
            OpenEmrDocumentReferenceId = "dr-1",
            DerivedFacts = [Fact(1.0)],
        };
        A.CallTo(() => _store.FindByContentHashAsync(A<string>._, A<CancellationToken>._)).Returns(existing);

        await Service().IngestAsync(Request());

        ConfidenceCalls().Should().BeEmpty();
        FieldOutcomeCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_WithTheRealMapper_NeverPutsModelSuppliedTextOnTheFieldLabel()
    {
        // The hazard, exercised rather than asserted. `test_name` is whatever the model emitted, and
        // the citation the mapper builds carries it as FieldOrChunkId - so wiring the label to the citation
        // instead of to the fact type is a one-character mistake that mints a permanent series per lab test
        // and puts document text on an exported label. Driving the REAL mapper with a hostile name is what
        // makes that fail here; a test over a faked mapper would pass either way.
        const string modelText = "Bruno Kowalczyk potassium";
        var hostile =
            """
            {"tests":[{"test_name":"MODEL_TEXT","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","match":"exact"}}]}
            """.Replace("MODEL_TEXT", modelText, StringComparison.Ordinal);
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Ok(ClinicalDocumentType.LabPdf, hostile, new LlmUsage(0, 0, 0m)));
        var service = new DocumentIngestionService(
            _extractor, _store, new DerivedFactMapper(), _metrics, TimeProvider.System,
            A.Fake<ILogger<DocumentIngestionService>>());

        await service.IngestAsync(Request());

        var fields = FieldOutcomeCalls().Select(call => call.Field).ToList();
        fields.Should().NotBeEmpty("the hostile document must actually have produced a fact to label");
        fields.Should().OnlyContain(field => DerivedFactType.Labels.Contains(field));
        FieldOutcomeCalls().Should().Equal((DerivedFactType.LabResult, "exact"));
    }

    // The labels that actually reached the meter, read off the recorded calls rather than matched against.
    // Asserting on the values is what lets a test say "and it was not the model's string" out loud.
    private IEnumerable<(string Field, string Outcome)> FieldOutcomeCalls() =>
        Fake.GetCalls(_metrics)
            .Where(call => call.Method.Name == nameof(IAgentForgeMetrics.RecordExtractionFieldOutcome))
            .Select(call => ((string)call.Arguments[0]!, (string)call.Arguments[1]!));

    private IEnumerable<(string DocumentType, double Confidence)> ConfidenceCalls() =>
        Fake.GetCalls(_metrics)
            .Where(call => call.Method.Name == nameof(IAgentForgeMetrics.RecordExtractionConfidence))
            .Select(call => ((string)call.Arguments[0]!, (double)call.Arguments[1]!));
}
