using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.Verification;

/// <summary>
/// The structured clinical data a <see cref="IDomainConstraintRule"/> evaluates - read directly
/// from tool results, never from the model's prose (ARCHITECTURE.md §9.2: rules evaluate values,
/// not model-internal knowledge or re-parsed free text).
/// </summary>
/// <param name="ActiveMedications">Every distinct active medication returned by a tool this turn.</param>
/// <param name="Labs">
/// Every distinct lab/vital Observation returned by a tool this turn - the whole history the tools
/// returned, which for an unbounded <c>get_labs</c> is the whole chart. A rule that describes the
/// patient now reads <see cref="CurrentLabs"/> instead.
/// </param>
/// <param name="ActiveProblems">Every distinct active problem returned by a tool this turn (drives indication detection).</param>
/// <remarks>
/// Distinct by citation: overlapping tools (<c>get_labs</c> and <c>get_interval_changes</c> issue the
/// same FHIR query) would otherwise make a rule fire once per copy, and a rule's output reaches the
/// clinician without de-duplication.
/// </remarks>
public sealed record DomainConstraintInput(
    IReadOnlyList<MedicationRecord> ActiveMedications,
    IReadOnlyList<ObservationRecord> Labs,
    IReadOnlyList<ConditionRecord> ActiveProblems)
{
    /// <summary>An input with nothing in it, for a turn where no relevant tool was called.</summary>
    public static DomainConstraintInput Empty { get; } = new([], [], []);

    /// <summary>
    /// The most recent Observation per code from <see cref="Labs"/> - the only subset a rule may
    /// speak about in the present tense (ARCHITECTURE.md §9.3).
    /// </summary>
    public IReadOnlyList<ObservationRecord> CurrentLabs => ObservationRecency.MostRecentPerCode(Labs);
}
