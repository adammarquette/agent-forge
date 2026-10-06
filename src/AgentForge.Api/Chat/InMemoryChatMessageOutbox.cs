using System.Collections.Concurrent;

namespace AgentForge.Api.Chat;

/// <summary>
/// Single-instance in-memory <see cref="IChatMessageOutbox"/>, matching the sidecar's
/// single-instance deployment assumption (the same trade-off as <c>InMemoryConversationStateStore</c>).
/// Bounds each session's backlog: a reconnect this far behind is treated as an abandoned session,
/// not a brief drop, so the oldest messages are evicted rather than growing unbounded. The bound is per
/// session, across every patient that session has shown.
/// </summary>
public sealed class InMemoryChatMessageOutbox : IChatMessageOutbox
{
    private const int MaxMessagesPerSession = 50;

    private readonly ConcurrentDictionary<string, List<Entry>> _bySessionId = new();
    private long _sequence;

    /// <inheritdoc />
    public ChatMessage Append(string sessionId, ChatPatientScope patient, string kind, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(patient);

        var message = new ChatMessage(Interlocked.Increment(ref _sequence), kind, payloadJson);
        var messages = _bySessionId.GetOrAdd(sessionId, static _ => []);

        lock (messages)
        {
            messages.Add(new Entry(patient, message));
            if (messages.Count > MaxMessagesPerSession)
            {
                messages.RemoveAt(0);
            }
        }

        return message;
    }

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> GetSince(string sessionId, ChatPatientScope patient, long lastSeenSequence)
    {
        ArgumentNullException.ThrowIfNull(patient);

        if (!_bySessionId.TryGetValue(sessionId, out var messages))
        {
            return [];
        }

        lock (messages)
        {
            return [.. messages.Where(e => e.Patient == patient && e.Message.Sequence > lastSeenSequence).Select(e => e.Message)];
        }
    }

    private sealed record Entry(ChatPatientScope Patient, ChatMessage Message);
}
