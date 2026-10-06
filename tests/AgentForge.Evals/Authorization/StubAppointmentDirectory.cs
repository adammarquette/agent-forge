using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.Evals.Authorization;

/// <summary>
/// Serves one case's seeded clinic day to the real <see cref="AgentForge.Mcp.Authorization.PatientRelationshipAuthorizer"/>,
/// so the gate under test is the shipped one rather than a re-implementation of it.
/// </summary>
/// <remarks>
/// The date filter is ignored on purpose: the case fixture <em>is</em> "the current clinic day", which keeps
/// the outcome independent of when the gate runs. Every other member throws — the authorization path reads
/// appointments and nothing else, and a silent empty list from an unexpected call would read as a refusal
/// the gate never actually made.
/// </remarks>
internal sealed class StubAppointmentDirectory(
    IReadOnlyList<AppointmentRecord> clinicDay, bool lookupFails) : IOpenEmrFhirClient
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AppointmentRecord>> GetAppointmentsAsync(
        string site, string dateFilter, CancellationToken cancellationToken) =>
        lookupFails
            ? Task.FromException<IReadOnlyList<AppointmentRecord>>(
                new HttpRequestException("Synthetic OpenEMR outage: the appointment search is unreachable."))
            : Task.FromResult(clinicDay);

    /// <inheritdoc />
    public Task<PatientRecord?> GetPatientAsync(string site, string patientId, CancellationToken cancellationToken) =>
        throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<MedicationRecord>> GetMedicationRequestsAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<ConditionRecord>> GetConditionsAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<ObservationRecord>> GetObservationsAsync(
        string site, string patientId, string? category, string? dateFilter, CancellationToken cancellationToken) =>
        throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<AllergyRecord>> GetAllergiesAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<EncounterRecord>> GetEncountersAsync(
        string site, string patientId, string? dateFilter, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcedureRecord>> GetProceduresAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<ClinicalDocumentRecord>> GetDiagnosticReportsAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<IReadOnlyList<ClinicalDocumentRecord>> GetDocumentReferencesAsync(
        string site, string patientId, CancellationToken cancellationToken) => throw NotOnThisPath();

    /// <inheritdoc />
    public Task<BinaryDocument?> GetBinaryAsync(string site, string documentId, CancellationToken cancellationToken) =>
        throw NotOnThisPath();

    private static NotSupportedException NotOnThisPath() =>
        new("The authorization eval harness serves appointments only; no other FHIR read is on this path.");
}
