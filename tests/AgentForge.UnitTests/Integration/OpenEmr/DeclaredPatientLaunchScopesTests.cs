using System.Text.RegularExpressions;
using AgentForge.Integration.OpenEmr;
using FluentAssertions;

namespace AgentForge.UnitTests.Integration.OpenEmr;

/// <summary>
/// The scopes a patient launch requests are not in code: they are the <c>OpenEmr__Scopes__N</c>
/// variables each deployment manifest declares. <c>SmartLaunchScopesTests</c> pins the registration
/// superset; this pins the request side of the same FR-AUTH-2 dependency, which is the half
/// <c>a separate change</c> found missing on production.
/// </summary>
public sealed class DeclaredPatientLaunchScopesTests
{
    // `OpenEmr__Scopes__15: patient/Appointment.read` (compose) or `OpenEmr__Scopes__15: "…",` (railway.ts).
    private static readonly Regex DeclaredScope =
        new(@"^\s*OpenEmr__Scopes__\d+:\s*""?(?<scope>[^""\s,]+)""?,?\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static TheoryData<string> Manifests => new() { "docker-compose.yml", ".railway/railway.ts" };

    [Theory]
    [MemberData(nameof(Manifests))]
    public void Manifest_PatientLaunchScopes_RequestTheScopeTheRelationshipGateReadsWith(string manifest)
    {
        // without it every token lacks Appointment, the gate's search 401s and every
        // per-patient launch is refused - a fail-closed outcome that looks exactly like "no relationship".
        DeclaredScopes(manifest).Should().Contain(
            SmartLaunchScopes.ReadScopeFor(SmartLaunchScopes.PatientContext, "Appointment"),
            "{0} declares what the patient launch requests (FR-AUTH-2)", manifest);
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void Manifest_PatientLaunchScopes_StayWithinTheRegisteredSuperset(string manifest)
    {
        // finalizeScopes drops any requested scope the client is not registered for, silently.
        DeclaredScopes(manifest).Should().BeSubsetOf(
            SmartLaunchScopes.PatientLaunch,
            "a scope requested but never registered is dropped without an error");
    }

    private static List<string> DeclaredScopes(string manifest)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root is found by walking up to AgentForge.slnx");
        var path = Path.Combine(directory!.FullName, manifest);
        File.Exists(path).Should().BeTrue("{0} is a deployment manifest this test guards", path);

        var scopes = DeclaredScope.Matches(File.ReadAllText(path))
            .Select(m => m.Groups["scope"].Value)
            .ToList();
        scopes.Should().NotBeEmpty("{0} must still declare OpenEmr__Scopes__N", manifest);
        return scopes;
    }
}
