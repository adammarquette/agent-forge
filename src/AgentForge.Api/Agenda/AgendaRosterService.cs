using AgentForge.Agent;
using AgentForge.Api.Chat;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Builds the Daily Agenda for one clinician (ARCHITECTURE.md §19): fetches today's roster,
/// filters it to this provider's not-yet-occurred appointments, and fans out a short per-patient
/// summary with bounded concurrency and per-patient failure isolation. Calls
/// <see cref="IAgentOrchestrator"/> only through <see cref="IAgendaPatientSummaryRunner"/> - never
/// through <see cref="ChatSessionCoordinator"/>, which is hard-wired to one chat session and
/// doesn't fit an N-patient fan-out.
/// </summary>
public sealed class AgendaRosterService(
    IOpenEmrFhirClient fhirClient,
    IAgendaPatientSummaryRunner summaryRunner,
    IScopedAccessTokenProvider tokenProvider,
    IConversationTurnBudget turnBudget,
    IAgendaSummaryCache summaryCache,
    ClinicClock clinicClock,
    IOptions<AgendaOptions> agendaOptions,
    ILogger<AgendaRosterService> logger)
{
    /// <summary>
    /// A row's <see cref="AgendaRow.FailureReason"/> when the session's LLM turn budget is spent: the
    /// roster still renders, without that patient's summary.
    /// </summary>
    public const string BudgetExhaustedReason =
        "Summary not generated - this session has reached its turn limit until its budget resets.";

    private static readonly string[] ExcludedStatuses = ["cancelled", "noshow", "entered-in-error"];

    /// <summary>
    /// Builds <paramref name="session"/>'s clinician's Daily Agenda, charging each patient summary to
    /// <paramref name="sessionId"/>'s LLM turn budget; a row the budget refuses is marked failed and runs no LLM call.
    /// A summary already in <see cref="IAgendaSummaryCache"/> for this site, clinician, patient, appointment and
    /// clinic day is re-served with no LLM call and no charge, carrying the instant it was generated as
    /// <see cref="AgendaRow.SummaryAsOf"/> rather than this load's. Only a row this load's live roster produced is
    /// looked up, so a cached summary is served only where the relationship it was generated under still holds.
    /// </summary>
    public async Task<AgendaResult> BuildAgendaAsync(
        string sessionId, AgendaSessionContext session, CancellationToken cancellationToken)
    {
        // The access token is AsyncLocal-backed (ScopedAccessTokenProvider) and flows into every
        // fan-out branch's new DI scope on its own - set once here, not per-patient, since it's
        // the same provider/token throughout (unlike clinician identity, which
        // IAgendaPatientSummaryRunner sets fresh per patient - see its own remarks).
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);

        // The clinic day, not the container's: the roster and the FR-AUTH-2 gate (§5.7) must ask
        // OpenEMR for the *same* day, or the agenda offers a patient the drill-down then refuses.
        var now = clinicClock.Now;
        var clinicDay = clinicClock.Today;
        var appointments = await fhirClient
            .GetAppointmentsAsync(session.Site, clinicClock.TodayDateSearchValue, cancellationToken)
            .ConfigureAwait(false);

        var roster = appointments
            .Where(a => !string.IsNullOrEmpty(a.PatientId))
            .Where(a => a.IsForProvider(session.ClinicianIdentity))
            .Where(a => !ExcludedStatuses.Contains(a.Status, StringComparer.OrdinalIgnoreCase))
            .Where(a => a.ScheduledStart is { } start && start > now)
            .OrderBy(a => a.ScheduledStart)
            .ToList();

        var rows = new AgendaRow?[roster.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, roster.Count),
            new ParallelOptions { MaxDegreeOfParallelism = agendaOptions.Value.MaxConcurrentSummaries, CancellationToken = cancellationToken },
            async (index, ct) =>
            {
                var appointment = roster[index];
                var patientId = appointment.PatientId!;
                var displayName = await ResolveDisplayNameOrNullAsync(session.Site, patientId, index, roster.Count, ct).ConfigureAwait(false);
                var cacheKey = new AgendaSummaryCacheKey(
                    session.Site, session.ClinicianIdentity, patientId, appointment.Source.Id, clinicDay);
                // A reload re-serves the summary, with its own generation instant, instead of re-charging it.
                if (summaryCache.TryGet(cacheKey, out var cached))
                {
                    rows[index] = new AgendaRow(
                        patientId, displayName, appointment.ScheduledStart!.Value, cached.Summary, cached.SafetyFlags,
                        Failed: false, FailureReason: null, SummaryAsOf: cached.GeneratedAt);
                    return;
                }

                // One summary is one LLM turn, so each one generated is charged.
                if (!turnBudget.TryConsume(sessionId))
                {
                    rows[index] = new AgendaRow(
                        patientId, displayName, appointment.ScheduledStart!.Value, Summary: null, SafetyFlags: [],
                        Failed: true, FailureReason: BudgetExhaustedReason, SummaryAsOf: null);
                    return;
                }

                try
                {
                    var result = await summaryRunner.RunAsync(session.Site, patientId, session.ClinicianIdentity, ct).ConfigureAwait(false);
                    // A fallback is a degraded answer; the next load should try for a real one.
                    if (!result.IsDeterministicFallback)
                    {
                        summaryCache.Store(cacheKey, new AgendaCachedSummary(result.Answer, result.SafetyFlags, now));
                    }

                    rows[index] = new AgendaRow(patientId, displayName, appointment.ScheduledStart!.Value, result.Answer, result.SafetyFlags, Failed: false, FailureReason: null, SummaryAsOf: now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AgendaRosterServiceLog.PatientSummaryFailed(logger, index + 1, roster.Count, ex.GetType());
                    rows[index] = new AgendaRow(
                        patientId, displayName, appointment.ScheduledStart!.Value, Summary: null, SafetyFlags: [],
                        Failed: true, FailureReason: "Summary unavailable for this patient right now.", SummaryAsOf: null);
                }
            }).ConfigureAwait(false);

        return new AgendaResult([.. rows!], now);
    }

    // Best-effort: a failed demographics read must not fail the row (UC-5) - the UI falls back to
    // "Patient {id}". Isolated from the summary so a name lookup can't take a good summary down.
    private async Task<string?> ResolveDisplayNameOrNullAsync(
        string site, string patientId, int index, int rosterSize, CancellationToken cancellationToken)
    {
        try
        {
            var patient = await fhirClient.GetPatientAsync(site, patientId, cancellationToken).ConfigureAwait(false);
            return patient?.DisplayName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var exceptionType = ex.GetType();
            AgendaRosterServiceLog.PatientNameUnavailable(logger, index + 1, rosterSize, exceptionType);
            return null;
        }
    }
}
