using AgentForge.Observability;

namespace AgentForge.Evals;

/// <summary>Swallows the meter. The gate scores behavior, and a live metrics listener is not part of it.
/// Shared by the authorization and answer-path harnesses.</summary>
internal sealed class NoOpMetrics : IAgentForgeMetrics
{
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
    public void RecordRetrievalDegradation(string stage)
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
