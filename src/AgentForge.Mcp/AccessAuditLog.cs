using Microsoft.Extensions.Logging;

namespace AgentForge.Mcp;

/// <summary>
/// The access-audit trail (FR-AUTH-4): who accessed which patient's data, through which tool, for
/// which correlated request. Deliberately distinct from every other log in this codebase - this is
/// the one place a patient id is expected to appear, because an access-audit trail structurally
/// requires it (the same reason every HIPAA-covered EHR's own audit log carries a patient
/// reference). It is not general application logging and does not relax "no PHI in logs"
/// elsewhere. Every line is written under the dedicated <see cref="AccessAudit"/> category, which the
/// host excludes from the OpenTelemetry log provider: the trail reaches the console only, never Loki or
/// any OTLP sink (item 1, option A). A dedicated access-controlled audit store is a separate,
/// future item.
/// </summary>
public static partial class AccessAuditLog
{
    /// <summary>
    /// Records one patient-data access — clinician identity, patient, tool, and correlation id — to the
    /// access-audit stream (FR-AUTH-4). This is the one log that intentionally carries a patient reference; do
    /// not route general application logging through it.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ACCESS AUDIT: clinician={ClinicianIdentity} accessed patient={PatientId} via tool={ToolName} correlation={CorrelationId}")]
    public static partial void RecordAccess(ILogger<AccessAudit> logger, string clinicianIdentity, string patientId, string toolName, string correlationId);

    /// <summary>
    /// Records one <em>refused</em> patient-data access (FR-AUTH-2 / FR-AUTH-4). A denial is an access
    /// attempt and belongs in the same trail as a granted one — REQUIREMENTS.md section 11 lists "Denied access
    /// attempt" as its own audit event — so an entitlement boundary that refuses silently would be
    /// invisible to exactly the review it exists for.
    /// <para>
    /// <paramref name="clinicDayAppointments"/> is what tells a real "not your patient" apart from a
    /// demo whose seeded appointment window has aged out: both refuse identically otherwise, and
    /// <c>0</c> means nobody has a clinic today, which is an operations problem rather than an
    /// entitlement one. A cardinality, never clinical content.
    /// </para>
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACCESS AUDIT: clinician={ClinicianIdentity} REFUSED patient={PatientId} via tool={ToolName} " +
                  "reason={Reason} clinicDayAppointments={ClinicDayAppointments} correlation={CorrelationId}")]
    public static partial void RecordRefusal(
        ILogger<AccessAudit> logger, string clinicianIdentity, string patientId, string toolName, string reason,
        string clinicDayAppointments, string correlationId);
}
