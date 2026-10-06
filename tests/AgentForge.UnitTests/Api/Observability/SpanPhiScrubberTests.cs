using System.Diagnostics;
using AgentForge.Api.Observability;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The scrubber every span passes through before any exporter sees it. Exporting traces to a
/// queryable backend makes the no-PHI rule for traces (ARCHITECTURE-DOCUMENTS.md §12, NFR-SEC-W2-1) load-bearing:
/// the HTTP instrumentations record the request URL, and this sidecar's URLs carry FHIR Patient and document
/// ids in their PATH. These pin that no identifier, query string, free-text status or client
/// address leaves the process on a span - and that the fixed-vocabulary tags the graph writes survive.
/// </summary>
public sealed class SpanPhiScrubberTests
{
    private const string PatientId = "9a7b4c1e-2f3d-4e5a-8b6c-7d8e9f0a1b2c";

    private static Activity Scrub(Action<Activity> tag, string name = "GET")
    {
        var activity = new Activity(name) { ActivityTraceFlags = ActivityTraceFlags.Recorded };
        tag(activity);
        new SpanPhiScrubber().OnEnd(activity);
        return activity;
    }

    [Fact]
    public void OnEnd_FhirReadUrlCarriesPatientId_KeepsOriginAndResourceTypeButNotTheId()
    {
        // Boundary: the outbound FHIR read is the span that carries a patient identifier on every tool call.
        var activity = Scrub(a => a.SetTag("url.full", $"https://openemr.internal:9300/apis/default/fhir/Patient/{PatientId}"));

        activity.GetTagItem("url.full").Should().Be("https://openemr.internal:9300/apis/default/fhir/Patient/{id}");
    }

    [Fact]
    public void OnEnd_UrlHasQueryString_DropsTheQueryEntirely()
    {
        // FHIR searches put the patient in the query (`?patient=`), and redacting values alone keeps the key.
        var activity = Scrub(a => a.SetTag("url.full", $"http://openemr/apis/default/fhir/DocumentReference?patient={PatientId}&_count=50"));

        activity.GetTagItem("url.full").Should().Be("http://openemr/apis/default/fhir/DocumentReference");
    }

    [Fact]
    public void OnEnd_UrlCarriesUserInfoOrFragment_DropsBoth()
    {
        var activity = Scrub(a => a.SetTag("url.full", "https://user:secret@api.example.test/v1/messages#frag"));

        activity.GetTagItem("url.full").Should().Be("https://api.example.test/v1/messages");
    }

    [Theory]
    [InlineData("/agentforge/evidence/document/48213", "/agentforge/evidence/document/{id}")]
    [InlineData("/apis/default/fhir/Binary/" + PatientId, "/apis/default/fhir/Binary/{id}")]
    [InlineData("/oauth2/default/token", "/oauth2/default/token")]
    [InlineData("/v1/messages", "/v1/messages")]
    [InlineData("/apis/default/fhir/Patient/$everything", "/apis/default/fhir/Patient/$everything")]
    [InlineData("/agentforge/", "/agentforge/")]
    [InlineData("/", "/")]
    public void OnEnd_InboundPath_KeepsVocabularySegmentsAndTemplatesIdentifiers(string path, string expected)
    {
        // Invariant: a segment survives only if it reads as vocabulary (letters, at most one trailing digit);
        // anything numeric, hex, percent-encoded or long is an identifier until proven otherwise.
        var activity = Scrub(a => a.SetTag("url.path", path));

        activity.GetTagItem("url.path").Should().Be(expected);
    }

    [Fact]
    public void OnEnd_PathSegmentIsPercentEncodedOrLong_IsTemplated()
    {
        var activity = Scrub(a => a.SetTag("url.path", "/search/J%C3%B6rg/" + new string('a', 41)));

        activity.GetTagItem("url.path").Should().Be("/search/{id}/{id}");
    }

    [Fact]
    public void OnEnd_LegacyHttpTargetCarriesQuery_IsTemplatedWithoutIt()
    {
        var activity = Scrub(a => a.SetTag("http.target", "/agentforge/evidence/document/77?patient=12"));

        activity.GetTagItem("http.target").Should().Be("/agentforge/evidence/document/{id}");
    }

    [Theory]
    [InlineData("url.query")]
    [InlineData("client.address")]
    public void OnEnd_TagCanOnlyCarryIdentifyingValues_IsRemoved(string key)
    {
        var activity = Scrub(a => a.SetTag(key, "patient=" + PatientId));

        activity.GetTagItem(key).Should().BeNull();
    }

    [Fact]
    public void OnEnd_UrlIsUnparseable_IsRemovedRatherThanExportedRaw()
    {
        // Fail closed: a value the scrubber cannot read is a value it cannot vouch for.
        var activity = Scrub(a => a.SetTag("url.full", "not a url " + PatientId));

        activity.GetTagItem("url.full").Should().BeNull();
    }

    [Fact]
    public void OnEnd_StatusDescriptionIsFreeText_IsClearedAndTheErrorStatusKept()
    {
        // Regression: AgentOrchestrator and McpToolDispatcher set the status description from ex.Message,
        // which is free text - a failed FHIR call's message can name the resource path.
        var activity = Scrub(a => a.SetStatus(ActivityStatusCode.Error, $"FHIR 404 for Patient/{PatientId}"));

        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().BeNull();
    }

    [Fact]
    public void OnEnd_SpanCarriesExceptionEvent_DropsItAndKeepsTheGraphsOwnEvents()
    {
        // Regression, found by the smoke run: on .NET 10 the runtime's System.Net.Http source records an
        // `exception` event (message + stack trace) whatever the instrumentation's RecordException says, and a
        // message is free text. The handoff events the supervisor writes are fixed vocabulary and must survive.
        var activity = Scrub(a =>
        {
            a.AddException(new InvalidOperationException($"FHIR read failed for Patient/{PatientId}"));
            a.AddEvent(new ActivityEvent("handoff", tags: new ActivityTagsCollection { ["agentforge.route.to"] = "critic" }));
        });

        activity.Events.Select(e => e.Name).Should().Equal("handoff");
        activity.Events.Single().Tags.Should().ContainSingle(t => t.Key == "agentforge.route.to");
        activity.Recorded.Should().BeTrue("removing the event must not cost the span itself");
    }

    [Fact]
    public void OnEnd_GraphSpanWithFixedVocabularyTags_IsLeftUnchanged()
    {
        // The graph's own tags are node names, outcomes and counts (EvidenceTracing) - scrubbing must not
        // cost the trace its diagnostic content.
        var activity = Scrub(
            a =>
            {
                a.SetTag("agentforge.worker", "evidence-retriever");
                a.SetTag("agentforge.outcome", "hit");
                a.SetTag("agentforge.evidence.snippet_count", 4);
                a.SetTag("http.route", "/evidence/document/{documentId}");
            },
            name: "worker.evidence-retriever");

        activity.DisplayName.Should().Be("worker.evidence-retriever");
        activity.GetTagItem("agentforge.worker").Should().Be("evidence-retriever");
        activity.GetTagItem("agentforge.outcome").Should().Be("hit");
        activity.GetTagItem("agentforge.evidence.snippet_count").Should().Be(4);
        activity.GetTagItem("http.route").Should().Be("/evidence/document/{documentId}");
    }
}
