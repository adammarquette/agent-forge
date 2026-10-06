using AgentForge.Mcp.Authorization;

namespace AgentForge.UnitTests.TestSupport;

/// <summary>
/// A fixed FR-AUTH-2 answer, for tests whose subject is something other than the entitlement
/// decision. The real rule is specified in <c>PatientRelationshipGateTests</c>, resolved in
/// <c>PatientRelationshipAuthorizerTests</c>, and enforced in
/// <c>McpToolDispatcherAuthorizationTests</c> and <c>EvidenceEndpointsAuthorizationTests</c>; using
/// this stub anywhere else keeps those the only places that can prove or disprove it.
/// </summary>
internal sealed class StubPatientRelationshipAuthorizer(bool related, int? clinicDayAppointments = 1)
    : IPatientRelationshipAuthorizer
{
    /// <summary>The permissive stub - "this requester is the patient's clinician".</summary>
    public static StubPatientRelationshipAuthorizer Related { get; } = new(true);

    /// <summary>The refusing stub - "this requester has no relationship to this patient".</summary>
    public static StubPatientRelationshipAuthorizer Unrelated { get; } = new(false, clinicDayAppointments: 4);

    /// <inheritdoc />
    public Task<PatientRelationshipDecision> AuthorizeAsync(
        string site, string? clinicianIdentity, string patientId, CancellationToken cancellationToken) =>
        Task.FromResult(new PatientRelationshipDecision(related, clinicDayAppointments));
}
