using System.Collections.Concurrent;
using System.Diagnostics;
using AgentForge.Observability;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// Listens to two families of <see cref="ActivitySource"/> while a run is in flight: the application's own
/// (every span it would export to Tempo, flattened to the strings an exporter would ship) and the runtime's
/// networking sources, which start an activity for every HTTP request (<c>System.Net.Http</c>), socket
/// connect (<c>Experimental.System.Net.Sockets</c>) and DNS lookup (<c>Experimental.System.Net.NameResolution</c>)
/// in the process. The second list is how "no network" is asserted rather than assumed; a red control per
/// kind proves each source is heard. Global by nature, which is why this assembly runs its tests one at a time.
/// </summary>
internal sealed class TelemetryRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<string> _spanText = new();
    private readonly ConcurrentQueue<string> _network = new();

    public TelemetryRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentForgeActivitySource.Name || IsNetworkSource(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (IsNetworkSource(activity.Source.Name))
                {
                    _network.Enqueue($"{activity.Source.Name}: {activity.DisplayName}");
                }
            },
            ActivityStopped = activity =>
            {
                if (activity.Source.Name != AgentForgeActivitySource.Name)
                {
                    return;
                }

                _spanText.Enqueue(activity.DisplayName);
                foreach (var (_, value) in activity.TagObjects)
                {
                    _spanText.Enqueue(value?.ToString() ?? string.Empty);
                }

                foreach (var e in activity.Events)
                {
                    _spanText.Enqueue(e.Name);
                    foreach (var (_, value) in e.Tags)
                    {
                        _spanText.Enqueue(value?.ToString() ?? string.Empty);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Every string the application's spans carried.</summary>
    public IReadOnlyList<string> SpanText => [.. _spanText];

    /// <summary>Every network operation the runtime started, as "source: operation".</summary>
    public IReadOnlyList<string> NetworkActivity => [.. _network];

    public void Dispose() => _listener.Dispose();

    // .NET 10 still ships the socket and DNS sources under an "Experimental." prefix; HTTP's has none.
    private static bool IsNetworkSource(string name) =>
        name.StartsWith("System.Net", StringComparison.Ordinal)
        || name.StartsWith("Experimental.System.Net", StringComparison.Ordinal);
}
