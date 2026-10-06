using AgentForge.Mcp.Authorization;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// A fixed "yes" to the FR-AUTH-2 relationship question. Deliberately stubbed rather than resolved
/// against QA: the real rule needs the QA token's subject to be the provider on a QA appointment
/// dated today, which no QA fixture guarantees and none of these tests is about. The entitlement
/// decision itself is covered by the unit tests named in
/// <c>AgentForge.UnitTests.TestSupport.StubPatientRelationshipAuthorizer</c>.
/// </summary>
internal sealed class StubPatientRelationshipAuthorizer : IPatientRelationshipAuthorizer
{
    /// <inheritdoc />
    public Task<PatientRelationshipDecision> AuthorizeAsync(
        string site, string? clinicianIdentity, string patientId, CancellationToken cancellationToken) =>
        Task.FromResult(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 1));
}
