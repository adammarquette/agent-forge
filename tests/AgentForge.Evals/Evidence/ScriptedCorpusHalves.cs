using AgentForge.Agents;
using AgentForge.Retrieval;

namespace AgentForge.Evals.Evidence;

/// <summary>
/// The pinned sparse (FTS) and dense (pgvector) halves of hybrid retrieval, and the pinned reranker, for
/// one evidence case. Each replays the ranking the case wrote down — or throws, when the case names it in
/// <c>failing_stages</c> — so what is left under test is <see cref="HybridEvidenceRetriever"/> itself:
/// the RRF merge, the rerank ordering and the deterministic degradation path.
/// </summary>
/// <remarks>
/// A ranking naming a chunk id the corpus does not hold throws rather than being skipped. A fixture typo
/// would otherwise quietly shrink the candidate pool, and a case that retrieves less than it meant to is a
/// weaker case that still passes — the shape this slice exists to stop.
/// </remarks>
internal static class ScriptedCorpus
{
    public const string SparseStage = "sparse";
    public const string DenseStage = "dense";
    public const string RerankStage = "rerank";

    /// <summary>Indexes a case's corpus by chunk id, refusing a corpus that repeats one.</summary>
    public static IReadOnlyDictionary<string, EvidenceSnippet> Index(EvidenceScenario scenario, string caseId)
    {
        var index = new Dictionary<string, EvidenceSnippet>(StringComparer.Ordinal);
        foreach (var chunk in scenario.Corpus)
        {
            if (!index.TryAdd(chunk.ChunkId, new EvidenceSnippet
            {
                ChunkId = chunk.ChunkId,
                DocumentId = chunk.DocumentId,
                Section = chunk.Section,
                Text = chunk.Text,
            }))
            {
                throw new InvalidOperationException(
                    $"Evidence case '{caseId}' seeds chunk '{chunk.ChunkId}' twice - two chunks sharing an id " +
                    "would collide in the hybrid retriever's hydration map and one would never be scored.");
            }
        }

        return index;
    }

    public static IReadOnlyList<EvidenceSnippet> Resolve(
        IReadOnlyList<string> ranking, IReadOnlyDictionary<string, EvidenceSnippet> corpus,
        int topK, string caseId, string stage) =>
        [.. ranking.Take(topK).Select(id => corpus.TryGetValue(id, out var snippet)
            ? snippet
            : throw new InvalidOperationException(
                $"Evidence case '{caseId}' has the {stage} half ranking '{id}', which its corpus does not hold."))];

    public static Exception StageFailure(string stage, string caseId) => new InvalidOperationException(
        $"Synthetic {stage} retrieval failure for evidence case '{caseId}': the stage is unavailable and its "
        + "own retries are exhausted.");
}

/// <summary>The sparse half, replayed from the case.</summary>
internal sealed class ScriptedSparseRetriever(
    EvidenceScenario scenario, IReadOnlyDictionary<string, EvidenceSnippet> corpus, string caseId) : ISparseRetriever
{
    /// <inheritdoc />
    public Task<IReadOnlyList<EvidenceSnippet>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken) =>
        scenario.FailingStages.Contains(ScriptedCorpus.SparseStage, StringComparer.Ordinal)
            ? Task.FromException<IReadOnlyList<EvidenceSnippet>>(
                ScriptedCorpus.StageFailure(ScriptedCorpus.SparseStage, caseId))
            : Task.FromResult(ScriptedCorpus.Resolve(
                scenario.SparseRanking, corpus, topK, caseId, ScriptedCorpus.SparseStage));
}

/// <summary>The dense half, replayed from the case.</summary>
internal sealed class ScriptedDenseRetriever(
    EvidenceScenario scenario, IReadOnlyDictionary<string, EvidenceSnippet> corpus, string caseId) : IDenseRetriever
{
    /// <inheritdoc />
    public Task<IReadOnlyList<EvidenceSnippet>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken) =>
        scenario.FailingStages.Contains(ScriptedCorpus.DenseStage, StringComparer.Ordinal)
            ? Task.FromException<IReadOnlyList<EvidenceSnippet>>(
                ScriptedCorpus.StageFailure(ScriptedCorpus.DenseStage, caseId))
            : Task.FromResult(ScriptedCorpus.Resolve(
                scenario.DenseRanking, corpus, topK, caseId, ScriptedCorpus.DenseStage));
}

/// <summary>
/// The cross-encoder, replayed from the case. Scores descend with position so the hybrid retriever's own
/// ordering is the one under test; a pinned id the fused pool does not hold throws, because a reranker that
/// promoted a chunk neither half retrieved is not a reordering, and silently dropping it would hide the
/// fixture error behind a passing case.
/// </summary>
internal sealed class ScriptedReranker(EvidenceScenario scenario, string caseId) : IReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query, IReadOnlyList<RerankDocument> documents, int topK, CancellationToken cancellationToken)
    {
        if (scenario.FailingStages.Contains(ScriptedCorpus.RerankStage, StringComparer.Ordinal))
        {
            return Task.FromException<IReadOnlyList<RerankedCandidate>>(
                ScriptedCorpus.StageFailure(ScriptedCorpus.RerankStage, caseId));
        }

        if (scenario.RerankRanking is not { } ranking)
        {
            return Task.FromResult<IReadOnlyList<RerankedCandidate>>([]);
        }

        var pool = documents.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var ranked = ranking.Select((id, position) => pool.Contains(id)
            ? new RerankedCandidate(id, 1.0 - (position * 0.01))
            : throw new InvalidOperationException(
                $"Evidence case '{caseId}' has the reranker returning '{id}', which neither half retrieved - "
                + "the reranker reorders the fused pool, it does not add to it.")).ToArray();

        // Honouring topK hides the retriever's own cap on this path; a case opts out to reach it.
        return Task.FromResult<IReadOnlyList<RerankedCandidate>>(
            scenario.RerankIgnoresTopK ? ranked : [.. ranked.Take(topK)]);
    }
}
