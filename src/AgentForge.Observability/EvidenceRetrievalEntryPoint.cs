namespace AgentForge.Observability;

/// <summary>
/// Which call site ran an evidence retrieval. Both reach the same <c>IEvidenceRetriever</c> and record into
/// the same instruments, so <c>NFR-SLO-W2-1</c>'s retrieval p95 covers the stage; this only says which path a
/// sample came from, so an operator can see which one is slow. A separate change
/// </summary>
/// <remarks>
/// <b>This becomes an exported metric label, so its cardinality is its contract.</b> Two values, both
/// compile-time constants, none derived from a request, a patient or a query. A third member multiplies every
/// bucket of the retrieval-duration histogram - add one only with that cost in hand.
/// </remarks>
public enum EvidenceRetrievalEntryPoint
{
    /// <summary><c>POST /evidence/ask</c>, through <c>EvidenceAgentSupervisor</c>.</summary>
    EvidenceAsk = 0,

    /// <summary>The chat agent's <c>retrieve_evidence</c> MCP tool, through <c>EvidenceTool</c>.</summary>
    ChatTool = 1,
}

/// <summary>Helpers for reporting an <see cref="EvidenceRetrievalEntryPoint"/> outside the C# type system.</summary>
public static class EvidenceRetrievalEntryPointExtensions
{
    /// <summary>
    /// The bounded token this entry point is reported under - the literal value of the exported
    /// <c>entry_point</c> label. Throws for an undeclared value rather than minting a new label value.
    /// </summary>
    public static string ToWireName(this EvidenceRetrievalEntryPoint entryPoint) => entryPoint switch
    {
        EvidenceRetrievalEntryPoint.EvidenceAsk => "evidence_ask",
        EvidenceRetrievalEntryPoint.ChatTool => "chat_tool",
        _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, "Unknown evidence retrieval entry point."),
    };
}
