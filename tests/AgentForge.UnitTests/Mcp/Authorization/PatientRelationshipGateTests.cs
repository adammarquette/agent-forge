using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Mcp.Authorization;
using FluentAssertions;

namespace AgentForge.UnitTests.Mcp.Authorization;

/// <summary>
/// The FR-AUTH-2 relationship decision itself (REQUIREMENTS.md §7.4), kept pure so it can be specified
/// without any FHIR/HTTP/session plumbing - the same shape as
/// <see cref="AgentForge.Api.Agenda.AgendaRosterGate"/>.
/// </summary>
public sealed class PatientRelationshipGateTests
{
    private const string Clinician = "dr-cardio";
    private const string Patient = "patient-123";

    [Fact]
    public void Authorize_ClinicianIsThePractitionerOnTheClinicDayAppointment_Permits()
    {
        // The permit half of the FR-AUTH-2 AC: a rule that denies everything is not a rule.
        var appointments = new[] { Appointment(Patient, $"Practitioner/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, Clinician, Patient).Should().BeTrue();
    }

    [Fact]
    public void Authorize_ForkEmittedThePersonActorFormInsteadOfPractitioner_Permits()
    {
        // The fork emits Practitioner/ only when the provider has an NPI on file and Person/
        // otherwise (AppointmentRecord.IsForProvider) - which form appears is not a statement
        // about entitlement, so the gate must accept both or it denies real clinicians at random.
        var appointments = new[] { Appointment(Patient, $"Person/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, Clinician, Patient).Should().BeTrue();
    }

    [Fact]
    public void Authorize_RequesterIsNotTheAppointmentsProvider_Denies()
    {
        // exactly: `admin` opened a chart they have no clinical relationship to.
        var appointments = new[] { Appointment(Patient, $"Practitioner/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, "admin", Patient).Should().BeFalse();
    }

    [Fact]
    public void Authorize_ClinicianHasAppointmentsButNotWithThisPatient_Denies()
    {
        var appointments = new[] { Appointment("some-other-patient", $"Practitioner/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, Clinician, Patient).Should().BeFalse();
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("noshow")]
    [InlineData("entered-in-error")]
    public void Authorize_TheOnlyMatchingAppointmentDidNotHappen_Denies(string status)
    {
        // Same exclusions AgendaRosterService already applies to the roster: an appointment that
        // was cancelled, no-showed or mis-entered is not evidence of a care relationship.
        var appointments = new[] { Appointment(Patient, $"Practitioner/{Clinician}", status) };

        PatientRelationshipGate.Authorize(appointments, Clinician, Patient).Should().BeFalse();
    }

    [Fact]
    public void Authorize_AppointmentCarriesNoProviderParticipant_Denies()
    {
        // Fail closed: the fork omits the provider participant when none is recorded, and an
        // unattributed appointment must not read as "related to whoever is asking".
        var appointments = new[] { Appointment(Patient, null, "booked") };

        PatientRelationshipGate.Authorize(appointments, Clinician, Patient).Should().BeFalse();
    }

    [Fact]
    public void Authorize_NoAppointmentsAtAll_Denies()
    {
        PatientRelationshipGate.Authorize([], Clinician, Patient).Should().BeFalse();
    }

    [Fact]
    public void Authorize_NoClinicianIdentity_Denies()
    {
        // An unauthenticated/unattributable requester can never be "related" (FR-AUTH-1).
        var appointments = new[] { Appointment(Patient, $"Practitioner/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, null, Patient).Should().BeFalse();
    }

    [Fact]
    public void Authorize_NoPatientId_Denies()
    {
        var appointments = new[] { Appointment(Patient, $"Practitioner/{Clinician}", "booked") };

        PatientRelationshipGate.Authorize(appointments, Clinician, null).Should().BeFalse();
    }

    private static AppointmentRecord Appointment(string? patientId, string? providerActorReference, string status) =>
        new(new ClinicalSourceRef("Appointment", "appt-1"), patientId, providerActorReference, status, DateTimeOffset.UtcNow);
}
