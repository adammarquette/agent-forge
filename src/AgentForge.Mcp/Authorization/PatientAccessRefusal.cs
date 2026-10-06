namespace AgentForge.Mcp.Authorization;

/// <summary>
/// The single wording of an FR-AUTH-2 refusal, shared by every choke point that can issue one
/// (the SMART launch, the tool dispatcher and the evidence endpoints) so a denial reads the same wherever
/// it is raised.
/// </summary>
public static class PatientAccessRefusal
{
    /// <summary>
    /// What the requester is told (REQUIREMENTS.md section 11's authorization-denied row). Says only that
    /// access was denied: no patient detail, no clinical content, and nothing about why the
    /// relationship check failed, so a refusal cannot be used to probe the schedule.
    /// </summary>
    public const string UserFacingMessage = "You don't have access to this patient's record.";

    /// <summary>The machine-readable reason recorded on the access-audit entry (FR-AUTH-4).</summary>
    public const string AuditReason = "no-clinical-relationship-to-patient (FR-AUTH-2)";

    /// <summary>
    /// The audit reason for a source-document fetch whose document is not the session patient's, or whose
    /// owner the sidecar cannot resolve at all (FR-AUTH-2 at <c>GET /evidence/document/{id}</c>). The requester
    /// is told the same <see cref="UserFacingMessage"/> either way.
    /// </summary>
    public const string DocumentOutsideSessionPatientAuditReason = "document-not-in-session-patient-record (FR-AUTH-2)";
}
