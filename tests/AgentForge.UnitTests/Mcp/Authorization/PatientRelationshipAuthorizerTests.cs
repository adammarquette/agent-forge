using System.Net;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp.Authorization;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.UnitTests.Mcp.Authorization;

/// <summary>
/// Resolving the FR-AUTH-2 relationship against the one signal this deployment actually carries:
/// the provider participant on the clinic day's FHIR <c>Appointment</c>s (the same column the
/// Daily Agenda's roster filter already trusts).
/// </summary>
public sealed class PatientRelationshipAuthorizerTests
{
    private const string Site = "default";
    private const string Clinician = "dr-cardio";
    private const string Patient = "patient-123";

    // 14:30 UTC on 2026-09-18 is 09:30 in the clinic - same calendar day either way, so the tests
    // that are not about the clinic day are unaffected by the zone.
    private static readonly DateTimeOffset MidMorning = new(2026, 9, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly CapturingLogger<PatientRelationshipAuthorizer> _logger = new();
    private readonly PatientRelationshipAuthorizer _sut;

    public PatientRelationshipAuthorizerTests() => _sut = BuildSut(MidMorning);

    [Fact]
    public async Task AuthorizeAsync_ClinicianIsTheProviderOnTodaysAppointment_PermitsAccess()
    {
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var decision = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        decision.IsRelated.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_RequesterHasNoAppointmentWithThePatient_RefusesAccess()
    {
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var decision = await _sut.AuthorizeAsync(Site, "admin", Patient, CancellationToken.None);

        decision.IsRelated.Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_TwoRequestersAskedOfOneInstance_AreDecidedSeparately()
    {
        // The memo key must carry the requester's identity. Nothing else in the suite proves it:
        // every other case builds a fresh instance per requester, so dropping `clinicianIdentity`
        // from the key leaves them all green while one clinician's permit silently answers for the
        // next - which is the cross-patient disclosure this whole change exists to stop. Identity
        // being fixed per DI scope today is what makes this unreachable, not what makes it safe.
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var provider = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);
        var unrelated = await _sut.AuthorizeAsync(Site, "admin", Patient, CancellationToken.None);

        provider.IsRelated.Should().BeTrue();
        unrelated.IsRelated.Should().BeFalse("a memo keyed without the requester would replay the permit");
    }

    [Fact]
    public async Task AuthorizeAsync_TwoSitesAskedOfOneInstance_AreDecidedSeparately()
    {
        // The other half of the key. The second site's calendar is empty, so a key that dropped the
        // site would replay the first site's permit.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment(Patient, $"Practitioner/{Clinician}")]));
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("other-site", A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([]));

        var here = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);
        var elsewhere = await _sut.AuthorizeAsync("other-site", Clinician, Patient, CancellationToken.None);

        here.IsRelated.Should().BeTrue();
        elsewhere.IsRelated.Should().BeFalse("a memo keyed without the site would replay the other site's permit");
    }

    [Fact]
    public async Task AuthorizeAsync_ValidRequest_AsksOpenEmrForTheCurrentClinicDayOnly()
    {
        // Minimum-necessary: the relationship lookup must not turn into an unbounded calendar read.
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-18", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AuthorizeAsync_UtcHasRolledOverButTheClinicDayHasNot_StillAsksForTheClinicsDay()
    {
        // 01:00 UTC on the 19th is 20:00 on the 18th in the clinic. Resolving the day in UTC asks
        // for tomorrow's calendar, matches nothing, and refuses every launch from early evening
        // onwards - including the clinician standing in front of their 19:30 patient.
        var sut = BuildSut(new DateTimeOffset(2026, 9, 19, 1, 0, 0, TimeSpan.Zero));
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var decision = await sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-18", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        decision.IsRelated.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_RefusesAccess_ReportsHowManyClinicDayAppointmentsItConsidered()
    {
        // A refusal against a full calendar is the gate working; a refusal against an empty one is
        // an aged-out seed. Nothing else distinguishes them (FR-AUTH-4).
        GiveAppointments(
            Appointment(Patient, $"Practitioner/{Clinician}"),
            Appointment("patient-456", $"Practitioner/{Clinician}"));

        var decision = await _sut.AuthorizeAsync(Site, "admin", Patient, CancellationToken.None);

        decision.IsRelated.Should().BeFalse();
        decision.ClinicDayAppointmentsConsidered.Should().Be(2);
        decision.AuditCount.Should().Be("2");
    }

    [Fact]
    public async Task AuthorizeAsync_ClinicDayIsEmpty_ReportsZeroAppointmentsRatherThanNoInformation()
    {
        GiveAppointments();

        var decision = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        decision.IsRelated.Should().BeFalse();
        decision.AuditCount.Should().Be("0");
    }

    [Fact]
    public async Task AuthorizeAsync_AppointmentLookupFails_ReturnsFalseRatherThanAssumingARelationship()
    {
        // Fail closed (ARCHITECTURE.md §5.7): an unresolvable relationship is a refusal, never an
        // allow - the opposite choice would make every OpenEMR outage an authorization bypass.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("OpenEMR unreachable"));

        var decision = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        decision.IsRelated.Should().BeFalse();
        decision.AuditCount.Should().Be("unresolved", "an unresolved lookup counted nothing, which is not zero");
    }

    [Fact]
    public async Task AuthorizeAsync_AppointmentLookupFails_LogsWhyTheRelationshipCouldNotBeResolvedWithoutNamingThePatient()
    {
        // A fail-closed denial is indistinguishable from a real "not your patient" without this
        // line, and the two need completely different operator responses. It must not name the
        // patient: every caller audits the refusal (AccessAuditLog.RecordRefusal) under the same
        // correlation id, and that is the one stream allowed to (CONVENTIONS.md §7).
        // The exception message is dropped too - a failed FHIR call can echo the id.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException($"GET /fhir/Appointment?patient={Patient} unreachable"));

        await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        _logger.Lines.Should().ContainSingle(line =>
            line.Contains(Clinician, StringComparison.Ordinal) &&
            line.Contains(nameof(HttpRequestException), StringComparison.Ordinal) &&
            !line.Contains(Patient, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorizeAsync_OpenEmrRejectsTheLookupWith401_FailsClosedAndLogsTheStatus()
    {
        // a token OpenEMR issued without patient/Appointment.read gets a 401 on this search.
        // The outcome is the same refusal as any unresolvable lookup; the status is what lets an
        // operator tell "the token cannot read the calendar" from "OpenEMR is down" without
        // reading OpenEMR's own log. Refit's ApiException, since that is what the client throws.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://openemr.example.org/apis/default/fhir/Appointment?patient={Patient}");
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent(string.Empty) };
        var unauthorized = await ApiException.Create(request, HttpMethod.Get, response, new RefitSettings());
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(unauthorized);

        var decision = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        decision.Should().Be(PatientRelationshipDecision.Unresolved);
        _logger.Lines.Should().ContainSingle(line =>
            line.Contains("401", StringComparison.Ordinal) &&
            line.Contains(Clinician, StringComparison.Ordinal) &&
            !line.Contains(Patient, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorizeAsync_NoClinicianIdentity_ReturnsFalseWithoutCallingOpenEmr()
    {
        var decision = await _sut.AuthorizeAsync(Site, null, Patient, CancellationToken.None);

        decision.IsRelated.Should().BeFalse();
        A.CallTo(_fhirClient).MustNotHaveHappened();
    }

    [Fact]
    public async Task AuthorizeAsync_SameTripleAskedRepeatedly_ResolvesAgainstOpenEmrOnlyOnce()
    {
        // One agent turn dispatches its tool calls in parallel (AgentOrchestrator's Task.WhenAll),
        // so without memoization a single brief would fire one calendar search per tool call.
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None)));

        decisions.Should().AllSatisfy(d => d.IsRelated.Should().BeTrue());
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AuthorizeAsync_DifferentPatientOnTheSameLookup_IsDecidedSeparately()
    {
        // The memo must key on the patient too, or the first decision would leak onto every other
        // patient asked about in the same scope.
        GiveAppointments(Appointment(Patient, $"Practitioner/{Clinician}"));

        var mine = await _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);
        var notMine = await _sut.AuthorizeAsync(Site, Clinician, "patient-999", CancellationToken.None);

        mine.IsRelated.Should().BeTrue();
        notMine.IsRelated.Should().BeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_ClinicDayRollsOverBetweenAsks_DoesNotServeTheEarlierDaysPermit()
    {
        // The memo caches "is there an appointment *today*", so the clinic day is the one dimension
        // that makes the answer expire - and nothing but the key can bound it. A registration that
        // widened the lifetime (AddSingleton is the one-word change the Known limits invite) would
        // otherwise let a 09:00 permit answer for that triple across every rollover after it.
        var clock = new MovableTimeProvider(MidMorning);
        var sut = new PatientRelationshipAuthorizer(
            _fhirClient, new ClinicClock(clock, Options.Create(new ClinicOptions())), _logger);
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-18", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment(Patient, $"Practitioner/{Clinician}")]));
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-19", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([]));

        var today = await sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);
        clock.UtcNow = MidMorning.AddDays(1);
        var tomorrow = await sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        today.IsRelated.Should().BeTrue();
        tomorrow.IsRelated.Should().BeFalse(
            "a permit derived from one clinic day's calendar must not answer for the next");
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-19", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AuthorizeAsync_TheClinicDayTurnsWhileOneLookupIsBeingResolved_SearchesTheDayItsMemoKeyCarries()
    {
        // The clinic day in the memo key has to be the *same* day the answer was resolved from, or
        // the memo bounds nothing: a decision derived from one calendar would be filed under
        // another, and the key that is supposed to expire it would go on serving it. Keeping the
        // day in the key while letting ResolveAsync read the clock again is a green refactor
        // today - the two reads only differ when one lands the far side of midnight, which is what
        // this clock does. A separate change finding 2.
        var clock = new TurningTimeProvider(MidMorning, MidMorning.AddDays(1));
        var sut = new PatientRelationshipAuthorizer(
            _fhirClient, new ClinicClock(clock, Options.Create(new ClinicOptions())), _logger);
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-18", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment(Patient, $"Practitioner/{Clinician}")]));
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-19", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([]));

        var decision = await sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-18", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(Site, "eq2026-09-19", A<CancellationToken>._))
            .MustNotHaveHappened();
        decision.IsRelated.Should().BeTrue(
            "the answer filed under the 18th must be the 18th's calendar, not whatever day the clock had reached");
    }

    private PatientRelationshipAuthorizer BuildSut(DateTimeOffset now) => new(
        _fhirClient,
        new ClinicClock(new FixedTimeProvider(now), Options.Create(new ClinicOptions())),
        _logger);

    private void GiveAppointments(params AppointmentRecord[] appointments) =>
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(appointments));

    private static AppointmentRecord Appointment(string patientId, string providerActorReference) =>
        new(new ClinicalSourceRef("Appointment", "appt-1"), patientId, providerActorReference, "booked", DateTimeOffset.UtcNow);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A clock a test can wind forward - the clinic day rolls when this says it does, never on wall time.</summary>
    private sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>
    /// A clock that turns the page the instant it is read a second time: the first read gets
    /// <paramref name="before"/>, every read after it gets <paramref name="after"/>. Reading the
    /// clinic day once and reading it twice are indistinguishable on any other clock, which is why
    /// this one exists.
    /// </summary>
    private sealed class TurningTimeProvider(DateTimeOffset before, DateTimeOffset after) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1 ? before : after;
    }

    [Fact]
    public async Task AuthorizeAsync_TheRequestersTokenHasExpired_PropagatesRatherThanRefusingAsUnrelated()
    {
        // This gate reads Appointment with the requester's own token, so an expired
        // session reaches it first - and swallowing that into "unresolved" told the clinician
        // there was no care relationship to their own patient, and wrote an ACCESS AUDIT refusal
        // saying so. An expired session is not an authorization decision; it must surface as
        // itself, the way cancellation already does.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new AccessTokenExpiredException("expired"));

        var act = () => _sut.AuthorizeAsync(Site, Clinician, Patient, CancellationToken.None);

        await act.Should().ThrowAsync<AccessTokenExpiredException>();
        _logger.Lines.Should().BeEmpty("an expired session is not a refusal and must not be audited as one");
    }

    [Fact]
    public async Task AuthorizeAsync_TheCallerCancelled_PropagatesRatherThanRefusingAsUnrelated()
    {
        // The catch filter's other exclusion, and the one nothing held it to - the case above says
        // "the way cancellation already does" and, until this test, that was an assertion about
        // code rather than a test of it. Same failure shape as a separate change if it regressed: an abandoned
        // turn would be audited as an FR-AUTH-4 refusal against the clinician's own patient.
        using var abandoned = new CancellationTokenSource();
        await abandoned.CancelAsync();
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new OperationCanceledException(abandoned.Token));

        var act = () => _sut.AuthorizeAsync(Site, Clinician, Patient, abandoned.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _logger.Lines.Should().BeEmpty("a cancelled request decided nothing and must not be audited as a refusal");
    }
}
