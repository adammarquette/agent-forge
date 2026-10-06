using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Single-instance in-memory <see cref="IAgendaSummaryCache"/>, with the same single-instance assumption as the
/// turn budget it saves. Entries expire <see cref="AgendaOptions.SummaryCacheTtl"/> after they are stored; at
/// <see cref="AgendaOptions.MaxCachedSummaries"/> a store first drops expired entries, then the soonest to
/// expire. It logs nothing: its keys and values are PHI. A restart empties it.
/// </summary>
public sealed class InMemoryAgendaSummaryCache(IOptions<AgendaOptions> options, TimeProvider timeProvider)
    : IAgendaSummaryCache
{
    private readonly TimeSpan _ttl = options.Value.SummaryCacheTtl;
    private readonly int _maxEntries = options.Value.MaxCachedSummaries;
    private readonly Dictionary<AgendaSummaryCacheKey, (AgendaCachedSummary Summary, DateTimeOffset ExpiresAt)> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>How many entries are held, expired or not. For tests of the size bound.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(AgendaSummaryCacheKey key, [NotNullWhen(true)] out AgendaCachedSummary? summary)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (now < entry.ExpiresAt)
                {
                    summary = entry.Summary;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        summary = null;
        return false;
    }

    /// <inheritdoc />
    public void Store(AgendaSummaryCacheKey key, AgendaCachedSummary summary)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= _maxEntries)
            {
                MakeRoom(now);
            }

            _entries[key] = (summary, now + _ttl);
        }
    }

    // O(n) at the bound only; n is MaxCachedSummaries, and a clinic day stores a few dozen per clinician.
    private void MakeRoom(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (now >= entry.ExpiresAt)
            {
                _entries.Remove(key);
            }
        }

        while (_entries.Count >= _maxEntries)
        {
            var soonest = _entries.MinBy(e => e.Value.ExpiresAt).Key;
            _entries.Remove(soonest);
        }
    }
}
