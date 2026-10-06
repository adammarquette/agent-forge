using System.Diagnostics;
using AgentForge.Agents;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.Retrieval;
using AgentForge.UnitTests.TestSupport;
using AgentForge.Verification;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.UnitTests.Agents;

/// <summary>
/// NFR-TRACE-W2 on the graph itself: <see cref="EvidenceAgentSupervisor"/> opens one supervisor span
/// and one child span per worker that actually ran, each naming its worker and why it was routed there, and
/// nothing on any of them identifies the patient (ARCHITECTURE-DOCUMENTS.md §12: traces carry no patient identifiers,
/// raw document text or extracted clinical values). Guarded failure mode: a refactor that flattens the graph's
/// spans, drops a parent link or leaks PHI into a span passes every behavioural test in
/// <see cref="EvidenceAgentSupervisorTests"/>, so it has to fail here instead.
/// </summary>
public sealed class EvidenceAgentSupervisorTracingTests
{
    // Synthetic sentinels, each distinctive enough that a substring hit in a span can only be a leak.
    private const string PatientId = "patient-sentinel-8841";
    private const string Question = "Is Zelda Quimby's potassium of 5.93 still safe on spironolactone?";
    private const string ExtractedValue = "5.93";
    private const string ExtractedTestName = "PotassiumSentinel";
    private const string EvidenceText = "Sentinel guideline text: hold MRA when K exceeds 5.5.";
    private const string DraftAnswer = "Sentinel draft answer about Zelda Quimby.";
    private const string VerifiedAnswer = "Sentinel verified answer about Zelda Quimby.";

    private static readonly string[] Workers = ["intake-extractor", "evidence-retriever", "answer-composer", "critic"];

    private readonly IDocumentExtractor _extractor = A.Fake<IDocumentExtractor>();
    private readonly IEvidenceRetriever _retriever = A.Fake<IEvidenceRetriever>();
    private readonly IDerivedFactStore _factStore = A.Fake<IDerivedFactStore>();
    private readonly ILlmProvider _llm = A.Fake<ILlmProvider>();
    private readonly IClinicalResponseVerifier _verifier = A.Fake<IClinicalResponseVerifier>();

    public EvidenceAgentSupervisorTracingTests()
    {
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Ok(
                ClinicalDocumentType.LabPdf,
                $$"""{"tests":[{"test_name":"{{ExtractedTestName}}","value":"{{ExtractedValue}}","unit":"mmol/L"}]}""",
                new LlmUsage(0, 0, 0m)));
        IReadOnlyList<EvidenceSnippet> evidence =
        [
            new EvidenceSnippet { DocumentId = "acc-hf-2022", Section = "7.3", ChunkId = "c-1", Text = EvidenceText, Score = 0.9 },
        ];
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).Returns(evidence);
        IReadOnlyList<DerivedFact> noFacts = [];
        A.CallTo(() => _factStore.GetByPatientAsync(A<string>._, A<CancellationToken>._)).Returns(noFacts);
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmResponse(DraftAnswer, [], LlmStopReason.EndTurn, new LlmUsage(0, 0, 0m)));
        A.CallTo(() => _verifier.Verify(A<string>._, A<IReadOnlyCollection<string>>._))
            .Returns(new VerificationResult(true, VerifiedAnswer, [], []));
    }

    private EvidenceAgentSupervisor CreateSut() => new(
        _extractor, _retriever, _factStore, _llm, _verifier, A.Fake<IAgentForgeMetrics>(),
        NullLogger<EvidenceAgentSupervisor>.Instance);

    private static EvidenceAgentRequest RequestWithDocument() => new()
    {
        PatientId = PatientId,
        Question = Question,
        Document = new PendingDocument(ClinicalDocumentType.LabPdf, "Zelda Quimby K 5.93"u8.ToArray(), "application/pdf"),
    };

    private static Activity SingleSpan(SpanRecorder recorder, string name) =>
        recorder.Spans.Should().ContainSingle(s => s.DisplayName == name, $"one {name} span per run").Subject;

    [Fact]
    public async Task RunAsync_WithDocument_OpensOneSupervisorSpanUnderTheCallerWithOneChildSpanPerWorker()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        var supervisor = SingleSpan(recorder, "evidence.supervisor");
        supervisor.ParentSpanId.Should().Be(recorder.Request.SpanId, "the supervisor span nests under whatever span called it");
        var children = recorder.ChildrenOf(supervisor);
        children.Select(c => c.GetTagItem("agentforge.worker")).Should().Equal(Workers.Cast<object>(),
            "each of the four workers runs once, in graph order, as a direct child of the supervisor span");
        children.Select(c => c.DisplayName).Should().Equal(Workers.Select(w => $"worker.{w}"));
        recorder.Spans.Should().HaveCount(1 + Workers.Length, "no worker span is opened anywhere but under the supervisor");
    }

    [Fact]
    public async Task RunAsync_WithoutDocument_OpensNoSpanForTheExtractorThatDidNotRun()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(
            new EvidenceAgentRequest { PatientId = PatientId, Question = Question }, CancellationToken.None);

        var supervisor = SingleSpan(recorder, "evidence.supervisor");
        recorder.ChildrenOf(supervisor).Select(c => c.GetTagItem("agentforge.worker"))
            .Should().Equal("evidence-retriever", "answer-composer", "critic");
    }

    [Fact]
    public async Task RunAsync_WithDocument_EachWorkerSpanCarriesTheRoutingDecisionAndItsOutcome()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        var byWorker = recorder.Spans.Where(s => s.DisplayName.StartsWith("worker.", StringComparison.Ordinal))
            .ToDictionary(s => (string)s.GetTagItem("agentforge.worker")!);
        byWorker["intake-extractor"].GetTagItem("agentforge.route.from").Should().Be("supervisor");
        byWorker["intake-extractor"].GetTagItem("agentforge.route.reason").Should().Be("document attached; extraction needed");
        byWorker["intake-extractor"].GetTagItem("agentforge.outcome").Should().Be("extracted");
        byWorker["evidence-retriever"].GetTagItem("agentforge.route.reason").Should().Be("question needs guideline evidence");
        byWorker["evidence-retriever"].GetTagItem("agentforge.outcome").Should().Be("hit");
        byWorker["evidence-retriever"].GetTagItem("agentforge.evidence.snippet_count").Should().Be(1);
        byWorker["answer-composer"].GetTagItem("agentforge.route.reason").Should().Be("facts + evidence assembled");
        byWorker["answer-composer"].GetTagItem("agentforge.outcome").Should().Be("composed");
        byWorker["critic"].GetTagItem("agentforge.route.from").Should().Be("answer-composer");
        byWorker["critic"].GetTagItem("agentforge.route.reason").Should().Be("draft ready for verification");
        byWorker["critic"].GetTagItem("agentforge.outcome").Should().Be("passed");
        byWorker["critic"].GetTagItem("agentforge.critic.suppressed_claims").Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ExtractionRejectedAndClaimsSuppressed_WorkerSpansSayWhatWentWrong()
    {
        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .Returns(DocumentExtractionResult.Rejected(ClinicalDocumentType.LabPdf, "schema failed"));
        IReadOnlyList<EvidenceSnippet> noEvidence = [];
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).Returns(noEvidence);
        A.CallTo(() => _verifier.Verify(A<string>._, A<IReadOnlyCollection<string>>._))
            .Returns(new VerificationResult(false, VerifiedAnswer, [new SuppressedClaim(DraftAnswer, "uncited")], []));
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        var byWorker = recorder.Spans.Where(s => s.DisplayName.StartsWith("worker.", StringComparison.Ordinal))
            .ToDictionary(s => (string)s.GetTagItem("agentforge.worker")!);
        byWorker["intake-extractor"].GetTagItem("agentforge.outcome").Should().Be("rejected");
        byWorker["evidence-retriever"].GetTagItem("agentforge.outcome").Should().Be("miss");
        byWorker["evidence-retriever"].GetTagItem("agentforge.evidence.snippet_count").Should().Be(0);
        byWorker["critic"].GetTagItem("agentforge.outcome").Should().Be("suppressed");
        byWorker["critic"].GetTagItem("agentforge.critic.suppressed_claims").Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_WithDocument_SupervisorSpanRecordsEveryHandoffTheResultReports()
    {
        using var recorder = SpanRecorder.Start();

        var result = await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        var supervisor = SingleSpan(recorder, "evidence.supervisor");
        var handoffs = supervisor.Events.Where(e => e.Name == "handoff").Select(e =>
        {
            var tags = e.Tags.ToDictionary(t => t.Key, t => t.Value);
            return new HandoffEvent(
                (string)tags["agentforge.route.from"]!, (string)tags["agentforge.route.to"]!,
                (string)tags["agentforge.route.reason"]!);
        });
        handoffs.Should().Equal(result.Handoffs, "the trace and the logged handoff list are the same routing record");
        supervisor.GetTagItem("agentforge.handoff_count").Should().Be(result.Handoffs.Count);
    }

    [Fact]
    public async Task RunAsync_WorkerThrows_MarksItsSpanFailedByExceptionTypeOnlyAndRethrows()
    {
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException($"retrieval failed for {PatientId}"));
        using var recorder = SpanRecorder.Start();

        var act = () => CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var retriever = recorder.Spans.Should()
            .ContainSingle(s => s.DisplayName == "worker.evidence-retriever").Subject;
        retriever.Status.Should().Be(ActivityStatusCode.Error);
        retriever.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
        SingleSpan(recorder, "evidence.supervisor").Status.Should().Be(ActivityStatusCode.Error);
        recorder.ExportedStrings().Should().NotContain(s => s.Contains(PatientId, StringComparison.Ordinal),
            "an exception message is free text and may carry PHI, so only its type reaches the trace");
    }

    [Fact]
    public async Task RunAsync_WithDocument_NoSpanCarriesPatientIdentifiersDocumentTextOrClinicalValues()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        string[] phi = [PatientId, Question, "Zelda", "Quimby", ExtractedValue, ExtractedTestName, EvidenceText, DraftAnswer, VerifiedAnswer];
        recorder.Spans.Should().NotBeEmpty();
        recorder.ExportedStrings().Should().NotContain(
            s => phi.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "ARCHITECTURE-DOCUMENTS.md §12: traces carry no patient identifiers, raw document text or extracted clinical values");
    }

    [Fact]
    public async Task RunAsync_WithDocument_EveryAttributeKeyIsOneOfTheBoundedPhiFreeSet()
    {
        // Pins the key set, not only the sentinel values above: a new attribute is a deliberate edit here,
        // made by someone who has had to decide it is not patient data.
        using var recorder = SpanRecorder.Start();

        await CreateSut().RunAsync(RequestWithDocument(), CancellationToken.None);

        recorder.Spans.Should().NotBeEmpty();
        recorder.TagKeys().Distinct().Should().BeSubsetOf(
        [
            "agentforge.worker", "agentforge.outcome", "agentforge.route.from", "agentforge.route.to",
            "agentforge.route.reason", "agentforge.handoff_count", "agentforge.evidence.snippet_count",
            "agentforge.critic.suppressed_claims", "agentforge.critic.constraint_flags", "error.type",
        ]);
    }

    // The real extractor and retriever, over faked providers, so the sub-call spans they open are the ones the
    // graph actually produces rather than ones a fake stands in for.
    private EvidenceAgentSupervisor CreateSutWithRealSubCalls()
    {
        var vlm = A.Fake<ILlmProvider>();
        A.CallTo(() => vlm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._)).Returns(new LlmResponse(
            $$$"""
            {"tests":[{"test_name":"{{{ExtractedTestName}}}","value":"{{{ExtractedValue}}}","unit":"mmol/L",
            "reference_range":null,"collection_date":null,"abnormal_flag":true,
            "citation":{"page":1,"quote":"Zelda Quimby K {{{ExtractedValue}}}","bounding_box":null}}]}
            """,
            [], LlmStopReason.EndTurn, new LlmUsage(0, 0, 0m)));
        var pdfReader = A.Fake<IPdfWordReader>();
        A.CallTo(() => pdfReader.ReadTextLayer(A<ReadOnlyMemory<byte>>._)).Returns(PdfTextLayer.None);
        var extractor = new DocumentExtractor(vlm, pdfReader, A.Fake<IAgentForgeMetrics>(), NullLogger<DocumentExtractor>.Instance);

        var sparse = A.Fake<ISparseRetriever>();
        var dense = A.Fake<IDenseRetriever>();
        IReadOnlyList<EvidenceSnippet> hits =
        [
            new EvidenceSnippet { DocumentId = "acc-hf-2022", Section = "7.3", ChunkId = "c-1", Text = EvidenceText, Score = 0 },
        ];
        A.CallTo(() => sparse.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).Returns(hits);
        A.CallTo(() => dense.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).Returns(hits);
        var reranker = A.Fake<IReranker>();
        A.CallTo(() => reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<RerankedCandidate>)[new RerankedCandidate("c-1", 0.9)]);
        var retriever = new HybridEvidenceRetriever(
            sparse, dense, reranker, A.Fake<IAgentForgeMetrics>(), NullLogger<HybridEvidenceRetriever>.Instance);

        return new(extractor, retriever, _factStore, _llm, _verifier, A.Fake<IAgentForgeMetrics>(),
            NullLogger<EvidenceAgentSupervisor>.Instance);
    }

    [Fact]
    public async Task RunAsync_WithRealSubCalls_VlmAndRetrievalStageSpansNestInsideTheirWorkerSpans()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSutWithRealSubCalls().RunAsync(RequestWithDocument(), CancellationToken.None);

        var extractor = SingleSpan(recorder, "worker.intake-extractor");
        recorder.ChildrenOf(extractor).Select(c => c.DisplayName).Should().Equal(["extraction.vlm"],
            "the VLM call is the extractor worker's one sub-call");
        var retriever = SingleSpan(recorder, "worker.evidence-retriever");
        recorder.ChildrenOf(retriever).Select(c => c.DisplayName).Should().Equal(
            ["retrieval.sparse", "retrieval.dense", "retrieval.fusion", "retrieval.rerank"],
            "each retrieval stage is a child of the retriever worker, not of the supervisor");
        SingleSpan(recorder, "retrieval.rerank").GetTagItem("agentforge.outcome").Should().Be("reranked");
    }

    [Fact]
    public async Task RunAsync_WithRealSubCalls_NoSpanInTheWholeTreeCarriesPhiAndEveryKeyIsBounded()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSutWithRealSubCalls().RunAsync(RequestWithDocument(), CancellationToken.None);

        string[] phi = [PatientId, Question, "Zelda", "Quimby", ExtractedValue, ExtractedTestName, EvidenceText, DraftAnswer, VerifiedAnswer];
        recorder.Spans.Should().Contain(s => s.DisplayName == "extraction.vlm");
        recorder.ExportedStrings().Should().NotContain(
            s => phi.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "ARCHITECTURE-DOCUMENTS.md §12 binds the sub-call spans exactly as it binds the worker spans");
        recorder.TagKeys().Distinct().Should().BeSubsetOf(
        [
            "agentforge.worker", "agentforge.outcome", "agentforge.route.from", "agentforge.route.to",
            "agentforge.route.reason", "agentforge.handoff_count", "agentforge.evidence.snippet_count",
            "agentforge.critic.suppressed_claims", "agentforge.critic.constraint_flags", "error.type",
            "agentforge.retrieval.stage", "agentforge.retrieval.candidate_count", "agentforge.retrieval.skip_reason",
            "agentforge.document.type", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens",
        ]);
    }
}
