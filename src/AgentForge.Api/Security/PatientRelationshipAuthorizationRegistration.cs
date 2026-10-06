using AgentForge.Mcp.Authorization;

namespace AgentForge.Api.Security;

/// <summary>
/// Registers the FR-AUTH-2 relationship authorizer with the lifetime its memo depends on.
/// </summary>
public static class PatientRelationshipAuthorizationRegistration
{
    /// <summary>
    /// Adds <see cref="IPatientRelationshipAuthorizer"/> as a <em>scoped</em> service, so its memo of
    /// relationship decisions lives exactly one chat turn or one agenda fan-out branch
    /// (<c>ARCHITECTURE.md</c> §5.7). Registered here rather than inline so the lifetime is a
    /// testable claim: a singleton would make the memo process-wide, never evicted and unbounded.
    /// </summary>
    public static IServiceCollection AddPatientRelationshipAuthorization(this IServiceCollection services) =>
        services.AddScoped<IPatientRelationshipAuthorizer, PatientRelationshipAuthorizer>();
}
