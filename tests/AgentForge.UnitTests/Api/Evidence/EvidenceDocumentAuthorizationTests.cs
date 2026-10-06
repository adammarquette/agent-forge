using AgentForge.Api.Evidence;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace AgentForge.UnitTests.Api.Evidence;

/// <summary>
/// FR-AUTH-2 and FR-AUTH-4 at <c>GET /evidence/document/{id}</c>, the click-to-source fetch. It used to stream
/// whatever <c>Binary</c> id it was handed with only the token's scope standing between a session and another
/// patient's document, and wrote no audit record either way. A <c>user/Binary.read</c> token is not confined to
/// one patient, so a single scope added to the agenda launch would have made that a live cross-patient read.
/// </summary>
public sealed class EvidenceDocumentAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 15, 5, 36, TimeSpan.Zero);

    private const string Site = "default";
    private const string SessionPatient = "patient-123";
    private const string AnotherPatient = "patient-999";
    private const string Clinician = "dr-cardio";
    private const string SessionPatientsDocument = "docref-own";
    private const string AnotherPatientsDocument = "docref-other";
    private const string UnindexedDocument = "docref-unknown";

    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly IDerivedFactStore _store = A.Fake<IDerivedFactStore>();
    private readonly IScopedAccessTokenProvider _tokenProvider = A.Fake<IScopedAccessTokenProvider>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly CapturingLogger<AccessAudit> _auditLogger = new();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly ExpiredSessionSignal _expiredSession;

    public EvidenceDocumentAuthorizationTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-7");
        _expiredSession = new ExpiredSessionSignal(
            _metrics, _correlationIdAccessor, new CapturingLogger<ExpiredSessionSignal>());
        A.CallTo(() => _store.FindPatientIdByDocumentReferenceIdAsync(SessionPatientsDocument, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(SessionPatient));
        A.CallTo(() => _store.FindPatientIdByDocumentReferenceIdAsync(AnotherPatientsDocument, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(AnotherPatient));
        A.CallTo(() => _store.FindPatientIdByDocumentReferenceIdAsync(UnindexedDocument, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(null));
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<BinaryDocument?>(new BinaryDocument([0x25, 0x50, 0x44, 0x46], "application/pdf")));
    }

    [Fact]
    public async Task GetDocumentAsync_DocumentBelongsToAnotherPatient_RefusesWithoutFetchingIt()
    {
        // The regression itself: a document id outside the session's patient never reaches Binary,
        // whatever the token's scope would have allowed.
        var result = await Fetch(AnotherPatientsDocument, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetDocumentAsync_DocumentBelongsToAnotherPatient_RecordsTheRefusalAgainstThatPatient()
    {
        // FR-AUTH-4: a denied attempt is its own audit event, and the patient it names is the one whose
        // document was asked for - that is who a reviewer needs to find.
        await Fetch(AnotherPatientsDocument, StubPatientRelationshipAuthorizer.Related);

        _auditLogger.Lines.Should().ContainSingle().Which.Should()
            .Contain("REFUSED")
            .And.Contain($"clinician={Clinician}")
            .And.Contain($"patient={AnotherPatient}")
            .And.Contain("tool=evidence_document")
            .And.Contain(PatientAccessRefusal.DocumentOutsideSessionPatientAuditReason)
            .And.Contain("correlation=corr-7");
    }

    [Fact]
    public async Task GetDocumentAsync_DocumentWasNeverIngested_RefusesAndRecordsAnUnresolvedPatient()
    {
        // The sidecar's ingest index is the only thing that can say whose document an id is. An id it has
        // never seen is refused the same way, so a 403-versus-404 difference cannot be used to probe ids.
        var result = await Fetch(UnindexedDocument, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        _auditLogger.Lines.Should().ContainSingle().Which.Should()
            .Contain("REFUSED").And.Contain("patient=unresolved").And.Contain("tool=evidence_document");
    }

    [Fact]
    public async Task GetDocumentAsync_RequesterHasNoRelationshipToTheSessionPatient_RefusesAndRecordsTheRefusal()
    {
        // Re-checked on use, like /evidence/ask: the session's own patient is only as current as the launch.
        var result = await Fetch(SessionPatientsDocument, StubPatientRelationshipAuthorizer.Unrelated);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        _auditLogger.Lines.Should().ContainSingle().Which.Should()
            .Contain("REFUSED")
            .And.Contain($"patient={SessionPatient}")
            .And.Contain("tool=evidence_document")
            .And.Contain(PatientAccessRefusal.AuditReason)
            .And.Contain("clinicDayAppointments=4");
    }

    [Fact]
    public async Task GetDocumentAsync_SessionPatientsDocumentAndRelatedRequester_StreamsItAndRecordsTheAccess()
    {
        var result = await Fetch(SessionPatientsDocument, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => _fhirClient.GetBinaryAsync(Site, SessionPatientsDocument, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        _auditLogger.Lines.Should().ContainSingle().Which.Should()
            .Contain($"clinician={Clinician} accessed patient={SessionPatient} via tool=evidence_document correlation=corr-7");
    }

    [Fact]
    public async Task GetDocumentAsync_AnyDecision_NeverWritesTheDocumentIdToTheAuditTrail()
    {
        // The audit trail is the one place a patient id belongs; a document id is not exempted with it.
        await Fetch(SessionPatientsDocument, StubPatientRelationshipAuthorizer.Related);
        await Fetch(AnotherPatientsDocument, StubPatientRelationshipAuthorizer.Related);

        _auditLogger.Lines.Should().HaveCount(2).And.OnlyContain(line =>
            !line.Contains(SessionPatientsDocument, StringComparison.Ordinal) &&
            !line.Contains(AnotherPatientsDocument, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDocumentAsync_RelationshipIsDecided_AsksAboutTheSessionPatientAsTheSessionClinician()
    {
        var authorizer = A.Fake<IPatientRelationshipAuthorizer>();
        A.CallTo(() => authorizer.AuthorizeAsync(A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientRelationshipDecision(true, 1)));

        await Fetch(SessionPatientsDocument, authorizer);

        A.CallTo(() => authorizer.AuthorizeAsync(Site, Clinician, SessionPatient, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _tokenProvider.Adopt("session-token", A<DateTimeOffset>._)).MustHaveHappened();
    }

    [Fact]
    public async Task GetDocumentAsync_TokenExpiresDuringTheRelationshipLookup_ReturnsUnauthorizedAndAuditsNothing()
    {
        // Expiry is not an entitlement decision: counted on the expired-session series, not audited.
        var authorizer = A.Fake<IPatientRelationshipAuthorizer>();
        A.CallTo(() => authorizer.AuthorizeAsync(A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        var result = await Fetch(SessionPatientsDocument, authorizer);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        _auditLogger.Lines.Should().BeEmpty();
        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(ExpiredSessionSurface.EvidenceDocument))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetDocumentAsync_NoLaunchedSession_ReturnsUnauthorizedWithoutResolvingTheDocument()
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };

        var result = await EvidenceEndpoints.GetDocumentAsync(
            httpContext, SessionPatientsDocument, contextKey: null, _fhirClient, _store, StubPatientRelationshipAuthorizer.Related,
            _tokenProvider, _correlationIdAccessor, _expiredSession, new FixedTimeProvider(Now), _auditLogger);

        StatusOf(result).Should().Be(StatusCodes.Status401Unauthorized);
        A.CallTo(_store).MustNotHaveHappened();
        _auditLogger.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDocumentAsync_PatientSwitchedAfterThePageLoaded_Answers409WithoutResolvingOrAuditingAnything()
    {
        // A page rendered for SessionPatient clicks a citation after another tab switched the session to AnotherPatient.
        // Answered 409 so the page reloads, before the ownership check could log the clinician as reaching for a
        // document outside their patient. A separate change
        var httpContext = SessionFor(SessionPatient);
        var stalePageKey = PatientContextBinding.KeyFor(httpContext.Session.Id, PatientSession(SessionPatient));
        httpContext.Session.SavePatientSession(PatientSession(AnotherPatient));

        var result = await Fetch(httpContext, SessionPatientsDocument, stalePageKey, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(_store).MustNotHaveHappened();
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        _auditLogger.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDocumentAsync_NoContextKey_Answers409WithoutResolvingTheDocument()
    {
        var httpContext = SessionFor(SessionPatient);

        var result = await Fetch(httpContext, SessionPatientsDocument, contextKey: null, StubPatientRelationshipAuthorizer.Related);

        StatusOf(result).Should().Be(StatusCodes.Status409Conflict);
        A.CallTo(_store).MustNotHaveHappened();
    }

    private Task<IResult> Fetch(string documentId, IPatientRelationshipAuthorizer authorizer)
    {
        var httpContext = SessionFor(SessionPatient);
        return Fetch(
            httpContext, documentId, PatientContextBinding.KeyFor(httpContext.Session.Id, PatientSession(SessionPatient)), authorizer);
    }

    private Task<IResult> Fetch(
        HttpContext httpContext, string documentId, string? contextKey, IPatientRelationshipAuthorizer authorizer) =>
        EvidenceEndpoints.GetDocumentAsync(
            httpContext, documentId, contextKey, _fhirClient, _store, authorizer, _tokenProvider, _correlationIdAccessor,
            _expiredSession, new FixedTimeProvider(Now), _auditLogger);

    private static PatientSessionContext PatientSession(string patientId) =>
        new("session-token", Site, patientId, Clinician, Now.AddHours(1));

    private static DefaultHttpContext SessionFor(string patientId)
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };
        httpContext.Session.SavePatientSession(PatientSession(patientId));
        return httpContext;
    }

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: { } status } ? status : 200;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
