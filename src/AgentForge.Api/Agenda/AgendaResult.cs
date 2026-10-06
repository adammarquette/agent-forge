namespace AgentForge.Api.Agenda;

/// <summary>The Daily Agenda for one clinician (ARCHITECTURE.md §19), ordered soonest-first.</summary>
/// <param name="Rows">One row per not-yet-seen patient today, ordered by <see cref="AgendaRow.ScheduledStart"/>.</param>
/// <param name="AsOf">
/// The instant this agenda was computed - the single "now" used for both the roster query and the not-yet-seen filter.
/// A summary re-served from the cache is older; its row's <see cref="AgendaRow.SummaryAsOf"/> says by how much.
/// </param>
public sealed record AgendaResult(IReadOnlyList<AgendaRow> Rows, DateTimeOffset AsOf);
