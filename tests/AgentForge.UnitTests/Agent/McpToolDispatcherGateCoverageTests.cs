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
/// What makes FR-AUTH-2's "every dispatch is checked" (<c>ARCHITECTURE.md</c> §5.7) hold for a tool
/// that does not exist yet. <c>McpToolDispatcherAuthorizationTests</c> proves both directions of the
/// gate for every tool in <see cref="McpToolCatalog"/>, but its coverage set <em>is</em> the catalog:
/// a name routable by <c>ExecuteAsync</c> and absent from the catalog is a name that suite never
/// dispatches. These cases pin the property that makes the catalog's completeness stop mattering -
/// the gate is decided <em>before</em> the name is routed, so it covers names nobody has listed
/// anywhere, and the coverage claim survives the next tool rather than depending on it being added
/// in two places. A separate change finding 1.
/// <para>
/// And one case holds the router to the catalog in both directions, read off <c>ExecuteAsync</c>'s
/// own switch, so a tool added to either list alone fails here - including an arm the catalog does
/// not offer, which no dispatch can observe. A separate change.
/// </para>
/// </summary>
public sealed class McpToolDispatcherGateCoverageTests
{
    private const string Site = "default";
    private const string ProviderOfRecord = "dr-cardio";
    private const string UnrelatedUser = "admin";
    private const string Patient = "patient-123";

    /// <summary>
    /// A name no catalog entry and no <c>ExecuteAsync</c> switch arm carries - the stand-in for the
    /// tool someone adds tomorrow. Deliberately not spelled like a real tool: the point is that the
    /// gate never consults a list of names at all.
    /// </summary>
    private const string ToolTheCatalogDoesNotList = "get_tool_added_after_this_test_was_written";

    private static readonly DateTimeOffset MidMorning = new(2026, 9, 18, 14, 30, 0, TimeSpan.Zero);

    private readonly IMcpToolServer _toolServer = A.Fake<IMcpToolServer>();
    private readonly IDocumentFactsTool _documentFactsTool = A.Fake<IDocumentFactsTool>();
    private readonly IEvidenceTool _evidenceTool = A.Fake<IEvidenceTool>();
    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly IClinicianIdentityAccessor _clinicianIdentityAccessor = A.Fake<IClinicianIdentityAccessor>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly CapturingLogger<McpToolDispatcher> _logger = new();
    private readonly CapturingLogger<AccessAudit> _auditLogger = new();

    public McpToolDispatcherGateCoverageTests()
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

    /// <summary>Every tool the catalog offers the model - read off the catalog, never hand-copied.</summary>
    public static TheoryData<string> EveryCatalogTool
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

    [Fact]
    public async Task DispatchAsync_ToolNameTheCatalogDoesNotList_IsRefusedByTheGateAndNotByTheRouter()
    {
        // The whole of finding 1: a tool added to ExecuteAsync's switch and left out of the catalog
        // is dispatched by nothing in the enumerated suite. It is still gated, because the decision
        // is taken before the name is looked at - so the refusal a stranger gets for a name that
        // does not exist is the FR-AUTH-2 refusal, word for word, not "unknown tool".
        var result = await DispatchAs(UnrelatedUser, ToolTheCatalogDoesNotList);

        result.IsError.Should().BeTrue();
        ErrorOf(result).Should().Be(
            PatientAccessRefusal.UserFacingMessage,
            "an unlisted tool must be refused by the relationship gate, not merely fail to route");
        BackingCalls().Should().BeEmpty("a refusal must prevent the read, not withhold its result");
    }

    [Fact]
    public async Task DispatchAsync_ToolNameTheCatalogDoesNotList_RefusesARelatedRequesterForADifferentReason()
    {
        // The control that stops the case above passing for the wrong reason. If an unlisted name
        // were rejected because nothing can serve it rather than because the requester is a stranger,
        // the provider of record would be turned away identically - and this case would go red.
        //
        // What rejects it past the gate moved: the NG1 scope gate now refuses an unoffered
        // name before ExecuteAsync is consulted, where the router's "Unknown tool" used to. The
        // control is unchanged in substance - the provider of record still gets a different refusal
        // from the stranger, which is the only thing this case is for.
        var result = await DispatchAs(ProviderOfRecord, ToolTheCatalogDoesNotList);

        result.IsError.Should().BeTrue();
        ErrorOf(result).Should().NotBe(
            PatientAccessRefusal.UserFacingMessage,
            "the gate must have let the provider of record through");
        ErrorOf(result).Should().Be(
            McpToolCatalog.OutOfScopeRefusalMessage,
            "and the scope gate is what should then have refused a name the catalog does not offer");
    }

    [Fact]
    public async Task DispatchAsync_ToolNameTheCatalogDoesNotList_RecordsTheRefusalAgainstThatName()
    {
        // FR-AUTH-4 does not get to lapse for a tool nobody listed: the audit line names whatever
        // tool was attempted, not whatever tool is on a list.
        await DispatchAs(UnrelatedUser, ToolTheCatalogDoesNotList);

        _auditLogger.Lines.Should().ContainSingle(line =>
            line.Contains(UnrelatedUser, StringComparison.Ordinal) &&
            line.Contains(Patient, StringComparison.Ordinal) &&
            line.Contains(ToolTheCatalogDoesNotList, StringComparison.Ordinal) &&
            line.Contains("corr-1", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryCatalogTool))]
    public async Task DispatchAsync_ToolTheCatalogOffersTheModel_IsRoutableByTheDispatcher(string toolName)
    {
        // The catalog-to-switch direction of the 1:1 the doc claims: a tool offered to the model
        // that ExecuteAsync cannot route is a tool the model can call and never reach. Arguments
        // are deliberately empty - a tool that rejects them is still routable, and what is under
        // test here is the routing, not the schema.
        var result = await DispatchAs(ProviderOfRecord, toolName);

        ErrorOf(result).Should().NotContain(
            "Unknown tool", $"'{toolName}' is offered to the model, so ExecuteAsync must route it");
    }

    [Fact]
    public void ExecuteAsync_RoutableToolNames_AreExactlyTheToolsTheCatalogOffers()
    {
        // Both directions of the 1:1, read off the router itself. The theory above cannot see the
        // switch-only direction: the scope gate refuses an unoffered name before ExecuteAsync is
        // reached, so an arm the catalog does not list is unobservable through DispatchAsync. That is
        // where a gate bypass keyed on a new name would sit. Separate changes
        var router = typeof(McpToolDispatcher).GetMethod(
            "ExecuteAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        router.Should().NotBeNull("the enumeration reads McpToolDispatcher's router by name");

        var routable = StringSwitchCases.Of(router!);
        var offered = McpToolCatalog.AllTools.Select(tool => tool.Name);

        routable.Should().BeEquivalentTo(
            offered,
            "every switch arm must be a tool the catalog offers, and every offered tool must have an arm");
    }

    private static string ErrorOf(LlmToolResultContent result)
    {
        var root = JsonDocument.Parse(result.ResultJson).RootElement;
        return root.TryGetProperty("error", out var error) ? error.GetString() ?? string.Empty : string.Empty;
    }

    private Task<LlmToolResultContent> DispatchAs(string? clinicianIdentity, string toolName)
    {
        A.CallTo(() => _clinicianIdentityAccessor.ClinicianIdentity).Returns(clinicianIdentity);
        var dispatcher = new McpToolDispatcher(
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

        return dispatcher.DispatchAsync(
            Site, Patient, new LlmToolCall("call_1", toolName, "{}"), CancellationToken.None);
    }

    /// <summary>Every call that actually read patient data, across all three backing tool surfaces.</summary>
    private IEnumerable<ICompletedFakeObjectCall> BackingCalls() =>
        Fake.GetCalls(_toolServer).Concat(Fake.GetCalls(_documentFactsTool)).Concat(Fake.GetCalls(_evidenceTool));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
