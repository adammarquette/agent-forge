namespace AgentForge.Observability;

/// <summary>
/// Which of <c>AgentOrchestrator</c>'s three entry points produced a turn — the one dimension that
/// separates <c>NFR-PERF-1</c>'s budgeted population from the turns that merely share its instrument.
/// The budget is stated for a single-patient <c>RequestBrief</c> turn (<see cref="Brief"/>) only;
/// <see cref="Agenda"/> turns are "1–3 sentences" by design and are fanned out one per rostered
/// patient across a 20–30-patient panel, so untagged they outnumber the budgeted turns and hold the
/// p95 down. A separate change
/// </summary>
/// <remarks>
/// <b>This becomes an exported metric label, so its cardinality is its contract.</b> Three values,
/// all compile-time constants, none derived from a patient, a site, a session or a correlation id.
/// A fourth member multiplies every bucket of the turn-duration histogram and re-opens the question
/// of which values <c>AgentForgeHighTurnLatencyP95</c>'s <c>{turn_type="brief"}</c> filter excludes —
/// add one only with that alert in hand.
/// </remarks>
public enum AgentTurnType
{
    /// <summary>
    /// The initial single-patient pre-visit brief (UC-1) — and the only population
    /// <c>NFR-PERF-1</c>'s p95 ≤ 26s budget covers.
    /// </summary>
    Brief = 0,

    /// <summary>
    /// A Daily Agenda per-patient summary (UC-6): 1–3 sentences read in a scan, explicitly not a
    /// full pre-visit brief, and emitted one per rostered patient by <c>AgendaRosterService</c>.
    /// </summary>
    Agenda = 1,

    /// <summary>An in-session follow-up question within an existing conversation (UC-2).</summary>
    FollowUp = 2,
}

/// <summary>Helpers for reporting an <see cref="AgentTurnType"/> outside the C# type system.</summary>
public static class AgentTurnTypeExtensions
{
    /// <summary>
    /// The bounded, PHI-free token this turn type is reported under. It is the literal value of the
    /// exported <c>turn_type</c> label, so changing a spelling here silently re-points
    /// <c>AgentForgeHighTurnLatencyP95</c>'s filter and Grafana panel 3 at an empty series.
    /// </summary>
    public static string ToWireName(this AgentTurnType turnType) => turnType switch
    {
        AgentTurnType.Brief => "brief",
        AgentTurnType.Agenda => "agenda",
        AgentTurnType.FollowUp => "follow_up",
        _ => throw new ArgumentOutOfRangeException(nameof(turnType), turnType, "Unknown agent turn type."),
    };
}
