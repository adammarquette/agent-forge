using AgentForge.Api.Security;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Mcp.Authorization;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests.Api.Security;

/// <summary>
/// The FR-AUTH-2 relationship memo's bound is a DI lifetime, and a lifetime is a claim like any
/// other: <see cref="PatientRelationshipAuthorizer"/> states that a decision "lives exactly as long
/// as one turn", which is only true while the <em>effective</em> registration stays scoped.
/// <c>AddSingleton</c> is the one-word change a future author reaches for on reading the per-branch
/// calendar search in <c>ARCHITECTURE.md</c> §5.7's *Known limits*. A separate change.
/// </summary>
/// <remarks>
/// These assert what the <em>container</em> does, not what
/// <see cref="PatientRelationshipAuthorizationRegistration.AddPatientRelationshipAuthorization"/>
/// added. The difference is the whole point: a descriptor assertion reads one entry, while
/// resolution takes the <em>last</em> registration for a service type - so an
/// <c>AddSingleton&lt;IPatientRelationshipAuthorizer, …&gt;</c> appended anywhere after this call,
/// in <c>Program.cs</c> or anywhere else, wins at resolution while leaving a descriptor assertion
/// perfectly green. A separate change finding 3.
/// <para>
/// What this tier can reach is the composition, not <c>Program.cs</c>'s own copy of it: nothing
/// short of booting the host hands a unit test the collection <c>Program.cs</c> built. So the last
/// case below pins the property that makes an appended singleton <em>detectable at all</em> - the
/// authorizer holds a request-scoped <see cref="IOpenEmrFhirClient"/>, which is what turns a
/// widened lifetime into a captive dependency a validating container refuses to build rather than
/// a memo that silently outlives its clinic day. <c>Program.cs</c> turns that validation on in
/// every environment for the same reason; keep these two together, because this case is what says
/// the detector still has something to detect.
/// </para>
/// </remarks>
public sealed class PatientRelationshipAuthorizationRegistrationTests
{
    [Fact]
    public void AddPatientRelationshipAuthorization_ResolvedFromTheBuiltContainer_GivesEachScopeItsOwnAuthorizer()
    {
        // The memo is a field on the instance, so "a decision lives exactly one turn" *is* the
        // claim that two turns get two instances. A singleton would hand both the same memo.
        using var provider = Build(Compose());
        using var oneTurn = provider.CreateScope();
        using var theNextTurn = provider.CreateScope();

        var first = oneTurn.ServiceProvider.GetRequiredService<IPatientRelationshipAuthorizer>();
        var again = oneTurn.ServiceProvider.GetRequiredService<IPatientRelationshipAuthorizer>();
        var next = theNextTurn.ServiceProvider.GetRequiredService<IPatientRelationshipAuthorizer>();

        first.Should().BeOfType<PatientRelationshipAuthorizer>();
        again.Should().BeSameAs(first, "one turn shares one memo - that is what keeps the calendar search off the per-tool-call path");
        next.Should().NotBeSameAs(first, "a memo that outlives its turn is process-wide, never evicted, and grows unbounded");
    }

    [Fact]
    public void AddPatientRelationshipAuthorization_ResolvedOutsideAnyScope_IsRefusedByTheContainer()
    {
        // The other face of the same lifetime: a container that will hand the authorizer out at
        // root has no turn to bound the memo to.
        using var provider = Build(Compose());

        var resolveFromTheRoot = () => provider.GetRequiredService<IPatientRelationshipAuthorizer>();

        resolveFromTheRoot.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddPatientRelationshipAuthorization_ASingletonAppendedAfterIt_CannotBuildOverTheScopedFhirClient()
    {
        // Reproduced during the review: a build carrying this mutant died at startup with
        // "Cannot consume scoped service 'IOpenEmrFhirClient' from singleton
        // 'IPatientRelationshipAuthorizer'". This case pins that it is *still* detectable - the
        // authorizer must go on holding a request-scoped dependency, or a widened lifetime becomes
        // silent again and only the memo's behaviour gives it away.
        var services = Compose();
        services.AddSingleton<IPatientRelationshipAuthorizer, PatientRelationshipAuthorizer>();

        var build = () => Build(services);

        build.Should().Throw<AggregateException>()
            .Which.ToString().Should().Contain(nameof(IOpenEmrFhirClient));
    }

    /// <summary>
    /// The authorizer's graph as <c>Program.cs</c> composes it: a request-scoped
    /// <see cref="IOpenEmrFhirClient"/> (it carries one call's token), a singleton
    /// <see cref="ClinicClock"/>, and the registration under test.
    /// </summary>
    private static ServiceCollection Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<ClinicOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ClinicClock>();
        services.AddScoped(_ => A.Fake<IOpenEmrFhirClient>());
        services.AddPatientRelationshipAuthorization();
        return services;
    }

    /// <summary>
    /// Builds with the same validation the shipped host runs - <c>Program.cs</c> turns
    /// <c>ValidateScopes</c>/<c>ValidateOnBuild</c> on in every environment, not only the
    /// Development boot the framework defaults them to, so a captive scoped dependency is a
    /// startup failure wherever the image runs rather than a silent one outside Development.
    /// Do not weaken this to match a default: matching the host is the point.
    /// </summary>
    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
}
