using Microsoft.Extensions.Options;

namespace AgentForge.Api.Chat;

/// <summary>
/// Single-instance in-memory <see cref="IConversationTurnBudget"/>, keyed by the same session id as
/// <see cref="Session.InMemoryConversationStateStore"/> and with the same single-instance assumption. Each session's
/// window starts at its first charge; entries whose window has ended are swept, so memory holds only sessions
/// that spent within the last window.
/// </summary>
public sealed class InMemoryConversationTurnBudget(IOptions<ConversationBudgetOptions> options, TimeProvider timeProvider)
    : IConversationTurnBudget
{
    // Sweep cost is O(sessions), so amortise it rather than paying it on every turn.
    private const int SweepEveryCharges = 256;

    private readonly int _maxTurns = options.Value.MaxTurnsPerWindow;
    private readonly TimeSpan _window = options.Value.Window;
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Used)> _bySessionId = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _chargesSinceSweep;

    /// <summary>How many sessions are currently tracked. For tests of the eviction.</summary>
    internal int TrackedSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _bySessionId.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryConsume(string sessionId)
    {
        var now = timeProvider.GetUtcNow();

        // Check and increment under one lock: the agenda's parallel fan-out on one session must not over-grant.
        lock (_gate)
        {
            if (++_chargesSinceSweep >= SweepEveryCharges)
            {
                _chargesSinceSweep = 0;
                SweepExpired(now);
            }

            if (!_bySessionId.TryGetValue(sessionId, out var entry) || now - entry.WindowStart >= _window)
            {
                entry = (now, 0);
            }

            if (entry.Used >= _maxTurns)
            {
                return false;
            }

            _bySessionId[sessionId] = (entry.WindowStart, entry.Used + 1);
            return true;
        }
    }

    private void SweepExpired(DateTimeOffset now)
    {
        foreach (var (sessionId, entry) in _bySessionId)
        {
            if (now - entry.WindowStart >= _window)
            {
                _bySessionId.Remove(sessionId);
            }
        }
    }
}
