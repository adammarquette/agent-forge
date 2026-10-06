using System.ComponentModel.DataAnnotations;

namespace AgentForge.Integration.OpenEmr;

/// <summary>
/// The clinic's wall-clock identity, bound via the Options pattern (CONVENTIONS.md §6).
/// One setting today: the zone in which "the current clinic day" is decided.
/// </summary>
/// <remarks>
/// OpenEMR stores an appointment's day in <c>pc_eventDate</c> as a <em>local</em> date with no
/// offset, so "today" is only well defined against a named zone. The zone is the clinic's, not the
/// container's: the sidecar and OpenEMR run in different containers, and a UTC host would roll the
/// clinic day over in the early evening for a US clinic - which, since FR-AUTH-2 resolves against
/// that day, would refuse every launch from that point until midnight.
/// </remarks>
public sealed class ClinicOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Clinic";

    /// <summary>
    /// Default clinic zone: the demo clinic is US Central, and it is the zone
    /// <c>DEPLOYMENT.md</c> §4a already tells the operator to seed appointments with
    /// (<c>php -d date.timezone=America/Chicago</c>), so it is what <c>pc_eventDate</c> means here.
    /// </summary>
    public const string DefaultTimeZone = "America/Chicago";

    /// <summary>
    /// IANA time-zone id for the clinic's wall clock, e.g. <c>America/Chicago</c>. Must match the
    /// zone OpenEMR's appointment rows were written in, or the two disagree about what day it is.
    /// </summary>
    public string TimeZone { get; init; } = DefaultTimeZone;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(TimeZone))
        {
            yield return new ValidationResult(
                $"{nameof(TimeZone)} must not be empty.", [nameof(TimeZone)]);
            yield break;
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            // Fail at startup rather than at the first authorization decision: an unresolvable zone
            // would otherwise surface as a blanket refusal, which looks like a policy decision.
            yield return new ValidationResult(
                $"{nameof(TimeZone)} '{TimeZone}' is not a time zone this host can resolve.",
                [nameof(TimeZone)]);
        }
    }
}
