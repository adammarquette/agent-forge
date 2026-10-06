using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// Records everything any logger in the composition emits at any level - the formatted message, every
/// structured value, every scope value and the exception text - because a PHI leak can ride in any of them,
/// and an exporter ships all four. Nothing is filtered by category: EF Core's own logs count too.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Every captured string, in emission order.</summary>
    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Record(state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(formatter(state, exception));
            Record(state);
            if (exception is not null)
            {
                entries.Enqueue(exception.ToString());
            }
        }

        private void Record<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (_, value) in values)
                {
                    entries.Enqueue(value?.ToString() ?? string.Empty);
                }
            }
            else
            {
                entries.Enqueue(state?.ToString() ?? string.Empty);
            }
        }
    }
}
