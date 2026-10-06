using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.Mcp.Authorization;

/// <summary>
/// The FR-AUTH-2 entitlement decision: is this requester clinically related to this patient?
/// Kept a pure function - no FHIR, HTTP or session plumbing - so the one decision that matters is
/// trivially unit-testable, mirroring <c>AgendaRosterGate</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "relationship" means here, and why.</b> OpenEMR ships no patient-panel ownership
/// (OpenEMR's access control is by feature/section, not by panel, so this is the authorization
/// boundary the agent must add; ARCHITECTURE.md §5.3), so the rule can only be built from a signal this
/// deployment actually carries. Exactly one does: the provider participant on a FHIR
/// <c>Appointment</c>, written by the demo seeder as <c>pc_aid</c> and already trusted by the
/// Daily Agenda's roster filter (<c>AgendaRosterService</c>) to decide which patients are a given
/// clinician's. <b>The rule is therefore: the requester is the provider participant on an
/// appointment with this patient on the current clinic day.</b> The patient record's own
/// <c>providerID</c> cannot substitute: it is unpopulated for the demo cohort and is not a
/// <c>Patient</c> FHIR search parameter.
/// </para>
/// <para>
/// <b>The alternative signal that does exist, and why it is not used.</b> The fork <em>does</em>
/// serve <c>CareTeam</c> (<c>FhirCareTeamService</c>, routed at
/// <c>_rest_routes_fhir_r4_us_core_3_1_0.inc.php</c>, searchable by <c>patient</c>). It is backed
/// by the <c>care_teams</c>/<c>care_team_member</c> tables, which neither demo seeder writes and
/// no demo workflow populates - so a gate built on it would refuse every requester, and would
/// refuse them for a data reason dressed as an entitlement one. It is the right signal for a real
/// deployment and the wrong one for this deployment.
/// </para>
/// <para>
/// <b>It is deliberately narrow, by choice rather than by constraint.</b> It admits the workflow
/// the product is scoped to - the cardiologist briefing the patient in the next room, whose
/// appointment is today (REQUIREMENTS.md UC-1, UC-4, UC-6) - and nothing else; chart review a day later is
/// denied. Widening it is technically available: the fork's <c>SearchComparator</c> supports
/// <c>ge</c>/<c>le</c> on <c>Appointment?date</c>, and only the sidecar's own single-valued
/// <c>date</c> parameter would have to change. It stays at one day because a wider window admits
/// every clinician who ever shared an appointment with the patient inside it - a materially
/// different rule needing its own acceptance criterion - and no documented use case needs it
/// (UC-1 and the pre-clinic sweep are same-day; UC-6 is today's remaining schedule). A narrow rule
/// that refuses is the correct failure here; a permissive one that never denies would restate the
/// bug it replaces.
/// </para>
/// <para>
/// <b>It fails closed.</b> Every unknown - no appointments, no provider participant, no identity,
/// an unresolvable lookup (see <see cref="PatientRelationshipAuthorizer"/>) - is a refusal.
/// </para>
/// </remarks>
public static class PatientRelationshipGate
{
    // An appointment that did not happen is not evidence of a care relationship. Same exclusions
    // AgendaRosterService already applies when building a clinician's roster.
    private static readonly string[] NonRelationshipStatuses = ["cancelled", "noshow", "entered-in-error"];

    /// <summary>
    /// Whether <paramref name="clinicianIdentity"/> is the provider participant on an appointment
    /// with <paramref name="patientId"/> among <paramref name="clinicDayAppointments"/>.
    /// </summary>
    /// <param name="clinicDayAppointments">Every appointment on the clinic day, across all providers.</param>
    /// <param name="clinicianIdentity">The authenticated requester's subject identifier, or null when none is in scope.</param>
    /// <param name="patientId">The patient whose data is being requested.</param>
    public static bool Authorize(
        IReadOnlyList<AppointmentRecord> clinicDayAppointments,
        string? clinicianIdentity,
        string? patientId)
    {
        if (string.IsNullOrEmpty(clinicianIdentity) || string.IsNullOrEmpty(patientId))
        {
            return false;
        }

        return clinicDayAppointments.Any(appointment =>
            string.Equals(appointment.PatientId, patientId, StringComparison.Ordinal) &&
            appointment.IsForProvider(clinicianIdentity) &&
            !NonRelationshipStatuses.Contains(appointment.Status, StringComparer.OrdinalIgnoreCase));
    }
}
