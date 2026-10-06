using System.Diagnostics;
using AgentForge.Agents;
using AgentForge.Observability;
using AgentForge.Retrieval;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.UnitTests.Retrieval;

/// <summary>
/// NFR-TRACE-W2 one level below the worker spans: <see cref="HybridEvidenceRetriever"/> opens one
/// child span per stage - sparse, dense, fusion, rerank - under whatever span called it, so a waterfall says
/// which stage cost a request its latency. Guarded failure modes: a rerank that never ran, or failed, reading
/// as a fast one (METRICS.md once misread a failed rerank as a skipped one), a degraded stage that the
/// <c>retrieval_degradations</c> counter sees but the trace does not, and the query or a snippet's text
/// reaching a span attribute (ARCHITECTURE-DOCUMENTS.md §12).
/// </summary>
public sealed class HybridEvidenceRetrieverTracingTests
{
    // Synthetic sentinels, each distinctive enough that a substring hit in a span can only be a leak.
    private const string Query = "Should Zelda Quimby stay on spironolactone with potassium 5.93?";
    private const string SnippetText = "Sentinel guideline body: hold MRA when K exceeds 5.5.";

    private static readonly string[] Stages = ["retrieval.sparse", "retrieval.dense", "retrieval.fusion", "retrieval.rerank"];

    private readonly ISparseRetriever _sparse = A.Fake<ISparseRetriever>();
    private readonly IDenseRetriever _dense = A.Fake<IDenseRetriever>();
    private readonly IReranker _reranker = A.Fake<IReranker>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();

    public HybridEvidenceRetrieverTracingTests()
    {
        ReturnsFromSparse(Snip("a"), Snip("b"));
        ReturnsFromDense(Snip("b"), Snip("c"));
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily((string _, IReadOnlyList<RerankDocument> docs, int k, CancellationToken _) =>
                (IReadOnlyList<RerankedCandidate>)[.. docs.Take(k).Select((d, i) => new RerankedCandidate(d.Id, 1.0 - (i * 0.01)))]);
    }

    private HybridEvidenceRetriever CreateSut() =>
        new(_sparse, _dense, _reranker, _metrics, NullLogger<HybridEvidenceRetriever>.Instance);

    private static EvidenceSnippet Snip(string chunkId) =>
        new() { DocumentId = "acc-hf-2022", Section = "7.3", ChunkId = chunkId, Text = SnippetText, Score = 0 };

    private void ReturnsFromSparse(params EvidenceSnippet[] snippets) =>
        A.CallTo(() => _sparse.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<EvidenceSnippet>)snippets);

    private void ReturnsFromDense(params EvidenceSnippet[] snippets) =>
        A.CallTo(() => _dense.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<EvidenceSnippet>)snippets);

    private static Activity Stage(SpanRecorder recorder, string name) =>
        recorder.Spans.Should().ContainSingle(s => s.DisplayName == name, $"one {name} span per retrieval").Subject;

    [Fact]
    public async Task RetrieveAsync_WithCandidates_OpensOneChildSpanPerStageInPipelineOrder()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        var children = recorder.ChildrenOf(recorder.Request);
        children.Select(c => c.DisplayName).Should().Equal(Stages,
            "each stage is a direct child of the calling span - the evidence-retriever worker span in the graph");
        children.Select(c => c.GetTagItem("agentforge.retrieval.stage"))
            .Should().Equal("sparse", "dense", "fusion", "rerank");
        recorder.Spans.Should().HaveCount(Stages.Length);
    }

    [Fact]
    public async Task RetrieveAsync_WithCandidates_EachStageSpanCarriesItsOutcomeAndCandidateCount()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        Stage(recorder, "retrieval.sparse").GetTagItem("agentforge.outcome").Should().Be("retrieved");
        Stage(recorder, "retrieval.sparse").GetTagItem("agentforge.retrieval.candidate_count").Should().Be(2);
        Stage(recorder, "retrieval.dense").GetTagItem("agentforge.outcome").Should().Be("retrieved");
        Stage(recorder, "retrieval.dense").GetTagItem("agentforge.retrieval.candidate_count").Should().Be(2);
        Stage(recorder, "retrieval.fusion").GetTagItem("agentforge.outcome").Should().Be("fused");
        Stage(recorder, "retrieval.fusion").GetTagItem("agentforge.retrieval.candidate_count").Should().Be(3,
            "fusion's count is the de-duplicated pool the reranker is handed");
        var rerank = Stage(recorder, "retrieval.rerank");
        rerank.GetTagItem("agentforge.outcome").Should().Be("reranked");
        rerank.GetTagItem("agentforge.retrieval.candidate_count").Should().Be(3);
        rerank.GetTagItem("agentforge.retrieval.skip_reason").Should().BeNull("a rerank that ran was not skipped");
    }

    [Fact]
    public async Task RetrieveAsync_NoCandidates_RerankSpanSaysSkippedAndWhyRatherThanLookingFast()
    {
        ReturnsFromSparse();
        ReturnsFromDense();
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        recorder.ChildrenOf(recorder.Request).Select(c => c.DisplayName).Should().Equal(Stages,
            "the tree keeps its shape, so a missing stage is never mistaken for an untraced one");
        foreach (var name in new[] { "retrieval.fusion", "retrieval.rerank" })
        {
            var span = Stage(recorder, name);
            span.GetTagItem("agentforge.outcome").Should().Be("skipped");
            span.GetTagItem("agentforge.retrieval.skip_reason").Should().Be("no_candidates");
            span.GetTagItem("agentforge.retrieval.candidate_count").Should().Be(0);
            span.Status.Should().Be(ActivityStatusCode.Unset, "skipping for want of candidates is not a failure");
        }
    }

    [Fact]
    public async Task RetrieveAsync_RerankerReturnsNoRanking_RerankSpanSaysUnrankedNotReranked()
    {
        // The disabled reranker (no Cohere key) answers this way: it ran, and the fused order was kept.
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .Returns((IReadOnlyList<RerankedCandidate>)[]);
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        var rerank = Stage(recorder, "retrieval.rerank");
        rerank.GetTagItem("agentforge.outcome").Should().Be("unranked");
        rerank.GetTagItem("agentforge.retrieval.candidate_count").Should().Be(3);
    }

    [Theory]
    [InlineData("dense")]
    [InlineData("sparse")]
    public async Task RetrieveAsync_HalfThrows_MarksThatStageDegradedUnderTheSameNameTheCounterUses(string half)
    {
        var failure = new InvalidOperationException($"{half} failed for {Query}");
        if (half == "dense")
        {
            A.CallTo(() => _dense.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).ThrowsAsync(failure);
        }
        else
        {
            A.CallTo(() => _sparse.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).ThrowsAsync(failure);
        }

        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        A.CallTo(() => _metrics.RecordRetrievalDegradation(half)).MustHaveHappenedOnceExactly();
        var span = recorder.Spans.Should()
            .ContainSingle(s => Equals(s.GetTagItem("agentforge.retrieval.stage"), half),
                "the span's stage is the counter's stage label, so the two can be joined").Subject;
        span.GetTagItem("agentforge.outcome").Should().Be("degraded");
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
        Stage(recorder, "retrieval.rerank").GetTagItem("agentforge.outcome").Should().Be("reranked",
            "one degraded half still leaves candidates for the rest of the pipeline");
        recorder.ExportedStrings().Should().NotContain(s => s.Contains("Zelda", StringComparison.Ordinal),
            "an exception message is free text and may carry the query, so only its type reaches the trace");
    }

    [Fact]
    public async Task RetrieveAsync_RerankerThrows_MarksRerankDegradedUnderTheSameNameTheCounterUses()
    {
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("rerank failed"));
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        A.CallTo(() => _metrics.RecordRetrievalDegradation("rerank")).MustHaveHappenedOnceExactly();
        var rerank = Stage(recorder, "retrieval.rerank");
        rerank.GetTagItem("agentforge.retrieval.stage").Should().Be("rerank");
        rerank.GetTagItem("agentforge.outcome").Should().Be("degraded");
        rerank.Status.Should().Be(ActivityStatusCode.Error);
        rerank.GetTagItem("error.type").Should().Be(typeof(HttpRequestException).FullName);
    }

    [Fact]
    public async Task RetrieveAsync_RerankCancelled_EndsTheRerankSpanWithNoOutcomeAndNoErrorAndPropagates()
    {
        // The shape ARCHITECTURE-DOCUMENTS.md §10 documents: cancellation is not a degradation, so the stage span
        // ends with no outcome, Unset status and nothing counted. A separate change
        A.CallTo(() => _reranker.RerankAsync(A<string>._, A<IReadOnlyList<RerankDocument>>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException());
        using var recorder = SpanRecorder.Start();

        var act = () => CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var rerank = Stage(recorder, "retrieval.rerank");
        rerank.GetTagItem("agentforge.outcome").Should().BeNull();
        rerank.Status.Should().Be(ActivityStatusCode.Unset);
        rerank.GetTagItem("error.type").Should().BeNull();
        rerank.GetTagItem("agentforge.retrieval.candidate_count").Should().NotBeNull(
            "the count is set before the call, so a cancelled rerank still says how much it was handed");
        A.CallTo(() => _metrics.RecordRetrievalDegradation(A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RetrieveAsync_WithCandidates_NoSpanCarriesTheQueryOrSnippetText()
    {
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        string[] phi = [Query, "Zelda", "Quimby", "5.93", SnippetText];
        recorder.Spans.Should().NotBeEmpty();
        recorder.ExportedStrings().Should().NotContain(
            s => phi.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "ARCHITECTURE-DOCUMENTS.md §12: the question is patient-specific free text, and so may a guideline chunk be quoted back");
    }

    [Fact]
    public async Task RetrieveAsync_EveryAttributeKeyIsOneOfTheBoundedPhiFreeSet()
    {
        A.CallTo(() => _dense.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("dense down"));
        using var recorder = SpanRecorder.Start();

        await CreateSut().RetrieveAsync(Query, 5, CancellationToken.None);

        recorder.Spans.Should().NotBeEmpty();
        recorder.TagKeys().Distinct().Should().BeSubsetOf(
        [
            "agentforge.retrieval.stage", "agentforge.outcome", "agentforge.retrieval.candidate_count",
            "agentforge.retrieval.skip_reason", "error.type",
        ]);
    }
}
