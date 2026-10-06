using System.Diagnostics;
using AgentForge.Agents;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Retrieval;

/// <summary>
/// Hybrid evidence retriever (ARCHITECTURE-DOCUMENTS.md §5, W2-D7): runs the sparse (Postgres FTS) and dense
/// (pgvector) halves, merges their candidate lists with Reciprocal Rank Fusion, then reranks the fused pool so
/// only the top grounded snippets reach the answer model. Degrades deterministically — a failing half or a
/// failing reranker is logged and the pipeline continues (sparse-only, or the fused order), never throwing
/// (§10). The two halves and the reranker are provider-agnostic seams; the Cohere/embedding wiring lives in
/// their implementations, not here.
/// </summary>
public sealed class HybridEvidenceRetriever : IEvidenceRetriever
{
    // Fan out wider than topK before rerank so the cross-encoder has real alternatives to reorder, not just
    // the final K. 4× is the "small corpus, basic but reliable" bar (§5); tune once baselines exist.
    private const int CandidatePoolMultiplier = 4;

    // Stage literals shared by the span and the retrieval_degradations counter, so the two join on one name.
    private const string FusionStage = "fusion";
    private const string RerankStage = "rerank";

    private readonly ISparseRetriever _sparse;
    private readonly IDenseRetriever _dense;
    private readonly IReranker _reranker;
    private readonly IAgentForgeMetrics _metrics;
    private readonly ILogger<HybridEvidenceRetriever> _logger;

    /// <summary>Creates the hybrid retriever over its two halves and the reranker.</summary>
    public HybridEvidenceRetriever(
        ISparseRetriever sparse, IDenseRetriever dense, IReranker reranker,
        IAgentForgeMetrics metrics, ILogger<HybridEvidenceRetriever> logger)
    {
        _sparse = sparse;
        _dense = dense;
        _reranker = reranker;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvidenceSnippet>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken)
    {
        var poolSize = Math.Max(topK, topK * CandidatePoolMultiplier);

        var sparse = await SafeRetrieveAsync("sparse", ct => _sparse.RetrieveAsync(query, poolSize, ct), cancellationToken).ConfigureAwait(false);
        var dense = await SafeRetrieveAsync("dense", ct => _dense.RetrieveAsync(query, poolSize, ct), cancellationToken).ConfigureAwait(false);

        // Hydrate one snippet body per chunk id (first occurrence wins); RRF below decides the ranking, so it
        // doesn't matter which half a body came from.
        var byId = new Dictionary<string, EvidenceSnippet>(StringComparer.Ordinal);
        foreach (var snippet in sparse.Concat(dense))
        {
            byId.TryAdd(snippet.ChunkId, snippet);
        }

        if (byId.Count == 0)
        {
            // Both stages still get a span, so an empty corpus reads "skipped" in the waterfall, never "fast".
            RecordSkipped(FusionStage);
            RecordSkipped(RerankStage);
            return [];
        }

        IReadOnlyList<EvidenceSnippet> fusedSnippets;
        using (var span = StartStage(FusionStage))
        {
            var fused = ReciprocalRankFusion.Fuse(
            [
                [.. sparse.Select(s => s.ChunkId)],
                [.. dense.Select(s => s.ChunkId)],
            ]);

            fusedSnippets = [.. fused.Select(candidate => byId[candidate.Id] with { Score = candidate.Score })];
            span?.SetTag(EvidenceTracing.Outcome, "fused");
            span?.SetTag(EvidenceTracing.CandidateCount, fusedSnippets.Count);
        }

        return await RerankAsync(query, fusedSnippets, topK, cancellationToken).ConfigureAwait(false);
    }

    // One child span per stage, under the caller's span (the evidence-retriever worker span in the graph).
    // Stage literals only, never the query or a snippet (ARCHITECTURE-DOCUMENTS.md §12). A separate change
    private static Activity? StartStage(string stage)
    {
        var span = AgentForgeActivitySource.Instance.StartActivity(EvidenceTracing.RetrievalStageSpanPrefix + stage);
        span?.SetTag(EvidenceTracing.RetrievalStage, stage);
        return span;
    }

    private static void RecordSkipped(string stage)
    {
        using var span = StartStage(stage);
        span?.SetTag(EvidenceTracing.Outcome, "skipped");
        span?.SetTag(EvidenceTracing.SkipReason, "no_candidates");
        span?.SetTag(EvidenceTracing.CandidateCount, 0);
    }

    private static void RecordDegraded(Activity? span, Exception exception)
    {
        span?.SetTag(EvidenceTracing.Outcome, "degraded");
        EvidenceTracing.RecordFailure(span, exception);
    }

    private async Task<IReadOnlyList<EvidenceSnippet>> RerankAsync(
        string query, IReadOnlyList<EvidenceSnippet> candidates, int topK, CancellationToken cancellationToken)
    {
        using var span = StartStage(RerankStage);
        span?.SetTag(EvidenceTracing.CandidateCount, candidates.Count);
        try
        {
            var documents = candidates.Select(s => new RerankDocument(s.ChunkId, s.Text)).ToList();
            var rerankStart = Stopwatch.GetTimestamp();
            var ranked = await _reranker.RerankAsync(query, documents, topK, cancellationToken).ConfigureAwait(false);
            _metrics.RecordRerankLatency(Stopwatch.GetElapsedTime(rerankStart));
            if (ranked.Count == 0)
            {
                // Ran and returned no ranking (the disabled reranker does this): the fused order stands.
                span?.SetTag(EvidenceTracing.Outcome, "unranked");
                return [.. candidates.Take(topK)];
            }

            span?.SetTag(EvidenceTracing.Outcome, "reranked");
            var byId = candidates.ToDictionary(s => s.ChunkId, StringComparer.Ordinal);
            return
            [
                .. ranked
                    .Where(r => byId.ContainsKey(r.Id))
                    .Select(r => byId[r.Id] with { Score = r.Score })
                    .Take(topK)
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RetrievalLog.RerankDegraded(_logger, ex);
            _metrics.RecordRetrievalDegradation(RerankStage);
            RecordDegraded(span, ex);
            return [.. candidates.Take(topK)];
        }
    }

    private async Task<IReadOnlyList<EvidenceSnippet>> SafeRetrieveAsync(
        string half, Func<CancellationToken, Task<IReadOnlyList<EvidenceSnippet>>> retrieve, CancellationToken cancellationToken)
    {
        using var span = StartStage(half);
        try
        {
            var snippets = await retrieve(cancellationToken).ConfigureAwait(false);
            span?.SetTag(EvidenceTracing.Outcome, "retrieved");
            span?.SetTag(EvidenceTracing.CandidateCount, snippets.Count);
            return snippets;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RetrievalLog.HalfDegraded(_logger, half, ex);
            _metrics.RecordRetrievalDegradation(half);
            RecordDegraded(span, ex);
            return [];
        }
    }
}
