using System.ComponentModel.DataAnnotations;
using AgentForge.Integration.OpenEmr;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Integration.OpenEmr;

/// <summary>
/// The clinic day, which OpenEMR stores as a local <c>pc_eventDate</c> with no offset and which
/// FR-AUTH-2 now resolves against (<c>ARCHITECTURE.md</c> §5.7). Getting the zone wrong refuses
/// every launch for part of the day, so this is an authorization boundary, not a display detail.
/// </summary>
public sealed class ClinicClockTests
{
    [Fact]
    public void Today_UtcHasRolledOverButTheClinicHasNot_StaysOnTheClinicsDay()
    {
        // 01:00 UTC on the 19th is 20:00 on the 18th in US Central: the clinic is still mid-evening
        // clinic, and every appointment it can see is dated the 18th.
        var clock = ClockAt(new DateTimeOffset(2026, 9, 19, 1, 0, 0, TimeSpan.Zero));

        clock.Today.Should().Be(new DateOnly(2026, 9, 18));
        clock.TodayDateSearchValue.Should().Be("eq2026-09-18");
    }

    [Fact]
    public void Today_ClinicHasRolledOverAndUtcAlreadyHad_MovesToTheNewDay()
    {
        var clock = ClockAt(new DateTimeOffset(2026, 9, 19, 6, 0, 0, TimeSpan.Zero));

        clock.Today.Should().Be(new DateOnly(2026, 9, 19));
    }

    [Fact]
    public void Now_AnyZone_IsTheSameInstantTheTimeProviderReports()
    {
        // Instant comparisons (AgendaRosterService's "appointment still ahead of now") must be
        // unaffected by the offset the clock carries.
        var utcNow = new DateTimeOffset(2026, 9, 19, 1, 0, 0, TimeSpan.Zero);

        ClockAt(utcNow).Now.ToUniversalTime().Should().Be(utcNow);
    }

    [Fact]
    public void TimeZone_NoConfiguration_IsTheDemoClinicsZone()
    {
        // DEPLOYMENT.md §4a seeds appointments with `date.timezone=America/Chicago`, so that is
        // what pc_eventDate means in this deployment.
        ClockAt(DateTimeOffset.UtcNow).TimeZone.Id.Should().Be(ClinicOptions.DefaultTimeZone);
    }

    [Fact]
    public void Validate_TimeZoneCannotBeResolved_FailsAtStartupRatherThanAtTheFirstDecision()
    {
        // An unresolvable zone would otherwise surface as a blanket refusal, which reads like a
        // policy decision rather than a misconfiguration.
        var options = new ClinicOptions { TimeZone = "Mars/Olympus_Mons" };

        options.Validate(new ValidationContext(options)).Should().ContainSingle();
    }

    [Fact]
    public void Validate_TimeZoneIsEmpty_IsRejected()
    {
        var options = new ClinicOptions { TimeZone = "  " };

        options.Validate(new ValidationContext(options)).Should().ContainSingle();
    }

    [Fact]
    public void Validate_ResolvableTimeZone_IsAccepted()
    {
        var options = new ClinicOptions { TimeZone = "America/New_York" };

        options.Validate(new ValidationContext(options)).Should().BeEmpty();
    }

    private static ClinicClock ClockAt(DateTimeOffset utcNow) =>
        new(new FixedTimeProvider(utcNow), Options.Create(new ClinicOptions()));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
