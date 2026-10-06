using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Chat;

/// <summary>
/// Source-generated log messages for <see cref="ChatHub"/> (CA1848). Never a clinical value or
/// the message payload itself - message kind and delivery outcome only (CONVENTIONS.md §7).
/// </summary>
internal static partial class ChatHubLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to deliver chat message kind={Kind} sequence={Sequence} to connection {ConnectionId} - " +
            "it remains in the outbox for the next Resume call")]
    public static partial void MessageDeliveryFailed(ILogger logger, string kind, long sequence, string connectionId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Chat hub connection {ConnectionId} arrived with no session resolved by ChatHubSessionMiddleware - " +
            "it must run after UseSession(); the connection is refused as unauthenticated")]
    public static partial void SessionNotResolved(ILogger logger, string connectionId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Chat turn refused for conversation {ConversationId} - its session has used its whole LLM turn budget " +
            "(ConversationBudget:MaxTurnsPerWindow); no LLM call was made")]
    public static partial void TurnLimitReached(ILogger logger, string conversationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Chat hub connection {ConnectionId} presented a page key that does not match its session's current " +
            "patient - the page was rendered for another patient, or presented none; every method on it is refused")]
    public static partial void PagePatientMismatch(ILogger logger, string connectionId);
}
