using AgentForge.Agents;
using AgentForge.Retrieval;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// The sparse half of retrieval over a three-chunk in-memory corpus, standing in for Postgres full-text
/// search (whose ranking is SQL and cannot run without the database). Everything above it - the real
/// <see cref="HybridEvidenceRetriever"/>, its fusion and its rerank stage - is the production code. Scores by
/// shared words of four letters or more, so the question selects the chunk about what it asks.
/// </summary>
internal sealed class InMemoryGuidelineCorpus(IReadOnlyList<EvidenceSnippet> chunks) : ISparseRetriever
{
    /// <summary>The chunk the fixture question should retrieve.</summary>
    public static EvidenceSnippet PotassiumChunk { get; } = new()
    {
        DocumentId = "synthetic-hf-guideline-fixture",
        Section = "MRA monitoring",
        ChunkId = "hf-mra-potassium-1",
        Text = "Reassess mineralocorticoid receptor antagonist therapy such as spironolactone when serum potassium exceeds 5.5 mmol/L.",
    };

    /// <summary>The fixture corpus: the potassium chunk and two that the question must not pull in.</summary>
    public static IReadOnlyList<EvidenceSnippet> Fixture { get; } =
    [
        PotassiumChunk,
        new()
        {
            DocumentId = "synthetic-af-guideline-fixture",
            Section = "Warfarin monitoring",
            ChunkId = "af-warfarin-inr-1",
            Text = "Target an INR of 2.0-3.0 for most patients taking warfarin for atrial fibrillation.",
        },
        new()
        {
            DocumentId = "synthetic-lipid-guideline-fixture",
            Section = "Statin therapy",
            ChunkId = "lipid-statin-1",
            Text = "High-intensity statin therapy is recommended after an acute coronary syndrome.",
        },
    ];

    public Task<IReadOnlyList<EvidenceSnippet>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken)
    {
        var terms = Words(query);
        IReadOnlyList<EvidenceSnippet> ranked =
        [
            .. chunks
                .Select(c => (Chunk: c, Score: Words(c.Text).Count(terms.Contains)))
                .Where(s => s.Score > 0)
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Chunk.ChunkId, StringComparer.Ordinal)
                .Take(topK)
                .Select(s => s.Chunk with { Score = s.Score }),
        ];
        return Task.FromResult(ranked);
    }

    private static HashSet<string> Words(string text) =>
        [.. text.Split((char[])[' ', ',', '.', '?', '\'', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())];
}
