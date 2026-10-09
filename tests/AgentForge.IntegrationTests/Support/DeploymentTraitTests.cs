using System.Reflection;
using FluentAssertions;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Keeps the merge-request job's selection honest. <c>integration-tests-no-deployment</c> runs this project
/// filtered to <c>Deployment=None</c>, so a deployment-free class that loses or never gets the trait drops out
/// of every merge-request pipeline silently, and a QA-bound class that gains it turns that job red on every
/// merge request. Named failure mode (regression): a class that needs no deployment ran only after merge, so
/// a broken hand-built host was seen there.
/// </summary>
// Selects this class into the merge-request job integration-tests-no-deployment.
[Trait("Deployment", "None")]
public sealed class DeploymentTraitTests
{
    private const string TraitName = "Deployment";
    private const string NoDeployment = "None";

    private static readonly IReadOnlyList<Type> TestClasses = typeof(DeploymentTraitTests).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetMethods().Any(IsTestMethod))
        .ToList();

    [Fact]
    public void TestClasses_NoQaFixture_DeclareWhatTheyNeed()
    {
        // A class with no QA fixture either needs nothing (None) or names the one real service it reaches.
        var undeclared = TestClasses
            .Where(t => !TakesQaFixture(t) && DeploymentTrait(t) is null)
            .Select(t => t.Name);

        undeclared.Should().BeEmpty(
            "a test class without a QA fixture must say whether it needs a deployment, or the merge-request " +
            "job cannot tell whether to run it");
    }

    [Fact]
    public void TestClasses_TaggedNoDeployment_TakeNoQaFixture()
    {
        var misTagged = TestClasses
            .Where(t => TakesQaFixture(t) && DeploymentTrait(t) == NoDeployment)
            .Select(t => t.Name);

        misTagged.Should().BeEmpty("a QA fixture throws without its environment, which the merge-request job does not have");
    }

    [Fact]
    public void TestClasses_TaggedNoDeployment_IncludeTheHandBuiltHubHost()
    {
        // Control: the two checks above pass vacuously if reflection finds no tagged class at all.
        TestClasses.Where(t => DeploymentTrait(t) == NoDeployment).Select(t => t.Name)
            .Should().Contain(nameof(Api.TestServerHubSessionTransportTests));
    }

    private static bool IsTestMethod(MethodInfo method) =>
        method.GetCustomAttributes<FactAttribute>(inherit: true).Any();

    private static bool TakesQaFixture(Type type) =>
        type.GetInterfaces().Any(i =>
            i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IClassFixture<>) || i.GetGenericTypeDefinition() == typeof(ICollectionFixture<>))
            && i.GetGenericArguments()[0].Name.EndsWith("QaFixture", StringComparison.Ordinal));

    private static string? DeploymentTrait(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(TraitAttribute)
                && a.ConstructorArguments.Count == 2
                && (string?)a.ConstructorArguments[0].Value == TraitName)
            .Select(a => (string?)a.ConstructorArguments[1].Value)
            .FirstOrDefault();
}
