using AgentForge.Agents;
using AgentForge.Api.Chat;
using AgentForge.Api.Evidence;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AgentForge.UnitTests.Api.Evidence;

/// <summary>
/// FR-AUTH-2 at the third choke point: <c>POST /evidence/ask</c>. This endpoint answers over the
/// patient's ingested document facts and returns verbatim quotes and page citations, and it used to
/// take the patient it answered about from the request form - so a launched session could name any
/// other patient and be briefed on them (UC-4, `ARCHITECTURE.md` §5.7).
/// </summary>
public sealed class EvidenceEndpointsAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 5, 36, TimeSpan.Zero);

    private const string Site = "default";
    private const string SessionPatient = "patient-123";
    private const string AnotherPatient = "patient-999";
    private const string ProviderOfRecord = "dr-cardio";

    private readonly IEvidenceAgentSupervisor _supervisor = A.Fake<IEvidenceAgentSupervisor>();
    private readonly IScopedAccessTokenProvider _tokenProvider = A.Fake<IScopedAccessTokenProvider>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly CapturingLogger<AccessAudit> _logger = new();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly IConversationTurnBudget _turnBudget = A.Fake<IConversationTurnBudget>();
    private readonly ExpiredSessionSignal _expiredSession;

    public EvidenceEndpointsAuthorizationTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).Returns(true);
        _expiredSession = new ExpiredSessionSignal(
            _metrics, _correlationIdAccessor, new CapturingLogger<ExpiredSessionSignal>());
        A.CallTo(() => _supervisor.RunAsync(A<EvidenceAgentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new EvidenceAgentResult
            {
                Answer = "answer",
                SafetyFlags = [],
                SuppressedClaims = [],
                Handoffs = [],
                Evidence = [],
            }));
    }

    [Fact]
    public async Task HandleAskAsync_FormNamesADifferentPatient_AnswersAboutTheSessionPatientInstead()
    {
        // The disclosure path itself: the form field is not narrowed, it is not read at all.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs", ["patientId"] = AnotherPatient });

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        result.Should().BeAssignableTo<IResult>();
        A.CallTo(() => _supervisor.RunAsync(
                A<EvidenceAgentRequest>.That.Matches(r => r.PatientId == SessionPatient), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleAskAsync_RequesterHasNoRelationshipToTheSessionPatient_RefusesWithoutRunningTheAgent()
    {
        // Re-checked on *use*, not only at session creation: the launch gate settled the pair once,
        // and this is the same question asked again at the moment data would actually be composed.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Unrelated);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_RequesterHasNoRelationshipToTheSessionPatient_RecordsTheRefusalInTheAccessAuditTrail()
    {
        // FR-AUTH-4: a denied access attempt is its own audit event, with the clinic-day appointment
        // count that tells a real refusal apart from an aged-out seed.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        await Ask(httpContext, StubPatientRelationshipAuthorizer.Unrelated);

        _logger.Lines.Should().ContainSingle(line =>
            line.Contains(ProviderOfRecord, StringComparison.Ordinal) &&
            line.Contains(SessionPatient, StringComparison.Ordinal) &&
            line.Contains("evidence_ask", StringComparison.Ordinal) &&
            line.Contains("clinicDayAppointments=4", StringComparison.Ordinal) &&
            line.Contains("corr-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleAskAsync_RequesterIsRelatedToTheSessionPatient_RecordsTheGrantInTheAccessAuditTrail()
    {
        // FR-AUTH-4: the permitted path composes an answer over the patient's document facts, and
        // used to leave no record that it had. One line naming who, which patient and which request.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        _logger.Lines.Should().ContainSingle().Which.Should().Contain(
            $"clinician={ProviderOfRecord} accessed patient={SessionPatient} via tool=evidence_ask correlation=corr-1");
    }

    [Fact]
    public async Task HandleAskAsync_RelationshipIsDecided_UsesTheSessionsOwnTokenForTheLookup()
    {
        // The relationship lookup is a FHIR read; without the session token it is unauthenticated,
        // throws, and fails closed on everyone.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        A.CallTo(() => _tokenProvider.Adopt("session-token", A<DateTimeOffset>._)).MustHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_NoLaunchedSession_ReturnsUnauthorizedWithoutRunningTheAgent()
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    private Task<IResult> Ask(HttpContext httpContext, IPatientRelationshipAuthorizer authorizer) =>
        Ask(httpContext, authorizer, Now);

    private Task<IResult> Ask(HttpContext httpContext, IPatientRelationshipAuthorizer authorizer, DateTimeOffset now) =>
        EvidenceEndpoints.HandleAskAsync(
            httpContext, _supervisor, authorizer, _tokenProvider, _correlationIdAccessor,
            _expiredSession, new FixedTimeProvider(now), _logger, _turnBudget);

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: { } status } ? status : 200;

    // The form carries the key GET /patient gave a page rendered for the session's patient, unless a test sets its own.
    private static DefaultHttpContext ContextWithSession(
        Dictionary<string, StringValues> form, DateTimeOffset? expiresAt = null)
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };
        var session = PatientSession(SessionPatient, expiresAt);
        httpContext.Session.SavePatientSession(session);
        form.TryAdd(PatientContextBinding.QueryParameter, PatientContextBinding.KeyFor(httpContext.Session.Id, session));
        httpContext.Request.ContentType = "multipart/form-data; boundary=----test";
        httpContext.Request.Form = new FormCollection(form);
        return httpContext;
    }

    private static PatientSessionContext PatientSession(string patientId, DateTimeOffset? expiresAt = null) =>
        new("session-token", Site, patientId, ProviderOfRecord, expiresAt ?? Now.AddHours(1));

    [Fact]
    public async Task HandleAskAsync_PatientSwitchedAfterThePageLoaded_RefusesTheStalePageRatherThanAnsweringForTheNewPatient()
    {
        // The scenario end to end at the handler: the page loaded for SessionPatient, then another tab's
        // drill-down switched the same session to AnotherPatient. The session alone would answer for AnotherPatient
        // under a page that still shows SessionPatient; the page's key is what says which patient it shows.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        httpContext.Session.SavePatientSession(PatientSession(AnotherPatient));

        // Related: the clinician may see AnotherPatient, so nothing but the page binding stands in the way.
        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        result.Should().BeAssignableTo<IValueHttpResult>().Which.Value.Should().Be(ChatHub.PatientChangedMessage,
            "one refusal for one condition, whether the stale page is the chat or the evidence page");
        A.CallTo(_supervisor).MustNotHaveHappened();
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).MustNotHaveHappened();
        _logger.Lines.Should().BeEmpty("the stale page asked about no patient's data, so there is no access to audit");
    }

    [Fact]
    public async Task HandleAskAsync_PageReloadedForTheNewPatient_IsAnsweredForTheNewPatient()
    {
        // The other direction: the refusal is about the page, not the switch. A page rendered after it presents the
        // new patient's key and is answered about that patient.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        var switchedTo = PatientSession(AnotherPatient);
        httpContext.Session.SavePatientSession(switchedTo);
        httpContext.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["question"] = "summarise the labs",
            [PatientContextBinding.QueryParameter] = PatientContextBinding.KeyFor(httpContext.Session.Id, switchedTo),
        });

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _supervisor.RunAsync(
                A<EvidenceAgentRequest>.That.Matches(r => r.PatientId == AnotherPatient), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("00000000000000000000000000000000")]
    public async Task HandleAskAsync_ContextKeyMissingOrForeign_RefusesWithoutRunningTheAgent(string presentedKey)
    {
        // A page (or a client) that says nothing about which patient it shows is not answered either.
        var httpContext = ContextWithSession(
            form: new() { ["question"] = "summarise the labs", [PatientContextBinding.QueryParameter] = presentedKey });

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_StalePageWithNoQuestion_IsStillABadRequest()
    {
        // The existing order holds: 401, then 400, then the page binding, then 403.
        var httpContext = ContextWithSession(form: new() { ["question"] = " " });
        httpContext.Session.SavePatientSession(PatientSession(AnotherPatient));

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleAskAsync_StalePageFromAClinicianWithNoRelationship_IsRefusedAsStaleBeforeTheRelationshipLookup()
    {
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        httpContext.Session.SavePatientSession(PatientSession(AnotherPatient));

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Unrelated);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        _logger.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAskAsync_SessionTokenHasExpired_ReturnsUnauthorizedWithoutRunningTheAgent()
    {
        // review round 1. An expired session used to reach the FR-AUTH-2 lookup, whose FHIR
        // read now throws - and with no catch on this handler that surfaced as an unhandled 500
        // where the same session previously got a 403. It is the same answer as "no launched
        // session", because that is what an aged-out session is.
        var httpContext = ContextWithSession(
            form: new() { ["question"] = "summarise the labs" }, expiresAt: Now.AddMinutes(-1));

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_TokenExpiresMidRequest_ReturnsUnauthorizedRatherThanFailingTheRequest()
    {
        // Live when the session was read, dead by the time the relationship lookup went out. The
        // narrow window the pre-check above cannot close, and the one that produced the 500.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        var expiringAuthorizer = A.Fake<IPatientRelationshipAuthorizer>();
        A.CallTo(() => expiringAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        var result = await Ask(httpContext, expiringAuthorizer);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_TokenExpiresMidRequest_CountsItAsAnExpiredSessionRatherThanAnAuthorizationDecision()
    {
        // ARCHITECTURE.md §5.7 names this one of the three choke points that answer an
        // expired session, and it answered silently: a 401 the clinician sees and nothing the
        // operator does. The counter it must *not* move is the entitlement one - a dead token
        // decides nothing about whether this clinician may see this patient.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        var expiringAuthorizer = A.Fake<IPatientRelationshipAuthorizer>();
        A.CallTo(() => expiringAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        await Ask(httpContext, expiringAuthorizer);

        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(ExpiredSessionSurface.EvidenceAsk))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordAuthorizationDecision(A<bool>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_RequesterHasNoRelationshipToTheSessionPatient_LeavesTheExpiredSessionSeriesAlone()
    {
        // The reverse of the pin above, and the reason a separate change is a new series rather than a label:
        // a genuine entitlement refusal must never read as a session that aged out, or an operator
        // watching the one-hour wall is watching the gate working instead.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        await Ask(httpContext, StubPatientRelationshipAuthorizer.Unrelated);

        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(A<string>._)).MustNotHaveHappened();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task HandleAskAsync_SessionBudgetExhausted_Answers429WithoutRunningTheAgent()
    {
        // The edge limits /evidence/ask per minute, never in total; each ask runs several LLM calls, so it is
        // charged to the same per-session budget as a chat turn.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });
        A.CallTo(() => _turnBudget.TryConsume(httpContext.Session.Id)).Returns(false);

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status429TooManyRequests);
        A.CallTo(_supervisor).MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleAskAsync_AnsweredAsk_ChargesTheSessionsBudgetOnce()
    {
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        var result = await Ask(httpContext, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _turnBudget.TryConsume(httpContext.Session.Id)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleAskAsync_RefusedForNoRelationship_ChargesNothing()
    {
        // A request that never reaches the model must not spend the clinician's budget.
        var httpContext = ContextWithSession(form: new() { ["question"] = "summarise the labs" });

        await Ask(httpContext, StubPatientRelationshipAuthorizer.Unrelated);

        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).MustNotHaveHappened();
    }
}
