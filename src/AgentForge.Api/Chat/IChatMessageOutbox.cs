namespace AgentForge.Api.Chat;

/// <summary>
/// Records every message sent to a session so a reconnecting client can replay anything it missed
/// - the "no silent drops" half of CONVENTIONS.md §12. Keyed by the stable session id,
/// not the SignalR connection id, since resume must work across a reconnect that gets a new
/// connection id. The session id outlives a patient switch, so every message also carries the
/// <see cref="ChatPatientScope"/> it was delivered for, and replay returns only the requested one's.
/// </summary>
public interface IChatMessageOutbox
{
    /// <summary>Appends a new message for <paramref name="sessionId"/> about <paramref name="patient"/> and returns it, sequence assigned.</summary>
    ChatMessage Append(string sessionId, ChatPatientScope patient, string kind, string payloadJson);

    /// <summary>
    /// Returns every message for <paramref name="sessionId"/> delivered for <paramref name="patient"/> with a
    /// sequence greater than <paramref name="lastSeenSequence"/> - never one delivered for another site or patient.
    /// </summary>
    IReadOnlyList<ChatMessage> GetSince(string sessionId, ChatPatientScope patient, long lastSeenSequence);
}
