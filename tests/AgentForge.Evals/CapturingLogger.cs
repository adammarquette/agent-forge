using Microsoft.Extensions.Logging;

namespace AgentForge.Evals;

/// <summary>One captured log line: the logger category, the event it was written as, and the formatted
/// message. The category and event are kept because <c>no_phi_in_logs</c> excepts the access-audit trail's
/// <c>patient=</c> value by both, not by text any logger could imitate. Separate changes</summary>
internal sealed record CapturedLog(string Category, EventId EventId, string Message);

/// <summary>Captures formatted log messages so the no_phi_in_logs and attempt_logged rubrics can inspect
/// them. An authorization case spans several loggers (the dispatcher, the audit trail, the relationship
/// authorizer), so the sink can be shared: the rubric asks what the run logged, not which type logged it.</summary>
internal sealed class CapturingLogger<T>(List<CapturedLog>? sink = null) : ILogger<T>
{
    private static readonly string Category = typeof(T).FullName ?? typeof(T).Name;

    public List<CapturedLog> Messages { get; } = sink ?? [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        // The console sink prints the exception after the message, so the scan must see it too.
        Messages.Add(new CapturedLog(
            Category, eventId, exception is null ? formatter(state, exception) : $"{formatter(state, exception)}\n{exception}"));
}
