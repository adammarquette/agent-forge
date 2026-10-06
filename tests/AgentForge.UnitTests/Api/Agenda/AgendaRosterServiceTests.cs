using System.Globalization;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Api.Agenda;
using AgentForge.Api.Chat;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Agenda;

public sealed class AgendaRosterServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly IAgendaPatientSummaryRunner _summaryRunner = A.Fake<IAgendaPatientSummaryRunner>();
    private readonly IScopedAccessTokenProvider _tokenProvider = A.Fake<IScopedAccessTokenProvider>();
    private readonly IConversationTurnBudget _turnBudget = A.Fake<IConversationTurnBudget>();
    private readonly AgendaSessionContext _session = new("agenda-token", "default", "dr-jones", Now.AddHours(1));
    private readonly SettableTimeProvider _time = new(Now);
    private readonly InMemoryAgendaSummaryCache _summaryCache;

    private AgendaRosterService BuildSut(int maxConcurrentSummaries = 4, ILogger<AgendaRosterService>? logger = null) => new(
        _fhirClient, _summaryRunner, _tokenProvider, _turnBudget, _summaryCache,
        new ClinicClock(_time, Options.Create(new ClinicOptions())),
        Options.Create(new AgendaOptions { MaxConcurrentSummaries = maxConcurrentSummaries }),
        logger ?? NullLogger<AgendaRosterService>.Instance);

    public AgendaRosterServiceTests()
    {
        // A real cache (a singleton in the host), shared by every BuildSut() so a second load is a reload.
        _summaryCache = new InMemoryAgendaSummaryCache(Options.Create(new AgendaOptions()), _time);
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).Returns(true);
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string site, string patientId, string _, CancellationToken _) =>
                Task.FromResult(new AgentTurnResult($"summary for {patientId}", ConversationState.Start(site, patientId), [], [])));
    }

    private static AppointmentRecord Appointment(string id, string patientId, DateTimeOffset? start, string status = "booked", string provider = "dr-jones") =>
        new(new ClinicalSourceRef("Appointment", id), patientId, $"Practitioner/{provider}", status, start);

    [Fact]
    public async Task BuildAgendaAsync_MultipleAppointmentsOutOfOrder_ReturnsRowsOrderedByScheduledStart()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "patient-late", Now.AddHours(3)),
                Appointment("2", "patient-soon", Now.AddMinutes(30)),
                Appointment("3", "patient-mid", Now.AddHours(1)),
            ]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Select(r => r.PatientId).Should().Equal("patient-soon", "patient-mid", "patient-late");
    }

    [Fact]
    public async Task BuildAgendaAsync_AppointmentNotForThisProvider_ExcludesIt()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "mine", Now.AddHours(1), provider: "dr-jones"),
                Appointment("2", "not-mine", Now.AddHours(1), provider: "dr-someone-else"),
            ]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.PatientId.Should().Be("mine");
    }

    [Fact]
    public async Task BuildAgendaAsync_AppointmentAtOrBeforeNow_ExcludesIt()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "already-seen", Now.AddHours(-1)),
                Appointment("2", "right-now", Now),
                Appointment("3", "still-upcoming", Now.AddMinutes(1)),
            ]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.PatientId.Should().Be("still-upcoming");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("noshow")]
    [InlineData("entered-in-error")]
    public async Task BuildAgendaAsync_CancelledNoShowOrEnteredInErrorAppointment_ExcludesIt(string status)
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
                [Appointment("1", "patient-1", Now.AddHours(1), status: status)]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAgendaAsync_AppointmentWithNoPatientId_ExcludesIt()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
                [new AppointmentRecord(new ClinicalSourceRef("Appointment", "1"), null, "Practitioner/dr-jones", "booked", Now.AddHours(1))]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildAgendaAsync_OnePatientsSummaryThrows_OtherRowsStillSucceedAndTheFailedRowIsMarked()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "patient-ok-1", Now.AddMinutes(10)),
                Appointment("2", "patient-fails", Now.AddMinutes(20)),
                Appointment("3", "patient-ok-2", Now.AddMinutes(30)),
            ]));
        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-fails", "dr-jones", A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("tool dispatch failed"));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().HaveCount(3);
        result.Rows.Should().Contain(r => r.PatientId == "patient-ok-1" && !r.Failed && r.Summary == "summary for patient-ok-1");
        result.Rows.Should().Contain(r => r.PatientId == "patient-ok-2" && !r.Failed && r.Summary == "summary for patient-ok-2");
        var failedRow = result.Rows.Single(r => r.PatientId == "patient-fails");
        failedRow.Failed.Should().BeTrue();
        failedRow.Summary.Should().BeNull();
        failedRow.FailureReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BuildAgendaAsync_OnePatientsSummaryThrows_DiagnosticLineNamesTheRowAndExceptionTypeButNotThePatient()
    {
        // NFR-SEC-1 / CONVENTIONS.md §7: diagnostic lines carry no patient id - and an exception message
        // is a way for one to arrive, since a failed FHIR call can name the resource it failed on. A separate change
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "patient-ok", Now.AddMinutes(10)),
                Appointment("2", "patient-fails", Now.AddMinutes(20)),
            ]));
        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-fails", "dr-jones", A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("GET /fhir/Patient/patient-fails/Observation failed"));
        var logger = new CapturingLogger<AgendaRosterService>();

        await BuildSut(logger: logger).BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var line = logger.Lines.Should().ContainSingle().Subject;
        line.Should().NotContain("patient-fails").And.Contain("row 2 of 2").And.Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task BuildAgendaAsync_PatientNameLookupThrows_DiagnosticLineDoesNotNameThePatient()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment("1", "patient-unnamed", Now.AddMinutes(10))]));
        A.CallTo(() => _fhirClient.GetPatientAsync("default", "patient-unnamed", A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("GET /fhir/Patient/patient-unnamed returned 500"));
        var logger = new CapturingLogger<AgendaRosterService>();

        await BuildSut(logger: logger).BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var line = logger.Lines.Should().ContainSingle().Subject;
        line.Should().NotContain("patient-unnamed").And.Contain(nameof(HttpRequestException));
    }

    [Fact]
    public async Task BuildAgendaAsync_Always_SetsTheAccessTokenOnceBeforeFanningOut()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
                [Appointment("1", "patient-1", Now.AddMinutes(10))]));

        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _tokenProvider.Adopt("agenda-token", _session.ExpiresAt)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BuildAgendaAsync_Always_ReturnsAsOfEqualToTheCapturedNow()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([]));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.AsOf.Should().Be(Now);
    }

    [Fact]
    public async Task BuildAgendaAsync_MorePatientsThanTheConcurrencyBound_NeverRunsMoreThanTheBoundConcurrently()
    {
        const int concurrencyBound = 2;
        var current = 0;
        var maxObserved = 0;
        var gate = new Lock();
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily(async (string site, string patientId, string _, CancellationToken ct) =>
            {
                lock (gate)
                {
                    current++;
                    maxObserved = Math.Max(maxObserved, current);
                }

                await Task.Delay(50, ct);

                lock (gate)
                {
                    current--;
                }

                return new AgentTurnResult($"summary for {patientId}", ConversationState.Start(site, patientId), [], []);
            });
        var appointments = Enumerable.Range(1, 6)
            .Select(i => Appointment(i.ToString(CultureInfo.InvariantCulture), $"patient-{i.ToString(CultureInfo.InvariantCulture)}", Now.AddMinutes(i)))
            .ToArray();
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(appointments));

        await BuildSut(concurrencyBound).BuildAgendaAsync("session-1", _session, CancellationToken.None);

        maxObserved.Should().BeLessThanOrEqualTo(concurrencyBound);
    }

    [Fact]
    public async Task BuildAgendaAsync_Always_ResolvesEachRowsPatientDisplayNameFromDemographics()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment("1", "patient-1", Now.AddMinutes(30))]));
        A.CallTo(() => _fhirClient.GetPatientAsync("default", "patient-1", A<CancellationToken>._))
            .Returns(Task.FromResult<PatientRecord?>(new PatientRecord(new ClinicalSourceRef("Patient", "patient-1"), "Jane Roe", null, null)));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.DisplayName.Should().Be("Jane Roe");
    }

    [Fact]
    public async Task BuildAgendaAsync_PatientDemographicsReadThrows_DisplayNameDegradesToNullWithoutFailingTheRow()
    {
        // A name lookup failure must not blank the whole row - the summary still shows, and the UI
        // falls back to "Patient <id>" for the missing name.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment("1", "patient-1", Now.AddMinutes(30))]));
        A.CallTo(() => _fhirClient.GetPatientAsync("default", "patient-1", A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("FHIR 500"));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var row = result.Rows.Should().ContainSingle().Subject;
        row.DisplayName.Should().BeNull();
        row.Failed.Should().BeFalse();
        row.Summary.Should().Be("summary for patient-1");
    }

    [Fact]
    public async Task BuildAgendaAsync_EachPatientsSummary_ChargesTheSessionsBudgetOnce()
    {
        // The agenda is the widest LLM fan-out in the product - one summary turn per rostered patient on every
        // load - so each summary is charged to the session like a chat turn. A separate change
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "patient-1", Now.AddMinutes(10)),
                Appointment("2", "patient-2", Now.AddMinutes(20)),
                Appointment("3", "patient-3", Now.AddMinutes(30)),
            ]));

        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _turnBudget.TryConsume("session-1")).MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task BuildAgendaAsync_SessionBudgetExhausted_MarksTheRowsFailedWithoutRunningAnySummary()
    {
        // Reloading the agenda in a loop must stop billing once the session's budget is spent; the roster itself
        // still renders, so the clinician can see who is next and why there is no summary.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                Appointment("1", "patient-1", Now.AddMinutes(10)),
                Appointment("2", "patient-2", Now.AddMinutes(20)),
            ]));
        A.CallTo(() => _turnBudget.TryConsume("session-1")).Returns(false);

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Failed && r.Summary == null
            && r.FailureReason == AgendaRosterService.BudgetExhaustedReason);
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task BuildAgendaAsync_SecondLoadWithinTheTtl_MakesNoLlmCallAndNoCharge()
    {
        // A reload (Back, refresh, return after a visit) re-serves the first load's summaries instead of
        // re-running and re-charging every remaining patient. A separate change
        StubRoster(Appointment("1", "patient-1", Now.AddMinutes(40)), Appointment("2", "patient-2", Now.AddMinutes(50)));
        var first = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);
        Fake.ClearRecordedCalls(_summaryRunner);
        Fake.ClearRecordedCalls(_turnBudget);
        _time.Advance(TimeSpan.FromMinutes(10));

        var second = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).MustNotHaveHappened();
        second.Rows.Select(r => (r.PatientId, r.Summary, r.Failed))
            .Should().Equal(first.Rows.Select(r => (r.PatientId, r.Summary, r.Failed)));
        second.Rows.Select(r => r.Summary).Should().Equal("summary for patient-1", "summary for patient-2");
    }

    [Fact]
    public async Task BuildAgendaAsync_FreshSummary_CarriesThisLoadsInstantAsItsSummaryAsOf()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));

        var result = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().ContainSingle().Which.SummaryAsOf.Should().Be(Now);
    }

    [Fact]
    public async Task BuildAgendaAsync_CachedSummaryOnReload_CarriesTheFirstLoadsInstantNotTheReloads()
    {
        // The page's AsOf is the reload; a re-served summary must say it is older, or it reads as current.
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(20));

        var second = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        second.AsOf.Should().Be(Now.AddMinutes(20));
        second.Rows.Should().ContainSingle().Which.SummaryAsOf.Should().Be(Now);
    }

    [Fact]
    public async Task BuildAgendaAsync_FailedOrBudgetRefusedRow_CarriesNoSummaryAsOf()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)), Appointment("2", "patient-2", Now.AddHours(2)));
        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-1", A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("boom"));
        A.CallTo(() => _turnBudget.TryConsume("session-1")).ReturnsNextFromSequence(true, false);

        var result = await BuildSut(maxConcurrentSummaries: 1).BuildAgendaAsync("session-1", _session, CancellationToken.None);

        result.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Failed && r.SummaryAsOf == null);
    }

    [Fact]
    public async Task BuildAgendaAsync_ReloadAtTheTtl_RegeneratesAndChargesAgain()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(2)));
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);
        _time.Advance(new AgendaOptions().SummaryCacheTtl);

        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-1", "dr-jones", A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
        A.CallTo(() => _turnBudget.TryConsume("session-1")).MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task BuildAgendaAsync_AppointmentReassignedToAnotherClinician_DoesNotReceiveTheFirstCliniciansCachedSummary()
    {
        // Same appointment id, reassigned mid-day: the summary is regenerated under the new clinician's identity.
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1), provider: "dr-smith"));

        await BuildSut().BuildAgendaAsync("session-2", _session with { ClinicianIdentity = "dr-smith" }, CancellationToken.None);

        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-1", "dr-smith", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _turnBudget.TryConsume("session-2")).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BuildAgendaAsync_SamePatientAtAnotherSite_DoesNotReceiveTheFirstSitesCachedSummary()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("other-site", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([Appointment("1", "patient-1", Now.AddHours(1))]));
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        await BuildSut().BuildAgendaAsync("session-1", _session with { Site = "other-site" }, CancellationToken.None);

        A.CallTo(() => _summaryRunner.RunAsync("other-site", "patient-1", "dr-jones", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task BuildAgendaAsync_SamePatientUnderAnotherAppointment_DoesNotReceiveTheFirstAppointmentsCachedSummary()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);
        StubRoster(Appointment("2", "patient-1", Now.AddHours(2)));

        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _summaryRunner.RunAsync("default", "patient-1", "dr-jones", A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task BuildAgendaAsync_FailedSummary_IsNotCachedSoTheReloadRetriesIt()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("boom")).Once();
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var second = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        second.Rows.Should().ContainSingle().Which.Summary.Should().Be("summary for patient-1");
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task BuildAgendaAsync_DeterministicFallbackSummary_IsNotCachedSoTheReloadRetriesIt()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult(
                "fallback", ConversationState.Start("default", "patient-1"), [], [], IsDeterministicFallback: true)))
            .Once();
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var second = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        second.Rows.Should().ContainSingle().Which.Summary.Should().Be("summary for patient-1");
        A.CallTo(() => _summaryRunner.RunAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
            .MustHaveHappened(2, Times.Exactly);
    }

    [Fact]
    public async Task BuildAgendaAsync_BudgetRefusedRow_IsNotCachedSoALaterLoadGeneratesIt()
    {
        StubRoster(Appointment("1", "patient-1", Now.AddHours(1)));
        A.CallTo(() => _turnBudget.TryConsume("session-1")).Returns(false).Once();
        await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        var second = await BuildSut().BuildAgendaAsync("session-1", _session, CancellationToken.None);

        second.Rows.Should().ContainSingle().Which.Summary.Should().Be("summary for patient-1");
    }

    private void StubRoster(params AppointmentRecord[] appointments) =>
        A.CallTo(() => _fhirClient.GetAppointmentsAsync("default", "eq2026-07-11", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(appointments));

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
