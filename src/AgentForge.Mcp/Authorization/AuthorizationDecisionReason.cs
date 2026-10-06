namespace AgentForge.Mcp.Authorization;

/// <summary>
/// The closed set of reasons an FR-AUTH-2 patient-access decision may be tagged with on a metric,
/// and the mapping from a <see cref="PatientRelationshipDecision"/> onto it.
/// </summary>
/// <remarks>
/// Separate from <see cref="PatientAccessRefusal.AuditReason"/> on purpose. That one is prose for a
/// human reading the audit trail; this one is an exported label, which means it multiplies the
/// counter's series, survives for the retention of the metrics store, and leaves the process. So it
/// is enum-like and derived from the decision's shape alone — never from the patient, the
/// requester, the tool or the appointment count (CONVENTIONS.md §7's no-PHI rule, which
/// covers telemetry as well as logs). A separate change.
/// </remarks>
public static class AuthorizationDecisionReason
{
    /// <summary>Permitted: the requester is a provider participant on the patient's clinic-day appointment.</summary>
    public const string ClinicalRelationship = "clinical-relationship";

    /// <summary>Refused: the clinic day was read and the requester is not on it — the gate working.</summary>
    public const string NoClinicalRelationship = "no-clinical-relationship";

    /// <summary>
    /// Refused because the question could not be answered at all — no requester identity, no
    /// patient, or the OpenEMR lookup failed. Kept distinct from
    /// <see cref="NoClinicalRelationship"/> because the two refuse identically to the requester by
    /// design, and an operator needs to tell an outage from an entitlement boundary.
    /// </summary>
    public const string RelationshipUnresolved = "relationship-unresolved";

    /// <summary>Every reason that may ever reach the label, so the cardinality claim is checkable.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ClinicalRelationship, NoClinicalRelationship, RelationshipUnresolved];

    /// <summary>Maps one decision onto its bounded reason. Total by construction — every shape lands in <see cref="All"/>.</summary>
    public static string For(PatientRelationshipDecision decision) => decision switch
    {
        { IsRelated: true } => ClinicalRelationship,
        { ClinicDayAppointmentsConsidered: null } => RelationshipUnresolved,
        _ => NoClinicalRelationship,
    };
}
