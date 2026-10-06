using FluentAssertions;
using AgentForge.Api.Session;

namespace AgentForge.UnitTests.Api.Session;

/// <summary>
/// The key a page is rendered with and its chat connection presents, so a connection that outlives a patient
/// switch (another tab drilling down, a second launch on the same cookie) can tell it no longer matches the
/// session's patient. Synthetic ids only. A separate change
/// </summary>
public sealed class PatientContextBindingTests
{
    private const string SessionId = "session-synthetic-1";
    private static readonly DateTimeOffset Expiry = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static PatientSessionContext SessionFor(string patientId, string site = "default") =>
        new("token-synthetic", site, patientId, "dr-synthetic", Expiry);

    [Fact]
    public void KeyFor_SameSessionAndPatient_IsStableSoAReconnectPresentsTheSameKey() =>
        PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A"))
            .Should().Be(PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A") with { AccessToken = "token-relaunched" }));

    [Fact]
    public void KeyFor_AnotherPatientInTheSameSession_Differs() =>
        PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A"))
            .Should().NotBe(PatientContextBinding.KeyFor(SessionId, SessionFor("pt-B")));

    [Fact]
    public void KeyFor_SamePatientIdOnAnotherSite_Differs() =>
        // Patient ids are unique only within an OpenEMR site.
        PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A", "site-one"))
            .Should().NotBe(PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A", "site-two")));

    [Fact]
    public void KeyFor_AnotherSession_Differs() =>
        PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A"))
            .Should().NotBe(PatientContextBinding.KeyFor("session-synthetic-2", SessionFor("pt-A")));

    [Fact]
    public void KeyFor_Always_CarriesNeitherTheSessionIdNorThePatientIdInTheClear()
    {
        // It travels in the hub URL's query string, which a reverse proxy logs.
        var key = PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A"));

        key.Should().NotContain(SessionId).And.NotContain("pt-A");
    }

    [Fact]
    public void Matches_KeyRenderedForTheSessionsCurrentPatient_IsTrue() =>
        PatientContextBinding.Matches(PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A")), SessionId, SessionFor("pt-A"))
            .Should().BeTrue();

    [Fact]
    public void Matches_KeyRenderedForThePreviousPatient_IsFalse() =>
        PatientContextBinding.Matches(PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A")), SessionId, SessionFor("pt-B"))
            .Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    public void Matches_NoKeyOrAMalformedOne_FailsClosed(string? presented) =>
        PatientContextBinding.Matches(presented, SessionId, SessionFor("pt-A")).Should().BeFalse();

    [Fact]
    public void Matches_KeyOfTheRightLengthThatIsNotHex_FailsClosedRatherThanThrowing()
    {
        // Passes the length check and reaches the hex decode, which must refuse rather than fault the connection.
        var presented = new string('z', PatientContextBinding.KeyFor(SessionId, SessionFor("pt-A")).Length);

        var act = () => PatientContextBinding.Matches(presented, SessionId, SessionFor("pt-A"));

        act.Should().NotThrow().Which.Should().BeFalse();
    }
}
