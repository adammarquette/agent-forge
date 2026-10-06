namespace AgentForge.Retrieval.Cohere;

/// <summary>
/// No-op embedding provider registered when no Cohere key is configured: returns no vectors, so the dense
/// retriever produces nothing and the hybrid retriever runs sparse-only. Not a degradation (§10): nothing fails
/// and nothing is counted. Keeps the app bootable and the pipeline honest without a key.
/// </summary>
internal sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, EmbeddingInputType inputType, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float[]>>([]);
}

/// <summary>
/// No-op reranker registered when no Cohere key is configured: returns nothing, so the hybrid retriever keeps
/// the RRF-fused order and the rerank span reads <c>unranked</c> (§10), not <c>degraded</c>.
/// </summary>
internal sealed class DisabledReranker : IReranker
{
    public Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query, IReadOnlyList<RerankDocument> documents, int topK, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RerankedCandidate>>([]);
}
