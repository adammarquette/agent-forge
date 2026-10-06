using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Observability;

/// <summary>
/// The logging scope provider every logger provider reads scopes from. It scrubs a scope's <c>RequestPath</c> value
/// the way <see cref="SpanPhiScrubber"/> scrubs a span's path - identifier segments read <c>{id}</c> - and passes every
/// other scope, and the scope's other values, through unchanged. ASP.NET Core's hosting scope puts the request path
/// on every record logged inside a request, and <c>/evidence/document/{documentId}</c> carries a document id in it,
/// so with <c>IncludeScopes</c> the OpenTelemetry exporters wrote that id to stdout and Loki on every fetch.
/// </summary>
public sealed class RequestPathScrubbingScopeProvider : IExternalScopeProvider
{
    private const string RequestPathKey = "RequestPath";

    private readonly IExternalScopeProvider _inner;

    /// <summary>Wraps the provider that holds the scope stack.</summary>
    public RequestPathScrubbingScopeProvider(IExternalScopeProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state) => _inner.ForEachScope(callback, state);

    /// <inheritdoc />
    public IDisposable Push(object? state) => _inner.Push(Scrub(state));

    private static object? Scrub(object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values
            || !values.Any(pair => pair.Key == RequestPathKey))
        {
            return state;
        }

        return new ScrubbedScope([.. values.Select(pair => pair.Key == RequestPathKey
            ? new KeyValuePair<string, object?>(pair.Key, SpanPhiScrubber.ScrubPath(pair.Value?.ToString() ?? string.Empty))
            : pair)]);
    }

    private sealed class ScrubbedScope(IReadOnlyList<KeyValuePair<string, object?>> values)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => values.Count;

        public KeyValuePair<string, object?> this[int index] => values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join(' ', values.Select(pair => $"{pair.Key}:{pair.Value}"));
    }
}

/// <summary>Registers <see cref="RequestPathScrubbingScopeProvider"/> as the logger factory's scope provider.</summary>
public static class RequestPathScopeScrubbing
{
    /// <summary>
    /// Makes the logger factory hand every provider a <see cref="RequestPathScrubbingScopeProvider"/>. It wraps the
    /// framework's own scope provider, so <see cref="LoggerFactoryOptions.ActivityTrackingOptions"/> still apply.
    /// </summary>
    public static ILoggingBuilder ScrubRequestPathFromLogScopes(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.Services.AddSingleton<IExternalScopeProvider>(services => new RequestPathScrubbingScopeProvider(
            FrameworkScopeProvider(services.GetRequiredService<IOptions<LoggerFactoryOptions>>().Value)));
        return logging;
    }

    // The framework's provider is internal; a factory hands it to any ISupportExternalScope provider it is given.
    private static IExternalScopeProvider FrameworkScopeProvider(LoggerFactoryOptions options)
    {
        var capture = new ScopeProviderCapture();
        using (LoggerFactory.Create(logging => logging
            .AddProvider(capture)
            .Configure(factory => factory.ActivityTrackingOptions = options.ActivityTrackingOptions)))
        {
            return capture.ScopeProvider ?? new LoggerExternalScopeProvider();
        }
    }

    private sealed class ScopeProviderCapture : ILoggerProvider, ISupportExternalScope
    {
        public IExternalScopeProvider? ScopeProvider { get; private set; }

        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

        public void Dispose()
        {
        }
    }
}
