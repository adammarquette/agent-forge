using System.Reflection;
using System.Text.Json;
using AgentForge.Agent;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Mcp;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FakeItEasy.Core;
using FluentAssertions;
using Refit;

namespace AgentForge.UnitTests.Agent;

/// <summary>
/// What enforces <c>REQUIREMENTS.md</c> §12.4's NG1 scope guardrail - "the Copilot surfaces and cites existing
/// record data and does not diagnose, recommend treatment, or place orders" - below the model rather
/// than in prompt text.
/// <para>
/// <b>The three verbs are not one guardrail, and only one of them is decidable.</b> "Does not
/// diagnose" and "does not recommend treatment" are properties of composed prose, and no
/// deterministic test separates surfacing a recorded diagnosis from making one - "she has AFib
/// [Condition/7]" is the same sentence either way. Those two stay prompt-only, stated as such in
/// <c>PROMPTS.md</c> §7. <b>"Does not place orders" is different in kind</b>: placing an order is
/// not something the model says, it is something the model would have to <i>call</i>, so it is
/// decidable from the action surface alone. These cases pin that surface at the three layers that
/// carry it, so the verb stops being true by construction and starts being true by decision.
/// </para>
/// <para>
/// A layer here going red is not a bug to route around. It means someone is giving the copilot a way
/// to change the record, which is NG1's third verb and <c>REQUIREMENTS.md</c> §4.2's NG5 - "a later,
/// higher-bar phase". This file is that bar: clear it by moving NG1/NG5 first, deliberately, not by
/// editing the assertion.
/// </para>
/// </summary>
public sealed class ClinicalScopeGuardrailTests
{
    private const string Site = "default";
    private const string Patient = "patient-123";

    /// <summary>
    /// A name the catalog does not offer, spelled as the thing NG1 forbids. The point is not that
    /// this particular name is blocked - it is that the gate consults what the copilot <i>offers</i>,
    /// so a tool wired into the router and advertised nowhere is inert rather than callable.
    /// </summary>
    private const string ToolTheCatalogDoesNotOffer = "place_order";

    private readonly IMcpToolServer _toolServer = A.Fake<IMcpToolServer>();
    private readonly IDocumentFactsTool _documentFactsTool = A.Fake<IDocumentFactsTool>();
    private readonly IEvidenceTool _evidenceTool = A.Fake<IEvidenceTool>();
    private readonly IClinicianIdentityAccessor _clinicianIdentityAccessor = A.Fake<IClinicianIdentityAccessor>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly CapturingLogger<McpToolDispatcher> _logger = new();
    private readonly CapturingLogger<AccessAudit> _auditLogger = new();
    private readonly McpToolDispatcher _sut;

    public ClinicalScopeGuardrailTests()
    {
        A.CallTo(() => _clinicianIdentityAccessor.ClinicianIdentity).Returns("dr-jones");
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        A.CallTo(() => _documentFactsTool.GetAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new DocumentFactsResult([])));
        A.CallTo(() => _evidenceTool.GetAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new EvidenceResult([])));

        // The entitlement decision is permissive on purpose: this file's subject is what the copilot
        // may *do* for a requester already allowed to see the patient, which is the only
        // configuration in which NG1 is the binding constraint.
        _sut = new McpToolDispatcher(
            _toolServer, StubPatientRelationshipAuthorizer.Related, _clinicianIdentityAccessor,
            _correlationIdAccessor, _metrics, _logger, _auditLogger, _documentFactsTool, _evidenceTool);
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

    // ---- Layer 1: what the model may cause to run --------------------------------------------

    [Fact]
    public async Task DispatchAsync_AToolTheCatalogDoesNotOffer_IsRefusedAsOutOfScopeRatherThanRouted()
    {
        // Given a tool call naming something the copilot does not offer, When it is dispatched for a
        // requester the entitlement gate permits, Then the refusal states the scope limit rather than
        // reporting a routing miss - the decision is taken from the offered surface, before the
        // router is consulted at all.
        var result = await Dispatch(ToolTheCatalogDoesNotOffer);

        result.IsError.Should().BeTrue();
        ErrorOf(result).Should().Be(
            McpToolCatalog.OutOfScopeRefusalMessage,
            "a tool the copilot does not offer is refused by the scope gate, not by the router");
    }

    [Fact]
    public async Task DispatchAsync_AToolTheCatalogDoesNotOffer_ReadsNothing()
    {
        await Dispatch(ToolTheCatalogDoesNotOffer);

        BackingCalls().Should().BeEmpty("a refused call must not reach any backing tool surface");
    }

    [Fact]
    public async Task DispatchAsync_AToolTheCatalogDoesNotOffer_IsNotCountedAsAToolFailure()
    {
        // The same separation FR-AUTH-2's refusal already has: an out-of-scope call is the
        // system working, and routing it into agentforge.tool_calls{outcome="failure"} would page an
        // operator for correct behaviour (FR-OBS-4). It also put a model-supplied name - which can be
        // any string the model emits - onto an exported metric label.
        await Dispatch(ToolTheCatalogDoesNotOffer);

        A.CallTo(() => _metrics.RecordToolCall(A<string>._, A<bool>._, A<TimeSpan>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task DispatchAsync_AToolTheCatalogDoesNotOffer_IsCountedOnItsOwnSeries()
    {
        // Not counting it as a failure must not mean not counting it. An attempt to make the copilot
        // act is the one signal this guardrail can emit at run time.
        await Dispatch(ToolTheCatalogDoesNotOffer);

        A.CallTo(() => _metrics.RecordOutOfScopeToolCall()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DispatchAsync_AToolTheCatalogDoesNotOffer_LogsTheAttemptedNameAndCorrelationId()
    {
        // The counter says an attempt happened; only the log says which name was attempted, which is
        // what tells a model typo apart from record content steering the copilot toward an action
        // (NFR-SEC-2, REQUIREMENTS.md R6). No patient id: this is not an FR-AUTH-4 access-audit event.
        await Dispatch(ToolTheCatalogDoesNotOffer);

        _logger.Lines.Should().ContainSingle(line =>
            line.Contains(ToolTheCatalogDoesNotOffer, StringComparison.Ordinal) &&
            line.Contains("corr-1", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryCatalogTool))]
    public async Task DispatchAsync_AToolTheCatalogOffers_IsNotRefusedAsOutOfScope(string toolName)
    {
        // The control that stops the gate passing by refusing everything. Arguments are empty, so a
        // tool may still reject them on its own contract - what must not happen is the scope refusal.
        var result = await Dispatch(toolName);

        ErrorOf(result).Should().NotBe(
            McpToolCatalog.OutOfScopeRefusalMessage,
            $"'{toolName}' is offered to the model, so the scope gate must let it through");
    }

    // ---- Layer 2: what the sidecar can send to the EHR ---------------------------------------

    [Fact]
    public void OpenEmrFhirApi_Always_DeclaresNoOperationThatCouldChangeTheRecord()
    {
        // Every tool above is only as read-only as the client underneath it. This is the layer that
        // makes "does not place orders" structural rather than a property of the current tool list:
        // there is no write verb to call, whatever a future tool asks for.
        //
        // IOpenEmrAuthApi's three POSTs are deliberately out of scope here - they are OAuth token,
        // introspection and client registration, not record operations (INTERFACES.md §A).
        var writeOperations = typeof(IOpenEmrFhirApi).GetMethods()
            .Where(method => method.GetCustomAttribute<HttpMethodAttribute>()?.Method != HttpMethod.Get)
            .Select(method => method.Name)
            .ToArray();

        writeOperations.Should().BeEmpty(
            "NG1/NG5: the sidecar reads the record and does not change it, so its FHIR client declares GET and nothing else");
    }

    // ---- Layer 3: what OpenEMR will accept from the sidecar, on a resource scope --------------

    [Fact]
    public void SmartLaunchScopes_Always_GrantNoResourceScopeThatCouldWrite()
    {
        // The layer outside this process entirely, and the only one an OpenEMR administrator can see:
        // every <context>/<Resource> scope either client is registered for ends .read, so a write the two
        // layers above somehow permitted has no scope to travel on. SmartLaunchScopesTests proves the read
        // scopes are *sufficient*; this proves no resource scope grants more than a read.
        //
        // Scoped to resource scopes deliberately, and that is narrower than "the launches cannot write"
        // . api:oemr is an API-FAMILY gate for OpenEMR's standard REST API, not a resource
        // grant, so it is not examined here - a standard-API write still needs a <context>/<resource>.write
        // alongside it, which is what this case would catch. These lists are the registration SUPERSET,
        // which is the right object: finalizeScopes intersects a launch's request against it, so a scope
        // absent here cannot be granted however a launch asks.
        var resourceScopes = SmartLaunchScopes.PatientLaunch
            .Concat(SmartLaunchScopes.AgendaLaunch)
            .Where(IsResourceScope)
            .ToArray();

        resourceScopes.Should().NotBeEmpty("both clients do carry resource scopes, so the filter above is not vacuous");
        resourceScopes.Should().OnlyContain(
            scope => scope.EndsWith(".read", StringComparison.Ordinal),
            "NG1/NG5: a resource scope granting a write would let the EHR accept a change the sidecar should never make");
    }

    /// <summary>
    /// A <c>&lt;context&gt;/&lt;Resource&gt;</c> scope, as opposed to the launch, identity and
    /// API-family scopes (<c>openid</c>, <c>fhirUser</c>, <c>launch</c>, <c>launch/patient</c>,
    /// <c>api:fhir</c>, <c>api:oemr</c>) - none of which names a resource or carries a read/write half,
    /// so there is nothing in them for the case above to check.
    /// </summary>
    private static bool IsResourceScope(string scope) =>
        scope.Contains('/', StringComparison.Ordinal)
        && !scope.StartsWith("launch", StringComparison.Ordinal);

    private Task<LlmToolResultContent> Dispatch(string toolName) =>
        _sut.DispatchAsync(Site, Patient, new LlmToolCall("call_1", toolName, "{}"), CancellationToken.None);

    private static string ErrorOf(LlmToolResultContent result)
    {
        var root = JsonDocument.Parse(result.ResultJson).RootElement;
        return root.TryGetProperty("error", out var error) ? error.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>Every call that actually read patient data, across all three backing tool surfaces.</summary>
    private IEnumerable<ICompletedFakeObjectCall> BackingCalls() =>
        Fake.GetCalls(_toolServer).Concat(Fake.GetCalls(_documentFactsTool)).Concat(Fake.GetCalls(_evidenceTool));
}
