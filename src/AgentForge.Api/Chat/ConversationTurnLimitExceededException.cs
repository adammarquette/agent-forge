namespace AgentForge.Api.Chat;

/// <summary>
/// Thrown by <see cref="ChatSessionCoordinator"/> before a turn runs when the session has used its whole
/// <see cref="ConversationBudgetOptions.MaxTurnsPerWindow"/> budget for the window; no LLM call has been made.
/// </summary>
public sealed class ConversationTurnLimitExceededException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public ConversationTurnLimitExceededException()
        : base("This session has used its whole LLM turn budget.")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ConversationTurnLimitExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public ConversationTurnLimitExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
