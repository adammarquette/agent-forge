using System.Reflection;
using System.Text.RegularExpressions;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using FluentAssertions;
using Refit;

namespace AgentForge.UnitTests.Integration.OpenEmr;

/// <summary>
/// Guards the invariant that nothing asserted: <b>a FHIR resource the sidecar reads
/// has a read scope on the client that reads it</b>. The FR-AUTH-2 relationship gate searched
/// <c>Appointment</c> as the patient-launch client while no scope list anywhere granted it, so the
/// search 401'd and every per-patient launch was refused 403 with the relationship unresolvable.
/// </summary>
public sealed class SmartLaunchScopesTests
{
    // Every literal /fhir/<Resource> route on IOpenEmrFhirApi. The templated {resourceType} read
    // names no resource, so it cannot be checked here.
    private static readonly Regex FhirResourceRoute =
        new(@"/fhir/(?<resource>[A-Za-z]+)(/|$)", RegexOptions.Compiled);

    // Resources the fork's ServerScopeListEntity::fhirResourceScopesV1() omits, so no
    // "<context>/<Resource>.read" exists to grant for them: requesting one is rejected as
    // invalid_scope at /authorize and /registration, which fails the whole launch rather than the
    // one call. They appear only in the V2 ".rs" catalog, and this sidecar requests no V2 scope.
    // Reading one is therefore unimplementable, so the two cases below assert the sidecar neither
    // routes to it nor asks for it - the opposite of exempting it from the checks.
    private static readonly string[] ResourcesAbsentFromForkV1ScopeCatalog = ["MedicationDispense"];

    [Fact]
    public void PatientLaunch_ForTheResourceTheRelationshipGateSearches_GrantsItsReadScope()
    {
        // The FR-AUTH-2 gate resolves its answer through OpenEmrFhirClient.GetAppointmentsAsync,
        // which is this route. Derived from the route rather than hard-coded, so repointing the
        // gate at another resource moves this assertion with it.
        var resource = ResourceOfRoute(typeof(IOpenEmrFhirApi).GetMethod(nameof(IOpenEmrFhirApi.SearchAppointmentsAsync))!);

        SmartLaunchScopes.PatientLaunch.Should().Contain(
            SmartLaunchScopes.ReadScopeFor(SmartLaunchScopes.PatientContext, resource!),
            "the patient launch cannot resolve a care relationship it is not allowed to read (FR-AUTH-2)");
    }

    [Fact]
    public void PatientLaunch_EveryFhirResourceTheSidecarReads_HasAPatientReadScope()
    {
        MissingReadScopes(SmartLaunchScopes.PatientLaunch, SmartLaunchScopes.PatientContext)
            .Should().BeEmpty();
    }

    [Fact]
    public void AgendaLaunch_EveryFhirResourceTheSidecarReads_HasAUserReadScope()
    {
        MissingReadScopes(SmartLaunchScopes.AgendaLaunch, SmartLaunchScopes.UserContext)
            .Should().BeEmpty();
    }

    [Fact]
    public void OpenEmrFhirApi_ForAResourceWithNoGrantableV1Scope_DeclaresNoRouteToIt()
    {
        FhirResourcesRead().Should().NotIntersectWith(
            ResourcesAbsentFromForkV1ScopeCatalog,
            "a read the fork cannot authorize on any client is dead weight, not a gap to grant around");
    }

    [Fact]
    public void LaunchScopes_ForAResourceWithNoGrantableV1Scope_RequestNoReadScopeForIt()
    {
        foreach (var resource in ResourcesAbsentFromForkV1ScopeCatalog)
        {
            SmartLaunchScopes.PatientLaunch.Should().NotContain(
                SmartLaunchScopes.ReadScopeFor(SmartLaunchScopes.PatientContext, resource),
                "a scope outside the fork's V1 catalog is invalid_scope at /authorize, which fails the launch");
            SmartLaunchScopes.AgendaLaunch.Should().NotContain(
                SmartLaunchScopes.ReadScopeFor(SmartLaunchScopes.UserContext, resource),
                "a scope outside the fork's V1 catalog is invalid_scope at /authorize, which fails the launch");
        }
    }

    [Fact]
    public void PatientLaunch_CarriesNoUserContextScope_SoTheTwoClientsStayDistinct()
    {
        // finalizeScopes narrows silently, so a context-prefix slip is invisible at launch and
        // surfaces as a 401 on one call. The agenda client is the only user/ one.
        SmartLaunchScopes.PatientLaunch.Should().NotContain(s => s.StartsWith("user/", StringComparison.Ordinal));
        SmartLaunchScopes.AgendaLaunch.Should().NotContain(s => s.StartsWith("patient/", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingFrom_ARegistrationWithoutTheGateScope_NamesPatientAppointmentRead()
    {
        // staging's patient client was registered before a separate change added the gate's scope, so
        // finalizeScopes dropped it from every token and the FR-AUTH-2 lookup 401'd.
        var stagingRegistration = string.Join(' ', SmartLaunchScopes.PatientLaunch.Where(
            s => s != SmartLaunchScopes.ReadScopeFor(SmartLaunchScopes.PatientContext, "Appointment")));

        SmartLaunchScopes.MissingFrom(SmartLaunchScopes.PatientLaunch, stagingRegistration)
            .Should().Equal("patient/Appointment.read");
    }

    [Fact]
    public void MissingFrom_AScopeJoinedByAnythingButOneSpace_CountsItMissing()
    {
        // OpenEMR splits a stored scope list with explode(" "), so a newline-joined scope is a
        // different token and grants nothing. Comparing any looser than that reports it present.
        SmartLaunchScopes.MissingFrom(["openid", "patient/Appointment.read"], "openid\npatient/Appointment.read")
            .Should().Equal("openid", "patient/Appointment.read");
    }

    [Fact]
    public void MissingFrom_NoScopeStringAtAll_ReportsEveryExpectedScope()
    {
        SmartLaunchScopes.MissingFrom(["openid", "api:fhir"], null).Should().Equal("openid", "api:fhir");
    }

    [Fact]
    public void MissingFrom_EveryExpectedScopePresentInAnyOrder_ReportsNothing()
    {
        SmartLaunchScopes.MissingFrom(SmartLaunchScopes.PatientLaunch, string.Join(' ', SmartLaunchScopes.PatientLaunch.Reverse()))
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://front.example.org/agentforge/callback")]
    [InlineData("http://localhost:8080/agentforge/callback|https://front.example.org/agentforge/callback")]
    public void RegisteredSupersetFor_ThePatientCallback_IsThePatientLaunchList(string redirectUri)
    {
        SmartLaunchScopes.RegisteredSupersetFor(redirectUri).Should().BeSameAs(SmartLaunchScopes.PatientLaunch);
    }

    [Fact]
    public void RegisteredSupersetFor_TheAgendaCallback_IsTheAgendaLaunchListNotThePatientOne()
    {
        // Both paths end in /callback; granting the agenda client patient/ scopes would be a
        // silent no-op on every agenda read.
        SmartLaunchScopes.RegisteredSupersetFor("https://front.example.org/agentforge/agenda/callback")
            .Should().BeSameAs(SmartLaunchScopes.AgendaLaunch);
    }

    [Theory]
    [InlineData("https://front.example.org/some-other-app/callback")]
    [InlineData("not a uri")]
    [InlineData("")]
    [InlineData("https://a.example.org/agentforge/callback|https://a.example.org/agentforge/agenda/callback")]
    public void RegisteredSupersetFor_AClientThatIsNotUnambiguouslyOneOfOurs_IsNull(string redirectUri)
    {
        // Null means "leave its scopes alone": widening a client we cannot identify is worse
        // than the gap it might close.
        SmartLaunchScopes.RegisteredSupersetFor(redirectUri).Should().BeNull();
    }

    [Theory]
    [InlineData("patient/Appointment.read", true)]
    [InlineData("user/Binary.read", true)]
    [InlineData("launch/patient", false)]
    [InlineData("openid", false)]
    [InlineData("api:fhir", false)]
    public void IsResourceScope_ClassifiesFhirResourceScopesOnly(string scope, bool expected)
    {
        SmartLaunchScopes.IsResourceScope(scope).Should().Be(expected);
    }

    private static IEnumerable<string> MissingReadScopes(IReadOnlyList<string> granted, string scopeContext) =>
        FhirResourcesRead()
            .Select(resource => SmartLaunchScopes.ReadScopeFor(scopeContext, resource))
            .Where(scope => !granted.Contains(scope, StringComparer.Ordinal));

    private static IEnumerable<string> FhirResourcesRead() =>
        typeof(IOpenEmrFhirApi).GetMethods()
            .Select(ResourceOfRoute)
            .Where(resource => resource is not null)
            .Select(resource => resource!)
            .Distinct(StringComparer.Ordinal);

    private static string? ResourceOfRoute(MethodInfo method)
    {
        var route = method.GetCustomAttribute<GetAttribute>()?.Path;
        if (route is null)
        {
            return null;
        }

        var match = FhirResourceRoute.Match(route);
        return match.Success ? match.Groups["resource"].Value : null;
    }
}
