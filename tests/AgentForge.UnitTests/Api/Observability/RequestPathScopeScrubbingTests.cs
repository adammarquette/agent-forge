using System.Diagnostics;
using AgentForge.Api.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The ASP.NET Core hosting scope carries the request path onto every record logged inside a request, and
/// <c>/evidence/document/{documentId}</c> puts a document id there. Failure mode guarded (regression): an
/// identifier in the request path reaching an exporter through the scope. <see cref="HostStdoutPhiScanTests"/> pins
/// it through the real host. A separate change
/// </summary>
public sealed class RequestPathScopeScrubbingTests
{
    private const string DocumentId = "scrub-binary-6c1e";

    [Fact]
    public void Push_WhenAScopeCarriesARequestPathWithAnId_ExposesThePathWithTheIdTemplated()
    {
        var provider = new RequestPathScrubbingScopeProvider(new LoggerExternalScopeProvider());

        using (provider.Push(HostingLikeScope($"/evidence/document/{DocumentId}")))
        {
            var scope = Scopes(provider).Should().ContainSingle().Subject;

            scope.Should().BeAssignableTo<IEnumerable<KeyValuePair<string, object?>>>().Which.Should().BeEquivalentTo(
                new Dictionary<string, object?> { ["RequestId"] = "0HN-scrub:00000001", ["RequestPath"] = "/evidence/document/{id}" });
            scope.ToString().Should().NotContain(DocumentId);
        }
    }

    [Fact]
    public void Push_WhenAScopeHasNoRequestPath_PassesItThroughUnchanged()
    {
        var provider = new RequestPathScrubbingScopeProvider(new LoggerExternalScopeProvider());
        var correlation = new Dictionary<string, object?> { ["CorrelationId"] = "c-scrub-1" };

        using (provider.Push(correlation))
        {
            Scopes(provider).Should().ContainSingle().Which.Should().BeSameAs(correlation);
        }
    }

    [Fact]
    public void Push_WhenTheScopeIsDisposed_RemovesIt()
    {
        var provider = new RequestPathScrubbingScopeProvider(new LoggerExternalScopeProvider());

        provider.Push(HostingLikeScope($"/evidence/document/{DocumentId}")).Dispose();

        Scopes(provider).Should().BeEmpty();
    }

    [Fact]
    public void ScrubRequestPathFromLogScopes_WhenWiredIntoLogging_ScrubsThePathAndKeepsTheActivityScope()
    {
        // Every provider that reads scopes gets them from the factory, so the capture sees what an exporter sees.
        var capture = new ScopeCapture();
        using var services = new ServiceCollection()
            .AddLogging(logging => logging
                .AddProvider(capture)
                .Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId)
                .ScrubRequestPathFromLogScopes())
            .BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentForge.Scrub");
        using var activity = new Activity("scrub-test").SetIdFormat(ActivityIdFormat.W3C).Start();

        using (logger.BeginScope(HostingLikeScope($"/evidence/document/{DocumentId}")))
        {
            var values = Scopes(capture.ScopeProvider!)
                .OfType<IEnumerable<KeyValuePair<string, object?>>>()
                .SelectMany(scope => scope)
                .ToDictionary(pair => pair.Key, pair => pair.Value?.ToString());

            values.Should().Contain("RequestPath", "/evidence/document/{id}");
            values.Should().Contain("TraceId", activity.TraceId.ToHexString(), "the framework's activity scope must survive the swap");
            values.Values.Should().NotContain(value => value != null && value.Contains(DocumentId, StringComparison.Ordinal));
        }
    }

    // The shape ASP.NET Core's hosting scope has: a read-only list of key/value pairs.
    private static IReadOnlyList<KeyValuePair<string, object?>> HostingLikeScope(string path) =>
        [new("RequestId", "0HN-scrub:00000001"), new("RequestPath", path)];

    private static List<object?> Scopes(IExternalScopeProvider provider)
    {
        var scopes = new List<object?>();
        provider.ForEachScope((scope, list) => list.Add(scope), scopes);
        return scopes;
    }

    private sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope
    {
        public IExternalScopeProvider? ScopeProvider { get; private set; }

        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

        public void Dispose()
        {
        }
    }
}
