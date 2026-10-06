using FluentAssertions;
using AgentForge.Api.Agenda;
using AgentForge.UnitTests.TestSupport;

namespace AgentForge.UnitTests.Api.Agenda;

public sealed class AgendaRosterGateTests
{
    [Fact]
    public void Authorize_PatientIsInTheRoster_ReturnsTrue()
    {
        var roster = new HashSet<string> { "patient-1", "patient-2" };

        AgendaRosterGate.Authorize(roster, "patient-1").Should().BeTrue();
    }

    [Fact]
    public void Authorize_PatientIsNotInTheRoster_ReturnsFalse()
    {
        // The core guard: a client cannot request an arbitrary patientId outside what the agenda
        // itself already returned (ARCHITECTURE.md §19.1 step 5).
        var roster = new HashSet<string> { "patient-1", "patient-2" };

        AgendaRosterGate.Authorize(roster, "someone-elses-patient").Should().BeFalse();
    }

    [Fact]
    public void Authorize_EmptyRoster_ReturnsFalse() =>
        AgendaRosterGate.Authorize(new HashSet<string>(), "patient-1").Should().BeFalse();

    [Fact]
    public void AuthorizeAndAudit_PatientIsNotInTheRoster_RefusesAndWritesARefusedAuditLineNamingThePatient()
    {
        // REQUIREMENTS.md §13.1 lists "Denied access attempt (FR-AUTH-4)" as an audit event. The diagnostic line no longer
        // names the patient (CONVENTIONS.md §7), so the audit trail is the only record of which one was
        // attempted - under the request's correlation id, like the other refusal paths. A separate change
        var roster = new HashSet<string> { "patient-1", "patient-2" };
        var logger = new CapturingLogger<AccessAudit>();

        const string clinician = "dr-jones";
        const string patient = "patient-9";

        var permitted = AgendaRosterGate.AuthorizeAndAudit(roster, clinician, patient, "corr-123", logger);

        permitted.Should().BeFalse();
        logger.Lines.Should().ContainSingle().Which.Should()
            .Contain($"ACCESS AUDIT: clinician={clinician} REFUSED patient={patient}")
            .And.Contain("correlation=corr-123")
            .And.Contain("clinicDayAppointments=2");
    }

    [Fact]
    public void AuthorizeAndAudit_PatientIsInTheRoster_PermitsAndWritesNothing()
    {
        // A permitted selection is not a data access - the chat's tool calls audit those when they happen.
        var logger = new CapturingLogger<AccessAudit>();

        var permitted = AgendaRosterGate.AuthorizeAndAudit(new HashSet<string> { "patient-1" }, "dr-jones", "patient-1", "corr-123", logger);

        permitted.Should().BeTrue();
        logger.Lines.Should().BeEmpty();
    }
}
