using AgentForge.Llm;

namespace AgentForge.Agent;

/// <summary>
/// One patient's conversation history. Site and PatientId are fixed for the life of the state -
/// set once from the authenticated launch context and never changed turn to turn - so every turn
/// run on a state stays with its patient (FR-CHAT-3). Which state a turn runs on is not structural:
/// the session that holds it outlives a patient switch, so the caller re-checks both against the
/// current launch context before resuming one (ChatSessionCoordinator). A separate change
/// </summary>
/// <param name="Site">OpenEMR multi-site segment for this session.</param>
/// <param name="PatientId">The one patient this entire session is scoped to.</param>
/// <param name="Messages">Conversation history so far, oldest first.</param>
public sealed record ConversationState(string Site, string PatientId, IReadOnlyList<LlmMessage> Messages)
{
    /// <summary>A fresh session for a patient, with no history yet.</summary>
    public static ConversationState Start(string site, string patientId) => new(site, patientId, []);
}
