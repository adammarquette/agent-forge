using System.Diagnostics;
using AgentForge.Agents;
using AgentForge.Observability;

namespace AgentForge.Agent;

/// <summary>
/// Default <see cref="IEvidenceTool"/>: runs hybrid retrieval over the guideline corpus and projects each
/// snippet to a citable <c>[Guideline/&lt;chunkId&gt;]</c> record, so the model can ground and cite an answer in
/// the corpus (FR-RAG-2). The retrieval itself (dense + sparse + rerank) and its degradation live
/// in <see cref="IEvidenceRetriever"/>; this tool only adapts the snippets to the citation shape, and records the
/// retrieval into the same series <c>POST /evidence/ask</c> does, tagged as the chat path.
/// </summary>
public sealed class EvidenceTool(IEvidenceRetriever retriever, IAgentForgeMetrics metrics) : IEvidenceTool
{
    private const int DefaultTopK = 5;

    /// <inheritdoc />
    public async Task<EvidenceResult> GetAsync(string query, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var snippets = await retriever.RetrieveAsync(query, DefaultTopK, cancellationToken).ConfigureAwait(false);
        metrics.RecordEvidenceRetrieval(
            snippets.Count > 0, snippets.Count, Stopwatch.GetElapsedTime(start), EvidenceRetrievalEntryPoint.ChatTool);
        var records = snippets
            .Select(snippet => new EvidenceRecord("Guideline", snippet.ChunkId, snippet.DocumentId, snippet.Section, snippet.Text))
            .ToList();

        return new EvidenceResult(records);
    }
}
