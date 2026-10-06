using System.Globalization;
using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.Verification;

/// <summary>
/// Recency handling for the Observations a domain-constraint rule evaluates - the two mitigations
/// ARCHITECTURE.md §9.3 names for stale data reaching the clinician: rank by recency, and stamp the
/// value with the date it was drawn.
/// </summary>
/// <remarks>
/// <c>get_labs</c> takes an optional <c>since_date</c> and the brief calls it unbounded, so what a
/// tool returns this turn is the patient's lab history, not the patient's current state. A rule that
/// reads all of it states a years-old excursion as though it were today.
/// </remarks>
public static class ObservationRecency
{
    /// <summary>FHIR's tombstone status: the record is retracted and is not a value the patient has.</summary>
    private const string EnteredInError = "entered-in-error";

    /// <summary>
    /// The most recent Observation for each distinct category + code, which is the subset that
    /// describes the patient now. Ties keep the order the tool returned them in.
    /// </summary>
    /// <remarks>
    /// Ranked, never windowed. A cutoff date would silently drop the only result on file for a code,
    /// trading a wrongly-tensed flag for a missing one - the worse failure for a safety check. An
    /// Observation with no effective date cannot be shown to be superseded, so it loses only to a
    /// dated one and is otherwise kept.
    /// </remarks>
    public static IReadOnlyList<ObservationRecord> MostRecentPerCode(IReadOnlyList<ObservationRecord> observations) =>
    [
        // Ranking on date alone would let a retracted record outrank a real one and silence the rule
        // entirely. Only the tombstone is read, never a whitelist: a FHIR Observation with no status
        // maps to "unknown", so whitelisting would drop every result.
        .. observations
            .Where(o => !string.Equals(o.Status, EnteredInError, StringComparison.OrdinalIgnoreCase))
            .GroupBy(o => $"{o.Category}|{o.CodeDisplay}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(o => o.EffectiveDateTime ?? DateTimeOffset.MinValue)
                .First()),
    ];

    /// <summary>
    /// The clinician-facing "data as of" stamp for <paramref name="observation"/> - the date it was
    /// drawn, or an explicit statement that the record does not carry one.
    /// </summary>
    public static string AsOf(ObservationRecord observation) =>
        observation.EffectiveDateTime is { } effective
            ? $"as of {effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : "date unknown";
}
