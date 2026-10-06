namespace AgentForge.Api.Chat;

/// <summary>Charges LLM turns against a per-session budget (<see cref="ConversationBudgetOptions"/>).</summary>
public interface IConversationTurnBudget
{
    /// <summary>
    /// Charges one LLM turn to <paramref name="sessionId"/>. Returns <see langword="false"/>, charging nothing,
    /// once the session has used its whole budget for the current window.
    /// </summary>
    bool TryConsume(string sessionId);
}
