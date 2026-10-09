using AgentForge.Mcp.Authorization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Pins FR-AUTH-2's per-request authorizer against the host that actually ships. Named failure mode
/// (regression): the Production provider handing <see cref="IPatientRelationshipAuthorizer"/> out of
/// the root, so one requester's memo of authorization decisions outlives its request. Outside
/// Development the host validated no scopes until <c>Program.cs</c> turned that on in every
/// environment, and no unit test asserts the authorizer's lifetime on the booted host: the unit
/// fixtures that boot <c>Program.cs</c> stay green with that validation removed. This asserts the
/// guarantee, not the two lines that produce it.
/// </summary>
// Selects this class into the merge-request job integration-tests-no-deployment.
[Trait("Deployment", "None")]
[Trait("Metric", "M3-AuthorizationIntegrity")]
public sealed class ProductionCompositionTests : IClassFixture<ProductionHostFixture>
{
    private readonly ProductionHostFixture _fixture;

    public ProductionCompositionTests(ProductionHostFixture fixture) => _fixture = fixture;

    [Fact]
    public void RootProvider_ProductionHost_RefusesThePatientRelationshipAuthorizer()
    {
        // Without this the fixture could quietly boot as Development and pass on the framework default.
        _fixture.Services.GetRequiredService<IHostEnvironment>().IsProduction().Should().BeTrue();

        var resolveFromRoot = () => _fixture.Services.GetService<IPatientRelationshipAuthorizer>();

        resolveFromRoot.Should().Throw<InvalidOperationException>()
            .WithMessage($"*scoped service*{nameof(IPatientRelationshipAuthorizer)}*root provider*");
    }

    [Fact]
    public void RequestScope_ProductionHost_ResolvesOneAuthorizerPerScope()
    {
        // Control: the refusal above is about lifetime, not a missing or unconstructable registration.
        using var first = _fixture.Services.CreateScope();
        using var second = _fixture.Services.CreateScope();

        var authorizer = first.ServiceProvider.GetService<IPatientRelationshipAuthorizer>();

        authorizer.Should().NotBeNull();
        authorizer.Should().BeSameAs(first.ServiceProvider.GetService<IPatientRelationshipAuthorizer>());
        authorizer.Should().NotBeSameAs(second.ServiceProvider.GetService<IPatientRelationshipAuthorizer>());
    }
}
