using System.Diagnostics;
using AgentForge.Agents;
using AgentForge.Api.Evidence;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using AgentForge.Verification;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace AgentForge.UnitTests.Api.Evidence;

/// <summary>
/// NFR-TRACE-W2 at the entry point: <c>POST /evidence/ask</c> used to call the supervisor with no span
/// of its own, so the Week 2 flow had no application trace. The whole tree is asserted here, through the real
/// <see cref="EvidenceAgentSupervisor"/> behind faked dependencies: ONE root per request, the supervisor its
/// child, a worker span per worker that ran under the supervisor, the correlation id on the root, and no PHI
/// in the tree, on the server span it nests under, or in baggage. Guarded failure mode: a refactor that flattens or detaches the hierarchy, or drops the
/// correlation id that joins a trace to its log lines, fails here rather than degrading silently.
/// </summary>
public sealed class EvidenceAskTracingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 15, 0, 0, TimeSpan.Zero);

    private const string CorrelationId = "corr-gl619";
    private const string SessionPatient = "patient-sentinel-7730";
    private const string Clinician = "dr-sentinel-okafor";
    private const string Question = "Does Ingrid Vasquez-Holm need her furosemide raised?";

    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly IEvidenceAgentSupervisor _supervisor;

    public EvidenceAskTracingTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns(CorrelationId);
        var retriever = A.Fake<IEvidenceRetriever>();
        IReadOnlyList<EvidenceSnippet> noEvidence = [];
        A.CallTo(() => retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._)).Returns(noEvidence);
        var factStore = A.Fake<IDerivedFactStore>();
        IReadOnlyList<DerivedFact> noFacts = [];
        A.CallTo(() => factStore.GetByPatientAsync(A<string>._, A<CancellationToken>._)).Returns(noFacts);
        var llm = A.Fake<ILlmProvider>();
        A.CallTo(() => llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmResponse("draft", [], LlmStopReason.EndTurn, new LlmUsage(0, 0, 0m)));
        var verifier = A.Fake<IClinicalResponseVerifier>();
        A.CallTo(() => verifier.Verify(A<string>._, A<IReadOnlyCollection<string>>._))
            .Returns(new VerificationResult(true, "verified", [], []));
        _supervisor = new EvidenceAgentSupervisor(
            A.Fake<IDocumentExtractor>(), retriever, factStore, llm, verifier, _metrics,
            NullLogger<EvidenceAgentSupervisor>.Instance);
    }

    [Fact]
    public async Task HandleAskAsync_Answered_OpensExactlyOneRootWithTheSupervisorAndItsWorkersBeneathIt()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(ContextWithSession(), StubPatientRelationshipAuthorizer.Related);

        var root = recorder.Roots().Should().ContainSingle("one request is one application root span").Subject;
        root.DisplayName.Should().Be("evidence.ask");
        root.ParentSpanId.Should().Be(recorder.Request.SpanId,
            "the root joins the host's HTTP server span, so it is one trace with the request and its outbound calls");
        var supervisor = recorder.ChildrenOf(root).Should().ContainSingle().Subject;
        supervisor.DisplayName.Should().Be("evidence.supervisor");
        recorder.ChildrenOf(supervisor).Select(w => w.GetTagItem("agentforge.worker"))
            .Should().Equal("evidence-retriever", "answer-composer", "critic");
        recorder.Spans.Should().HaveCount(5);
    }

    [Fact]
    public async Task HandleAskAsync_Answered_PutsTheCorrelationIdAndOutcomeOnTheRoot()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(ContextWithSession(), StubPatientRelationshipAuthorizer.Related);

        var root = recorder.Spans.Should().ContainSingle(s => s.DisplayName == "evidence.ask").Subject;
        root.GetTagItem("agentforge.correlation_id").Should().Be(CorrelationId,
            "the correlation id is what every log line of the request carries, so it is the key from a log line to the trace");
        root.GetTagItem("agentforge.outcome").Should().Be("answered");
    }

    [Fact]
    public async Task HandleAskAsync_Refused_RootRecordsTheRefusalAndNoGraphSpanIsOpened()
    {
        using var recorder = SpanRecorder.Start();

        var result = await Ask(ContextWithSession(), StubPatientRelationshipAuthorizer.Unrelated);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var root = recorder.Spans.Should().ContainSingle("the graph never ran").Subject;
        root.DisplayName.Should().Be("evidence.ask");
        root.GetTagItem("agentforge.outcome").Should().Be("forbidden");
        root.GetTagItem("agentforge.correlation_id").Should().Be(CorrelationId);
    }

    [Fact]
    public async Task HandleAskAsync_NoLaunchedSession_RootRecordsUnauthorized()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(new DefaultHttpContext { Session = new InMemoryTestSession() }, StubPatientRelationshipAuthorizer.Related);

        recorder.Spans.Should().ContainSingle().Which.GetTagItem("agentforge.outcome").Should().Be("unauthorized");
    }

    [Fact]
    public async Task HandleAskAsync_NoQuestion_RootRecordsBadRequest()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(ContextWithSession(question: " "), StubPatientRelationshipAuthorizer.Related);

        recorder.Spans.Should().ContainSingle().Which.GetTagItem("agentforge.outcome").Should().Be("bad_request");
    }

    [Fact]
    public async Task HandleAskAsync_PageRenderedForAnotherPatient_RootRecordsPatientChanged()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(ContextWithSession(contextKey: new string('0', 32)), StubPatientRelationshipAuthorizer.Related);

        recorder.Spans.Should().ContainSingle().Which.GetTagItem("agentforge.outcome").Should().Be("patient_changed");
    }

    [Fact]
    public async Task HandleAskAsync_Answered_NoSpanCarriesThePatientTheClinicianOrTheQuestion()
    {
        using var recorder = SpanRecorder.Start();

        await Ask(ContextWithSession(), StubPatientRelationshipAuthorizer.Related);

        string[] phi = [SessionPatient, Clinician, Question, "Ingrid", "Vasquez", "furosemide"];
        recorder.Spans.Should().NotBeEmpty();
        recorder.ExportedStrings().Should().NotContain(
            s => phi.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "ARCHITECTURE-DOCUMENTS.md §12: traces carry no patient identifiers; the requester is named only in the access-audit trail");
        recorder.Spans.Single(s => s.DisplayName == "evidence.ask").TagObjects.Select(t => t.Key)
            .Should().BeEquivalentTo(["agentforge.correlation_id", "agentforge.outcome"],
                "a new root attribute is a deliberate edit here, by someone who has decided it is not patient data");
    }

    private Task<IResult> Ask(HttpContext httpContext, IPatientRelationshipAuthorizer authorizer) =>
        EvidenceEndpoints.HandleAskAsync(
            httpContext, _supervisor, authorizer, A.Fake<IScopedAccessTokenProvider>(), _correlationIdAccessor,
            new ExpiredSessionSignal(_metrics, _correlationIdAccessor, new CapturingLogger<ExpiredSessionSignal>()),
            new FixedTimeProvider(Now), new CapturingLogger<AccessAudit>(), AlwaysGrantingBudget());

    private static DefaultHttpContext ContextWithSession(string question = Question, string? contextKey = null)
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };
        var session = new PatientSessionContext("session-token", "default", SessionPatient, Clinician, Now.AddHours(1));
        httpContext.Session.SavePatientSession(session);
        httpContext.Request.ContentType = "multipart/form-data; boundary=----test";
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["question"] = question,
            [PatientContextBinding.QueryParameter] = contextKey ?? PatientContextBinding.KeyFor(httpContext.Session.Id, session),
        });
        return httpContext;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AgentForge.Api.Chat.IConversationTurnBudget AlwaysGrantingBudget()
    {
        var budget = A.Fake<AgentForge.Api.Chat.IConversationTurnBudget>();
        A.CallTo(() => budget.TryConsume(A<string>._)).Returns(true);
        return budget;
    }
}
