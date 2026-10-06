using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Patient;

/// <summary>
/// Builds the post-launch patient-context confirmation for the one patient a session is scoped to:
/// demographics for identity, and a per-class reachability summary proving the patient-scoped FHIR
/// path works end-to-end. Thin and read-only by design - the cited clinical brief is the Epic 3
/// agent, not this (REQUIREMENTS.md UC-1). Mirrors <c>AgendaRosterService</c>: sets the AsyncLocal access
/// token once, then isolates each fetch so one failing resource degrades to "unreachable" instead
/// of blanking the page (UC-5).
/// </summary>
public sealed class PatientContextService(
    IOpenEmrFhirClient fhirClient,
    IScopedAccessTokenProvider tokenProvider,
    ILogger<PatientContextService> logger,
    ILogger<AccessAudit> auditLogger,
    ICorrelationIdAccessor correlationIdAccessor)
{
    /// <summary>The access-audit trail's tool name for <c>GET /patient</c> (FR-AUTH-4).</summary>
    public const string AuditToolName = "patient_context";

    /// <summary>
    /// Builds <paramref name="session"/>'s launched-patient context confirmation, and writes one access-audit
    /// record for the read (FR-AUTH-4).
    /// </summary>
    public async Task<PatientContextResult> BuildAsync(PatientSessionContext session, CancellationToken cancellationToken)
    {
        // AsyncLocal-backed (ScopedAccessTokenProvider); the FHIR client's AuthHandler reads it back.
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);
        AccessAuditLog.RecordAccess(
            auditLogger, session.ClinicianIdentity, session.PatientId, AuditToolName, correlationIdAccessor.CorrelationId);

        PatientRecord? patient = null;
        var demographicsReachable = true;
        try
        {
            patient = await fhirClient.GetPatientAsync(session.Site, session.PatientId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PatientContextServiceLog.FetchFailed(logger, "Patient", ex.GetType());
            demographicsReachable = false;
        }

        var problems = await SummarizeAsync(
            "Condition",
            () => fhirClient.GetConditionsAsync(session.Site, session.PatientId, cancellationToken)).ConfigureAwait(false);
        var medications = await SummarizeAsync(
            "MedicationRequest",
            () => fhirClient.GetMedicationRequestsAsync(session.Site, session.PatientId, cancellationToken)).ConfigureAwait(false);
        var allergies = await SummarizeAsync(
            "AllergyIntolerance",
            () => fhirClient.GetAllergiesAsync(session.Site, session.PatientId, cancellationToken)).ConfigureAwait(false);

        return new PatientContextResult(
            session.PatientId,
            patient?.DisplayName,
            patient?.BirthDate,
            patient?.Gender,
            new ClinicalDataSummary(demographicsReachable, patient is null ? 0 : 1),
            problems,
            medications,
            allergies);
    }

    private async Task<ClinicalDataSummary> SummarizeAsync<T>(
        string resourceType, Func<Task<IReadOnlyList<T>>> fetch)
    {
        try
        {
            var items = await fetch().ConfigureAwait(false);
            return new ClinicalDataSummary(Reachable: true, Count: items.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PatientContextServiceLog.FetchFailed(logger, resourceType, ex.GetType());
            return new ClinicalDataSummary(Reachable: false, Count: 0);
        }
    }
}
