using AgentForge.Observability;

namespace AgentForge.Evals.Evidence;

/// <summary>
/// Swallows the meter like <see cref="NoOpMetrics"/>, except for the one signal an evidence case has to
/// score: <see cref="RecordRetrievalDegradation"/>. "Degrade deterministically and say so" has two halves —
/// the surviving half's results still arrive, and the pipeline recorded that a stage dropped out — and a
/// retriever that quietly returned fewer results satisfies the first while failing the second. Nothing else
/// here is asserted; a live metrics listener is not part of what the gate scores.
/// </summary>
internal sealed class RecordingMetrics : IAgentForgeMetrics
{
    private readonly List<string> _degradedStages = [];

    /// <summary>Stages a degradation was recorded for, in the order the retriever recorded them.</summary>
    public IReadOnlyList<string> DegradedStages => _degradedStages;

    /// <inheritdoc />
    public void RecordRetrievalDegradation(string stage) => _degradedStages.Add(stage);

    /// <inheritdoc />
    public void RecordAgentTurn(AgentTurnType turnType, bool succeeded, TimeSpan duration)
    {
    }

    /// <inheritdoc />
    public void RecordToolCall(string toolName, bool succeeded, TimeSpan duration)
    {
    }

    /// <inheritdoc />
    public void RecordVerificationResult(bool passed)
    {
    }

    /// <inheritdoc />
    public void RecordAuthorizationDecision(bool permitted, string reason)
    {
    }

    /// <inheritdoc />
    public void RecordOutOfScopeToolCall()
    {
    }

    /// <inheritdoc />
    public void RecordExpiredSessionRefusal(string surface)
    {
    }

    /// <inheritdoc />
    public void RecordLlmUsage(int inputTokens, int outputTokens, decimal estimatedCostUsd)
    {
    }

    /// <inheritdoc />
    public void RecordDocumentIngestion(string outcome, TimeSpan duration)
    {
    }

    /// <inheritdoc />
    public void RecordWorkerLatency(string worker, TimeSpan duration)
    {
    }

    /// <inheritdoc />
    public void RecordRoutingDecision(string fromNode, string toNode)
    {
    }

    /// <inheritdoc />
    public void RecordEvidenceRetrieval(bool hit, int resultCount, TimeSpan duration, EvidenceRetrievalEntryPoint entryPoint)
    {
    }

    /// <inheritdoc />
    public void RecordRerankLatency(TimeSpan duration)
    {
    }

    /// <inheritdoc />
    public void RecordCitationQuoteMatch(string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordExtractionConfidence(string documentType, double confidence)
    {
    }

    /// <inheritdoc />
    public void RecordExtractionFieldOutcome(string field, string outcome)
    {
    }
}
