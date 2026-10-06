namespace AgentForge.Mcp.Authorization;

/// <summary>
/// The outcome of one FR-AUTH-2 relationship question, plus the one number an operator needs to
/// read a refusal: how many clinic-day appointments the decision was made against.
/// </summary>
/// <param name="IsRelated">Whether the requester may access the patient's data.</param>
/// <param name="ClinicDayAppointmentsConsidered">
/// Appointments returned for the current clinic day, across all providers, or
/// <see langword="null"/> when the lookup could not be resolved at all.
/// </param>
/// <remarks>
/// The count exists because a genuine "not your patient" and a demo whose seeded appointment
/// window has aged out produce the same refusal, and the two need opposite responses — one is the
/// gate working, the other is an empty calendar. A count of <c>0</c> says "nobody has a clinic
/// today", which is an operations problem, not an entitlement one. It is a cardinality, not
/// clinical content, so it is safe on the audit line (CONVENTIONS.md §7).
/// </remarks>
public readonly record struct PatientRelationshipDecision(bool IsRelated, int? ClinicDayAppointmentsConsidered)
{
    /// <summary>A refusal made without ever reaching OpenEMR, or against a lookup that failed.</summary>
    public static PatientRelationshipDecision Unresolved { get; } = new(IsRelated: false, ClinicDayAppointmentsConsidered: null);

    /// <summary>The count as it appears on an audit line, with the unresolved case named rather than blank.</summary>
    public string AuditCount =>
        ClinicDayAppointmentsConsidered is { } count ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unresolved";
}
