using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.TestSupport;

/// <summary>
/// Captures every formatted log line written through a real <see cref="ILoggerFactory"/>, with the
/// factory's shared scope state appended the way a scope-aware formatter renders it.
/// <see cref="CapturingLogger{T}"/> owns a scope provider per logger, so it cannot show a scope
/// opened on one category reaching a line written on another - which is precisely what the
/// correlation-id scope has to do (FR-OBS-1, CONVENTIONS.md §7).
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopeProvider;

    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            owner._scopeProvider?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            List<string> scopeParts = [];
            owner._scopeProvider?.ForEachScope(
                (scope, parts) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                    {
                        parts.AddRange(pairs.Select(kv => $"{kv.Key}={kv.Value}"));
                    }
                },
                scopeParts);

            owner.Lines.Enqueue(scopeParts.Count > 0 ? $"{message} {string.Join(" ", scopeParts)}" : message);
        }
    }
}
