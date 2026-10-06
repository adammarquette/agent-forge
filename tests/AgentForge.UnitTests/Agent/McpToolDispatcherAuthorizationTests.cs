using System.Text.Json;
using AgentForge.Agent;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FakeItEasy.Core;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Agent;

/// <summary>
/// FR-AUTH-2's acceptance criterion, exercised where FR-AUTH-3 requires it to hold - in the tool
/// layer, below the model: <em>identical query, two roles → correctly different results or a
/// refusal</em>. Deliberately drives the real <see cref="PatientRelationshipAuthorizer"/> over a
/// faked FHIR client rather than a faked authorizer, so the rule itself is what is under test and
/// not a restatement of the mock.
/// </summary>
/// <remarks>
/// The per-tool theories pin the other half of the claim: the gate covers <em>every</em> dispatch,
/// not only the six FHIR tools behind <see cref="IMcpToolServer"/>. They enumerate
/// <see cref="McpToolCatalog.AllTools"/> rather than a hand-copied list, so a tool added later is
/// either covered automatically or reddens the suite. A separate change.
/// </remarks>
public sealed class McpToolDispatcherAuthorizationTests
{
    private const string Site = "default";
    private const string ProviderOfRecord = "dr-cardio";
    private const string UnrelatedUser = "admin";
    private const string Patient = "patient-123";

    // A fixed instant rather than TimeProvider.System: every FHIR arrangement here matches the date
    // with a wildcard today, so a real clock is invisible right up until someone tightens one.
    private static readonly DateTimeOffset MidMorning = new(2026, 9, 18, 14, 30, 0, TimeSpan.Zero);

    private static readonly LlmToolCall TheSameQuery = new("call_1", "get_patient_summary", "{}");

    /// <summary>
    /// Arguments satisfying each tool's schema, so the permit half of each theory reaches the tool
    /// instead of dying on a contract failure. Checked against the catalog on every case: a tool
    /// added to <see cref="McpToolCatalog.AllTools"/> with no entry here fails the suite rather
    /// than quietly going unproven.
    /// </summary>
    private static readonly Dictionary<string, string> ArgumentsByTool = new(StringComparer.Ordinal)
    {
        ["get_patient_summary"] = "{}",
        ["get_interval_changes"] = """{"since_date":"ge2026-01-01"}""",
        ["get_labs"] = "{}",
        ["get_vitals"] = "{}",
        ["get_recent_encounters"] = "{}",
        ["get_documents"] = "{}",
        ["get_document_facts"] = "{}",
        ["retrieve_evidence"] = """{"query":"target INR for atrial fibrillation on warfarin"}""",
    };

    private readonly IMcpToolServer _toolServer = A.Fake<IMcpToolServer>();
    private readonly IDocumentFactsTool _documentFactsTool = A.Fake<IDocumentFactsTool>();
    private readonly IEvidenceTool _evidenceTool = A.Fake<IEvidenceTool>();
    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly IClinicianIdentityAccessor _clinicianIdentityAccessor = A.Fake<IClinicianIdentityAccessor>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly CapturingLogger<McpToolDispatcher> _logger = new();
    private readonly CapturingLogger<AccessAudit> _auditLogger = new();

    public McpToolDispatcherAuthorizationTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        A.CallTo(() => _toolServer.GetPatientSummaryAsync(A<GetPatientSummaryRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientSummaryResult(null, [], [], [])));
        A.CallTo(() => _documentFactsTool.GetAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new DocumentFactsResult([])));
        A.CallTo(() => _evidenceTool.GetAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new EvidenceResult([])));
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>(
            [
                new AppointmentRecord(
                    new ClinicalSourceRef("Appointment", "appt-1"), Patient, $"Practitioner/{ProviderOfRecord}",
                    "booked", DateTimeOffset.UtcNow),
            ]));
    }

    /// <summary>Every tool the model may be offered - read off the catalog, never hand-copied.</summary>
    public static TheoryData<string> EveryDispatchableTool
    {
        get
        {
            var tools = new TheoryData<string>();
            foreach (var tool in McpToolCatalog.AllTools)
            {
                tools.Add(tool.Name);
            }

            return tools;
        }
    }

    [Theory]
    [MemberData(nameof(EveryDispatchableTool))]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_RefusesEveryToolBeforeItRuns(string toolName)
    {
        // The gate is claimed for every dispatch, "not only the six behind IMcpToolServer", and the
        // two that are not behind it are where that matters most: get_document_facts reads the same
        // derived-fact store whose other reader was a live cross-patient disclosure, and bypasses
        // the MCP server's audit choke point - so for it this gate is the only line there is.
        var result = await DispatchAs(UnrelatedUser, toolName);

        result.IsError.Should().BeTrue();
        JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("error").GetString()
            .Should().Be(PatientAccessRefusal.UserFacingMessage);
        BackingCalls().Should().BeEmpty("a refusal must prevent the read, not withhold its result");
    }

    [Theory]
    [MemberData(nameof(EveryDispatchableTool))]
    public async Task DispatchAsync_RequesterIsThePatientsProviderOfRecord_StillRunsEveryTool(string toolName)
    {
        // The other direction, per tool: a gate that refuses everything passes every refusal test.
        var result = await DispatchAs(ProviderOfRecord, toolName);

        result.IsError.Should().BeFalse();
        BackingCalls().Should().NotBeEmpty("a permitted dispatch must reach the tool it names");
    }

    [Fact]
    public async Task DispatchAsync_RequesterIsThePatientsProviderOfRecord_ReturnsTheToolsData()
    {
        var result = await DispatchAs(ProviderOfRecord);

        result.IsError.Should().BeFalse();
        A.CallTo(() => _toolServer.GetPatientSummaryAsync(A<GetPatientSummaryRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_RefusesWithoutRunningTheTool()
    {
        // as observed: `admin` opened a chart and briefed on it. The refusal has
        // to happen before the tool runs, or the data is already read whatever the model is told.
        var result = await DispatchAs(UnrelatedUser);

        result.IsError.Should().BeTrue();
        A.CallTo(_toolServer).MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_LeaksNoClinicalDetailInTheRefusal()
    {
        var result = await DispatchAs(UnrelatedUser);

        var error = JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("error").GetString();
        error.Should().Be(PatientAccessRefusal.UserFacingMessage);
        result.ResultJson.Should().NotContain(Patient, "a refusal must not echo back who was asked about");
    }

    [Fact]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_RecordsTheRefusalInTheAccessAuditTrail()
    {
        // FR-AUTH-4: a denied access attempt is an audit event in its own right - who, what and
        // why - not a silent no-op (REQUIREMENTS.md §11's "Denied access attempt" row).
        await DispatchAs(UnrelatedUser);

        _auditLogger.Lines.Should().ContainSingle(line =>
            line.Contains(UnrelatedUser, StringComparison.Ordinal) &&
            line.Contains(Patient, StringComparison.Ordinal) &&
            line.Contains("get_patient_summary", StringComparison.Ordinal) &&
            line.Contains("clinicDayAppointments=1", StringComparison.Ordinal) &&
            line.Contains("corr-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_DoesNotCountTheRefusalAsAToolFailure()
    {
        // A refusal is the system working. Routing it into the tool-failure rate would page an
        // operator for correct behaviour (FR-OBS-4) - claimed in two comments, asserted here.
        await DispatchAs(UnrelatedUser);

        A.CallTo(() => _metrics.RecordToolCall(A<string>._, A<bool>._, A<TimeSpan>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_RequesterIsThePatientsProviderOfRecord_StillCountsTheToolCall()
    {
        // The other half: skipping the metric must be specific to the refusal, not a hole.
        await DispatchAs(ProviderOfRecord);

        A.CallTo(() => _metrics.RecordToolCall("get_patient_summary", true, A<TimeSpan>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DispatchAsync_RequesterHasNoRelationshipToThePatient_MetersTheRefusalOutsideTheToolCallSeries()
    {
        // THE pin. A refusal must be counted - M3's live story is otherwise invisible -
        // and counted *somewhere that is not the tool-call series*, because the tool-failure rate
        // feeds AgentForgeHighToolFailureRate and would page an operator for correct behaviour
        // (FR-OBS-4). Asserting "no other metric call at all" rather than naming RecordToolCall is
        // deliberate: a later change that routes the refusal into any existing series - agent
        // turns, verification, a future one - reddens this test rather than shipping a pager.
        await DispatchAs(UnrelatedUser);

        A.CallTo(() => _metrics.RecordAuthorizationDecision(false, AuthorizationDecisionReason.NoClinicalRelationship))
            .MustHaveHappenedOnceExactly();
        A.CallTo(_metrics)
            .Where(call => call.Method.Name != nameof(IAgentForgeMetrics.RecordAuthorizationDecision))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_RequesterIsThePatientsProviderOfRecord_MetersThePermitWithoutPerturbingTheToolCallSeries()
    {
        // The permit branch is metered too (both branches, or the series counts refusals only and
        // the permit rate is unreadable) - and the tool-call series still sees exactly one call.
        await DispatchAs(ProviderOfRecord);

        A.CallTo(() => _metrics.RecordAuthorizationDecision(true, AuthorizationDecisionReason.ClinicalRelationship))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _metrics.RecordToolCall(A<string>._, A<bool>._, A<TimeSpan>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DispatchAsync_SessionTokenHasExpired_RecordsNoAuthorizationDecisionAtAll()
    {
        // a separate change stopped the authorizer swallowing an expired token: it now throws past this
        // branch's counter instead of returning Unresolved. Three places assert the consequence -
        // METRICS.md §3, panel 20's description, and the comment at the call site - and until
        // this test nothing held the code to it. Re-wrapping that await is a plausible edit, since
        // the exception is otherwise uncaught in DispatchAsync, and it would chart a clinician's
        // aged-out token as a refusal against their own patient: the clinical bug re-expressed
        // in telemetry, on the panel an operator reads.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new AccessTokenExpiredException("the session's access token has expired"));

        Func<Task> dispatch = () => DispatchAs(ProviderOfRecord);

        await dispatch.Should().ThrowAsync<AccessTokenExpiredException>();

        // No metric call AT ALL rather than merely no authorization decision, so a later change
        // routing an expired session into *any* series reddens here - the same move as the
        // refusal-exclusion pin above.
        A.CallTo(_metrics).MustNotHaveHappened();

        // Nor audited as a refusal: that is the same falsehood written to the trail an entitlement
        // review reads, which is the half of the bug that was not about a 401.
        _auditLogger.Lines.Should().NotContain(line => line.Contains("REFUSED", StringComparison.Ordinal));
        BackingCalls().Should().BeEmpty("an unanswerable session must not reach the tool either");
    }

    [Fact]
    public async Task DispatchAsync_RelationshipCannotBeResolved_MetersTheRefusalAsUnresolvedRatherThanUnrelated()
    {
        // The same distinction the audit line draws, on the dashboard: an OpenEMR outage is an
        // operations problem, a real "not your patient" is the gate working.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("OpenEMR unreachable"));

        await DispatchAs(ProviderOfRecord);

        A.CallTo(() => _metrics.RecordAuthorizationDecision(false, AuthorizationDecisionReason.RelationshipUnresolved))
            .MustHaveHappenedOnceExactly();
    }

    [Theory]
    [MemberData(nameof(EveryDispatchableTool))]
    public async Task DispatchAsync_AnyToolAndEitherOutcome_TagsTheDecisionOnlyWithBoundedReasons(string toolName)
    {
        // No PHI in telemetry: `reason` is an exported label, so it may never carry the patient,
        // the requester, the tool or any count. Pinned across every tool and both outcomes.
        await DispatchAs(UnrelatedUser, toolName);
        await DispatchAs(ProviderOfRecord, toolName);

        var reasons = Fake.GetCalls(_metrics)
            .Where(call => call.Method.Name == nameof(IAgentForgeMetrics.RecordAuthorizationDecision))
            .Select(call => (string)call.Arguments[1]!)
            .ToList();

        reasons.Should().HaveCount(2);
        reasons.Should().OnlyContain(reason => AuthorizationDecisionReason.All.Contains(reason));
        reasons.Should().NotContain(reason =>
            reason.Contains(Patient, StringComparison.Ordinal) ||
            reason.Contains(UnrelatedUser, StringComparison.Ordinal) ||
            reason.Contains(ProviderOfRecord, StringComparison.Ordinal) ||
            reason.Contains(toolName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_ClinicDayIsEmpty_RefusesAndSaysSoOnTheAuditLine()
    {
        // An aged-out demo seed refuses exactly like a genuine "not your patient"; the count is the
        // only thing that tells an operator which one they are looking at.
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AppointmentRecord>>([]));

        var result = await DispatchAs(ProviderOfRecord);

        result.IsError.Should().BeTrue();
        _auditLogger.Lines.Should().ContainSingle(line => line.Contains("clinicDayAppointments=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_RelationshipCannotBeResolved_SaysSoOnTheAuditLineRatherThanClaimingZero()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("OpenEMR unreachable"));

        await DispatchAs(ProviderOfRecord);

        _auditLogger.Lines.Should().ContainSingle(line =>
            line.Contains("clinicDayAppointments=unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchAsync_NoClinicianIdentityInScope_RefusesRatherThanRunningUnattributed()
    {
        var result = await DispatchAs(null);

        result.IsError.Should().BeTrue();
        A.CallTo(_toolServer).MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_RelationshipCannotBeResolved_RefusesRatherThanFallingOpen()
    {
        A.CallTo(() => _fhirClient.GetAppointmentsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Throws(new HttpRequestException("OpenEMR unreachable"));

        var result = await DispatchAs(ProviderOfRecord);

        result.IsError.Should().BeTrue();
        A.CallTo(_toolServer).MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_ModelInjectsAnotherPatientIdIntoTheArguments_StillDecidesOnTheSessionPatient()
    {
        // The dispatcher already forces site/patientId from the session (FR-CHAT-3); this proves
        // the new authorization decision is made on that forced patient too, so an injected id
        // cannot be used to get a permit for one patient and data for another.
        var result = await DispatcherFor(ProviderOfRecord).DispatchAsync(
            Site, Patient, new LlmToolCall("call_2", "get_patient_summary", """{"patientId":"patient-999"}"""),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        A.CallTo(() => _toolServer.GetPatientSummaryAsync(
                A<GetPatientSummaryRequest>.That.Matches(r => r.PatientId == Patient), A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    private static string ArgumentsFor(string toolName)
    {
        ArgumentsByTool.Should().ContainKey(
            toolName,
            "a tool added to McpToolCatalog needs arguments here, or the gate goes unproven for it");
        return ArgumentsByTool[toolName];
    }

    private Task<LlmToolResultContent> DispatchAs(string? clinicianIdentity) =>
        DispatcherFor(clinicianIdentity).DispatchAsync(Site, Patient, TheSameQuery, CancellationToken.None);

    private Task<LlmToolResultContent> DispatchAs(string? clinicianIdentity, string toolName) =>
        DispatcherFor(clinicianIdentity).DispatchAsync(
            Site, Patient, new LlmToolCall("call_1", toolName, ArgumentsFor(toolName)), CancellationToken.None);

    /// <summary>Every call that actually read patient data, across all three backing tool surfaces.</summary>
    private IEnumerable<ICompletedFakeObjectCall> BackingCalls() =>
        Fake.GetCalls(_toolServer).Concat(Fake.GetCalls(_documentFactsTool)).Concat(Fake.GetCalls(_evidenceTool));

    private McpToolDispatcher DispatcherFor(string? clinicianIdentity)
    {
        A.CallTo(() => _clinicianIdentityAccessor.ClinicianIdentity).Returns(clinicianIdentity);
        return new McpToolDispatcher(
            _toolServer,
            new PatientRelationshipAuthorizer(
                _fhirClient,
                new ClinicClock(new FixedTimeProvider(MidMorning), Options.Create(new ClinicOptions())),
                new CapturingLogger<PatientRelationshipAuthorizer>()),
            _clinicianIdentityAccessor,
            _correlationIdAccessor,
            _metrics,
            _logger,
            _auditLogger,
            _documentFactsTool,
            _evidenceTool);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
