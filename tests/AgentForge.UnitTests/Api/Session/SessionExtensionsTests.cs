using FluentAssertions;
using AgentForge.Api.Session;
using AgentForge.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http;

namespace AgentForge.UnitTests.Api.Session;

public sealed class SessionExtensionsTests
{
    // One hour after the launch - OpenEMR's real access-token lifetime.
    private static readonly DateTimeOffset Expiry = new(2026, 9, 18, 16, 5, 36, TimeSpan.Zero);

    // Every read in this suite happens an hour before Expiry unless a test says otherwise.
    private static readonly DateTimeOffset BeforeExpiry = Expiry.AddHours(-1);

    private readonly InMemoryTestSession _session = new();

    [Fact]
    public void SavePatientSession_ThenTryGetPatientSession_RoundTripsAllFields()
    {
        var context = new PatientSessionContext("token-abc", "default", "123", "dr-jones", Expiry);

        _session.SavePatientSession(context);
        var result = _session.TryGetPatientSession(At(BeforeExpiry));

        result.Should().Be(context);
    }

    [Fact]
    public void TryGetPatientSession_NothingSaved_ReturnsNull() =>
        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();

    [Fact]
    public void TryGetPatientSession_PartiallyPopulatedSession_ReturnsNullRatherThanABrokenObject()
    {
        // Guards against ever handing a caller a PatientSessionContext with a missing field silently
        // defaulted (e.g. an empty PatientId) - the whole product is single-patient-scoped, so a
        // half-populated session must read as "not authenticated," never as authenticated-but-wrong.
        _session.SetString("patient-session.access-token", "token-abc");

        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void TryGetPatientSession_MissingClinicianIdentity_ReturnsNullRatherThanAnUnattributableSession()
    {
        // FR-AUTH-4: every access must be attributable to who made it - a session missing the
        // clinician identity is just as invalid as one missing the patient id.
        _session.SetString("patient-session.access-token", "token-abc");
        _session.SetString("patient-session.site", "default");
        _session.SetString("patient-session.patient-id", "123");

        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void ClearPatientSession_AfterSave_TryGetReturnsNullAfterward()
    {
        _session.SavePatientSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Expiry));

        _session.ClearPatientSession();

        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void SaveAgendaSession_ThenTryGetAgendaSession_RoundTripsAllFields()
    {
        var context = new AgendaSessionContext("token-abc", "default", "dr-jones", Expiry);

        _session.SaveAgendaSession(context);
        var result = _session.TryGetAgendaSession(At(BeforeExpiry));

        result.Should().Be(context);
    }

    [Fact]
    public void TryGetAgendaSession_NothingSaved_ReturnsNull() =>
        _session.TryGetAgendaSession(At(BeforeExpiry)).Should().BeNull();

    [Fact]
    public void TryGetAgendaSession_PartiallyPopulatedSession_ReturnsNullRatherThanABrokenObject()
    {
        _session.SetString("agenda-session.access-token", "token-abc");

        _session.TryGetAgendaSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void ClearAgendaSession_AfterSave_TryGetReturnsNullAfterward()
    {
        _session.SaveAgendaSession(new AgendaSessionContext("token-abc", "default", "dr-jones", Expiry));

        _session.ClearAgendaSession();

        _session.TryGetAgendaSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void SaveAgendaSession_AndSavePatientSession_DoNotCollideInTheSameUnderlyingSession()
    {
        // Distinct key prefixes: a clinician mid-flow on both a single-patient launch and an
        // agenda launch in the same browser session must not have one clobber the other.
        _session.SaveAgendaSession(new AgendaSessionContext("agenda-token", "default", "dr-jones", Expiry));
        _session.SavePatientSession(new PatientSessionContext("patient-token", "default", "123", "dr-jones", Expiry));

        _session.TryGetAgendaSession(At(BeforeExpiry)).Should().Be(new AgendaSessionContext("agenda-token", "default", "dr-jones", Expiry));
        _session.TryGetPatientSession(At(BeforeExpiry)).Should().Be(new PatientSessionContext("patient-token", "default", "123", "dr-jones", Expiry));
    }

    [Fact]
    public void SaveAgendaRoster_ThenTryGetAgendaRoster_RoundTripsEveryPatientId()
    {
        _session.SaveAgendaRoster(["patient-1", "patient-2", "patient-3"]);

        _session.TryGetAgendaRoster().Should().BeEquivalentTo(["patient-1", "patient-2", "patient-3"]);
    }

    [Fact]
    public void TryGetAgendaRoster_NothingSaved_ReturnsEmptySetRatherThanNull()
    {
        // The drill-down gate (AgendaRosterGate.Authorize) takes a set unconditionally - an empty
        // set correctly rejects every patientId rather than requiring a separate null check.
        _session.TryGetAgendaRoster().Should().BeEmpty();
    }

    [Fact]
    public void TryGetPatientSession_SavedWithoutATokenExpiry_ReturnsNullRatherThanAnUnboundedSession()
    {
        // A session whose token expiry is unknown is exactly the state that let a browser
        // session outlive the one-hour OpenEMR access token it holds: every FHIR read came back 401
        // while the session still read as authenticated. It is also the shape of a session written
        // by a build from before this key existed, so a rolling deploy must land those users on a
        // re-launch, not on a brief written from an empty chart.
        _session.SetString("patient-session.access-token", "token-abc");
        _session.SetString("patient-session.site", "default");
        _session.SetString("patient-session.patient-id", "123");
        _session.SetString("patient-session.clinician-identity", "dr-jones");

        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void TryGetPatientSession_SavedWithAnUnparseableExpiry_ReturnsNullRatherThanGuessing()
    {
        _session.SavePatientSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Expiry));
        _session.SetString("patient-session.expires-at", "not-a-timestamp");

        _session.TryGetPatientSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void TryGetAgendaSession_SavedWithoutATokenExpiry_ReturnsNullRatherThanAnUnboundedSession()
    {
        // The agenda launch holds its own token with its own one-hour lifetime.
        _session.SetString("agenda-session.access-token", "token-abc");
        _session.SetString("agenda-session.site", "default");
        _session.SetString("agenda-session.clinician-identity", "dr-jones");

        _session.TryGetAgendaSession(At(BeforeExpiry)).Should().BeNull();
    }

    [Fact]
    public void SavePatientSession_ThenTryGetPatientSession_RoundTripsTheExpiryInstantExactly()
    {
        // Not just "some expiry": an instant that round-trips lossily would refuse a live session
        // or, worse, admit a dead one.
        var context = new PatientSessionContext(
            "token-abc", "default", "123", "dr-jones", new DateTimeOffset(2026, 9, 18, 16, 5, 36, 123, TimeSpan.Zero));

        _session.SavePatientSession(context);

        _session.TryGetPatientSession(At(BeforeExpiry))!.ExpiresAt.Should().Be(context.ExpiresAt);
    }

    [Fact]
    public void TryGetPatientSession_ReadAfterTheTokenExpired_ReturnsNullSoEverySurfaceRefusesTheSameWay()
    {
        // review round 1. Reading a session is the one thing every HTTP surface and the hub
        // already do, so the expiry check lives here rather than in each of them: there is exactly
        // one way to obtain a session and it can never hand back a dead one. Without this, an
        // expired token reached a FHIR call and surfaced as an unhandled 500.
        _session.SavePatientSession(new PatientSessionContext("token-abc", "default", "123", "dr-jones", Expiry));

        _session.TryGetPatientSession(At(Expiry)).Should().BeNull("the boundary belongs on the refusing side");
        _session.TryGetPatientSession(At(Expiry.AddSeconds(1))).Should().BeNull();
        _session.TryGetPatientSession(At(Expiry.AddSeconds(-1))).Should().NotBeNull();
    }

    [Fact]
    public void TryGetAgendaSession_ReadAfterTheTokenExpired_ReturnsNull()
    {
        _session.SaveAgendaSession(new AgendaSessionContext("token-abc", "default", "dr-jones", Expiry));

        _session.TryGetAgendaSession(At(Expiry)).Should().BeNull();
        _session.TryGetAgendaSession(At(Expiry.AddSeconds(-1))).Should().NotBeNull();
    }

    private static FixedTimeProvider At(DateTimeOffset now) => new(now);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
