namespace AgentForge.Api.Health;

/// <summary>
/// Whether this build's migrations (<see cref="IDataStoreStartupWork.MigrateAsync"/>) have completed in this
/// process, and how they have failed so far. Written by <see cref="DataStoreStartupService"/>, read by
/// <see cref="VectorIndexHealthCheck"/>, so <c>/ready</c> stays 503 until they have run - the guarantee the old
/// inline call gave by not serving at all.
/// </summary>
public sealed class DataStoreStartupState
{
    private readonly object _gate = new();
    private bool _complete;
    private int _failedAttempts;
    private string? _lastFailure;

    /// <summary>True once the migrations have run to completion.</summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _complete;
            }
        }
    }

    /// <summary>Migration attempts that threw so far.</summary>
    public int FailedAttempts
    {
        get
        {
            lock (_gate)
            {
                return _failedAttempts;
            }
        }
    }

    /// <summary>The type name of the last failure - never its message, which can carry the host and port.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>Records one failed migration attempt.</summary>
    public void RecordFailure(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _failedAttempts++;
            _lastFailure = failure.GetType().Name;
        }
    }

    /// <summary>Records that the migrations completed.</summary>
    public void MarkComplete()
    {
        lock (_gate)
        {
            _complete = true;
        }
    }

    /// <summary>The sentence readiness appends while the work is still pending.</summary>
    public string DescribePending()
    {
        lock (_gate)
        {
            return _failedAttempts == 0
                ? "This build's startup migrations have not completed yet."
                : $"This build's startup migrations have not completed: {_failedAttempts} failed attempt(s), last {_lastFailure}; retrying with backoff.";
        }
    }
}
