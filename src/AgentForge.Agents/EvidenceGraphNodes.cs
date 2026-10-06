namespace AgentForge.Agents;

/// <summary>
/// The nodes of the Week 2 evidence graph (ARCHITECTURE-DOCUMENTS.md §6) - the only values a
/// <see cref="HandoffEvent"/> may carry in <see cref="HandoffEvent.From"/> and <see cref="HandoffEvent.To"/>.
/// They are published as an enumeration in the graph contract, and they are also metric and span labels, so
/// adding, renaming or removing one is a MAJOR contract change (ARCHITECTURE-DOCUMENTS.md §9).
/// </summary>
public static class EvidenceGraphNodes
{
    /// <summary>The router every run starts and ends at.</summary>
    public const string Supervisor = "supervisor";

    /// <summary>Extracts a document attached this turn.</summary>
    public const string IntakeExtractor = "intake-extractor";

    /// <summary>Hybrid guideline retrieval.</summary>
    public const string EvidenceRetriever = "evidence-retriever";

    /// <summary>Drafts the cited answer.</summary>
    public const string AnswerComposer = "answer-composer";

    /// <summary>The verification gate, reused as a node.</summary>
    public const string Critic = "critic";

    /// <summary>Every node, in graph order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Supervisor, IntakeExtractor, EvidenceRetriever, AnswerComposer, Critic];
}
