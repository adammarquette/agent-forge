using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace AgentForge.Api.Observability;

/// <summary>
/// Removes patient identifiers from every span before any exporter reads it (ARCHITECTURE-DOCUMENTS.md §12,
/// NFR-SEC-W2-1). The HTTP instrumentations record request URLs, and this sidecar's URLs carry FHIR
/// Patient, Binary and document ids in their path, so on end: a URL keeps its scheme,
/// host, port and a path whose identifier segments read <c>{id}</c>; query strings, fragments, user info
/// and the client address are dropped; the free-text status description is cleared while the status code
/// is kept; and <c>exception</c> events (message and stack trace) are removed while every other event is
/// kept. A URL that cannot be parsed is removed rather than exported raw. Registered ahead of the
/// exporters in <c>Program.cs</c>, so it sees each span first.
/// </summary>
public sealed partial class SpanPhiScrubber : BaseProcessor<Activity>
{
    private const string IdPlaceholder = "{id}";
    private const int MaxVocabularySegmentLength = 40;

    private static readonly string[] UrlKeys = ["url.full", "http.url"];
    private static readonly string[] PathKeys = ["url.path", "http.target"];
    private static readonly string[] RemovedKeys = ["url.query", "client.address"];

    // The OTel semantic-convention event System.Net.Http records on .NET 10 regardless of RecordException.
    private const string ExceptionEventName = "exception";

    private static readonly FieldInfo? EventsField =
        typeof(Activity).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (var key in UrlKeys)
        {
            if (data.GetTagItem(key) is { } value)
            {
                data.SetTag(key, ScrubUrl(value.ToString()));
            }
        }

        foreach (var key in PathKeys)
        {
            if (data.GetTagItem(key) is { } value)
            {
                data.SetTag(key, ScrubPath(value.ToString() ?? string.Empty));
            }
        }

        foreach (var key in RemovedKeys)
        {
            data.SetTag(key, null);
        }

        if (data.StatusDescription is not null)
        {
            data.SetStatus(data.Status);
        }

        DropExceptionEvents(data);
    }

    // Activity has no API to remove an event, so the list is reset and the survivors re-added. If a future
    // runtime renames the field, the span is dropped from export rather than shipped with its exception text.
    private static void DropExceptionEvents(Activity data)
    {
        var events = data.Events.ToList();
        if (!events.Exists(e => e.Name == ExceptionEventName))
        {
            return;
        }

        if (EventsField is null)
        {
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        EventsField.SetValue(data, null);
        foreach (var kept in events.Where(e => e.Name != ExceptionEventName))
        {
            data.AddEvent(kept);
        }
    }

    private static string? ScrubUrl(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        return origin + ScrubPath(uri.AbsolutePath);
    }

    // Shared with RequestPathScrubbingScopeProvider, so a log scope's path reads as the span's does.
    internal static string ScrubPath(string raw)
    {
        var cut = raw.IndexOfAny(['?', '#']);
        var path = cut >= 0 ? raw[..cut] : raw;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0 && !IsVocabulary(segments[i]))
            {
                segments[i] = IdPlaceholder;
            }
        }

        return string.Join('/', segments);
    }

    // Letters, `-`, `_`, `.`, a leading `$` (FHIR operations) and at most one trailing digit (`v1`, `oauth2`).
    private static bool IsVocabulary(string segment) =>
        segment.Length <= MaxVocabularySegmentLength && VocabularySegment().IsMatch(segment);

    [GeneratedRegex(@"^\$?[A-Za-z][A-Za-z_.\-]*[0-9]?$", RegexOptions.CultureInvariant)]
    private static partial Regex VocabularySegment();
}
