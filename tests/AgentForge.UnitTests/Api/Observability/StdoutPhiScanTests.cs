using FluentAssertions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The stdout scanner's exemption rules, over console text written by hand, so each rule is pinned without a host.
/// Failure mode guarded (invariant): the audit trail's exemption widening past its own two fields - a name, or an
/// identifier in the wrong field, riding an audit line unscanned. Separate changes
/// </summary>
public sealed class StdoutPhiScanTests
{
    private static readonly ScanIdentifiers Ids = new("scan-patient-5e1f", "scan-clinician-8a2c", "Scanfamilyname", ["scan-document-3b7d"]);

    [Fact]
    public void LinesLeaking_WhenAnAuditLineNamesOnlyItsOwnPatientAndClinician_ReportsNothing()
    {
        var stdout = Audit($"clinician={Ids.ClinicianId} accessed patient={Ids.PatientId} via tool=get_patient_summary correlation=c-1");

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().BeEmpty();
        StdoutPhiScan.AuditLinesNaming(stdout, Ids.PatientId).Should().ContainSingle();
    }

    [Theory]
    [InlineData("clinician={1} accessed patient={0} via tool={2} correlation=c-1")]
    [InlineData("clinician={0} accessed patient={0} via tool=get_patient_summary correlation=c-1")]
    [InlineData("clinician={1} accessed patient={1} via tool=get_patient_summary correlation=c-1")]
    [InlineData("clinician={1} accessed patient={2} via tool=get_patient_summary correlation=c-1")]
    [InlineData("clinician={1} accessed patient={0} via tool=get_patient_summary correlation={1}")]
    [InlineData("clinician={1} REFUSED patient={0} via tool=evidence_ask reason={2} clinicDayAppointments=1 correlation=c-1")]
    public void LinesLeaking_WhenAnAuditLineCarriesAnIdentifierOutsideItsOwnField_ReportsIt(string template)
    {
        var stdout = Audit(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Ids.PatientId, Ids.ClinicianId, Ids.DisplayName));

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().ContainSingle().Which.Should().Contain("ACCESS AUDIT: ");
    }

    [Fact]
    public void LinesLeaking_WhenAnotherCategoryImitatesTheAuditLine_ReportsIt()
    {
        var stdout = $"""
            info: AgentForge.Mcp.McpToolServer[1]
                  ACCESS AUDIT: clinician={Ids.ClinicianId} accessed patient={Ids.PatientId} via tool=get_patient_summary correlation=c-1
            """;

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().ContainSingle();
    }

    [Fact]
    public void LinesLeaking_WhenAnAuditCategoryLineIsOffTemplate_ScansItWhole()
    {
        var stdout = $"""
            info: {StdoutPhiScan.AccessAuditCategory}[1]
                  ACCESS AUDIT: patient={Ids.PatientId} clinician={Ids.ClinicianId}
            """;

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().ContainSingle();
    }

    [Fact]
    public void LinesLeaking_WhenAnAuditEntryIsFollowedByAnExporterRecord_ScansTheRecord()
    {
        // The OTel exporter's record is not indented under the audit header, so it is diagnostic.
        var stdout = Audit($"clinician={Ids.ClinicianId} accessed patient={Ids.PatientId} via tool=get_patient_summary correlation=c-1")
            + $"\nLogRecord.FormattedMessage: ACCESS AUDIT: clinician={Ids.ClinicianId} accessed patient={Ids.PatientId} via tool=x correlation=c-1";

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().ContainSingle().Which.Should().StartWith("LogRecord.");
    }

    [Theory]
    [InlineData("[Scope.0]:RequestPath: /evidence/document/scan-document-3b7d")]
    [InlineData("LogRecord.FormattedMessage: fetched scan-document-3b7d")]
    [InlineData("      ACCESS AUDIT: clinician=scan-clinician-8a2c accessed patient=scan-patient-5e1f via tool=evidence_document document=scan-document-3b7d")]
    public void LinesLeaking_WhenAnyLineNamesADocumentId_ReportsIt(string line)
    {
        // A document id has no exempt field, so it is reported on an audit line and in a scope alike. A separate change
        var stdout = line.StartsWith("      ", StringComparison.Ordinal) ? $"info: {StdoutPhiScan.AccessAuditCategory}[1]\n{line}" : line;

        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().ContainSingle().Which.Should().Contain("scan-document-3b7d");
    }

    private static string Audit(string fields) => $"""
        info: {StdoutPhiScan.AccessAuditCategory}[1]
              ACCESS AUDIT: {fields}
        """;
}
