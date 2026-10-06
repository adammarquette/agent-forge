using Microsoft.Extensions.Options;

namespace AgentForge.Integration.OpenEmr;

/// <summary>
/// The one definition of "now" and "the current clinic day" that everything reading OpenEMR's
/// calendar shares — the Daily Agenda roster and the FR-AUTH-2 relationship check
/// (<c>ARCHITECTURE.md</c> §5.7, §19.2a). Both ask OpenEMR for one day's appointments, so if they
/// resolved that day differently the roster could offer a patient the authorization gate then
/// refuses.
/// </summary>
/// <remarks>
/// Wraps <see cref="TimeProvider"/> rather than replacing it, so tests still drive time through a
/// fake. <see cref="Now"/> is the same instant <see cref="TimeProvider.GetUtcNow"/> returns, just
/// carried at the clinic's offset — instant comparisons are unaffected; only the calendar date is.
/// </remarks>
public sealed class ClinicClock
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a clock reading <paramref name="options"/>' zone off <paramref name="timeProvider"/>.</summary>
    public ClinicClock(TimeProvider timeProvider, IOptions<ClinicOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider;
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    }

    /// <summary>The clinic's zone, as configured by <see cref="ClinicOptions.TimeZone"/>.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Now, at the clinic's offset.</summary>
    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZone);

    /// <summary>The clinic's current calendar day — what OpenEMR's <c>pc_eventDate</c> means.</summary>
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>
    /// The FHIR <c>Appointment?date=</c> value selecting exactly the current clinic day
    /// (<c>INTERFACES.md</c> B.2).
    /// </summary>
    public string TodayDateSearchValue => $"eq{Today:yyyy-MM-dd}";
}
