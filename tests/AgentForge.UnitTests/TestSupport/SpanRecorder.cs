using System.Collections.Concurrent;
using System.Diagnostics;
using AgentForge.Observability;

namespace AgentForge.UnitTests.TestSupport;

/// <summary>
/// In-memory stand-in for the OTel tracer: listens to <see cref="AgentForgeActivitySource"/> and keeps every
/// span of ONE trace. The trace is seeded by an ambient <c>test.request</c> activity standing in for the ASP.NET
/// Core server span the host opens around every request; a span that loses its link to it lands in another
/// trace and is not recorded, which is what makes a dropped parent visible to the assertions.
/// </summary>
/// <remarks>
/// Create it with <see cref="Start"/> inside the test method, so the ambient activity is set on the test's own
/// execution context. The listener is process-wide, so filtering on the trace id is what keeps parallel tests
/// out of each other's recordings.
/// </remarks>
internal sealed class SpanRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _spans = new();

    private SpanRecorder()
    {
        // Started before the listener exists: the callback runs on other tests' threads too, and must never
        // see a recorder without a trace to filter on.
        Request = new Activity("test.request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var traceId = Request.TraceId;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentForgeActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId)
                {
                    _spans.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The ambient request activity every recorded span must descend from.</summary>
    public Activity Request { get; }

    /// <summary>Every stopped <c>AgentForge</c> span in the request's trace, in stop order.</summary>
    public IReadOnlyList<Activity> Spans => [.. _spans];

    /// <summary>Starts recording, with the request activity set as <see cref="Activity.Current"/>.</summary>
    public static SpanRecorder Start() => new();

    /// <summary>The spans whose parent is not another recorded span - the application roots of the trace.</summary>
    public IReadOnlyList<Activity> Roots()
    {
        var spans = Spans;
        var ids = spans.Select(s => s.SpanId).ToHashSet();
        return [.. spans.Where(s => !ids.Contains(s.ParentSpanId))];
    }

    /// <summary>The recorded spans whose parent is <paramref name="parent"/>.</summary>
    public IReadOnlyList<Activity> ChildrenOf(Activity parent) =>
        [.. Spans.Where(s => s.ParentSpanId == parent.SpanId)];

    /// <summary>
    /// Every string an exporter could receive from this request: the recorded spans' names, tag keys and values,
    /// event names and event tags, status descriptions and baggage, plus the tags and baggage of the request
    /// activity itself - the stand-in for the server span every recorded span nests under, which is exported too.
    /// What the no-PHI assertions scan.
    /// </summary>
    public IEnumerable<string> ExportedStrings()
    {
        foreach (var tag in Request.TagObjects)
        {
            yield return tag.Key;
            yield return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        // Baggage propagates to outbound calls and child spans, so it leaks as surely as a tag.
        foreach (var item in Spans.SelectMany(s => s.Baggage).Concat(Request.Baggage))
        {
            yield return item.Key;
            yield return item.Value ?? string.Empty;
        }

        foreach (var span in Spans)
        {
            yield return span.DisplayName;
            yield return span.OperationName;
            if (span.StatusDescription is { } status)
            {
                yield return status;
            }

            foreach (var tag in span.TagObjects)
            {
                yield return tag.Key;
                yield return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }

            foreach (var spanEvent in span.Events)
            {
                yield return spanEvent.Name;
                foreach (var tag in spanEvent.Tags)
                {
                    yield return tag.Key;
                    yield return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
        }
    }

    /// <summary>Every tag key on every recorded span and span event.</summary>
    public IEnumerable<string> TagKeys() =>
        Spans.SelectMany(s => s.TagObjects.Select(t => t.Key)
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => t.Key))));

    /// <inheritdoc />
    public void Dispose()
    {
        Request.Stop();
        _listener.Dispose();
    }
}
