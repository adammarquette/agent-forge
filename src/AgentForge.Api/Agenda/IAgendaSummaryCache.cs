using System.Diagnostics.CodeAnalysis;
using AgentForge.Verification;

namespace AgentForge.Api.Agenda;

/// <summary>
/// Holds each Daily Agenda patient summary for a bounded time, so a roster reload re-serves it instead of
/// re-running and re-charging an LLM turn (ARCHITECTURE.md §19.4). Process-wide; a singleton.
/// </summary>
public interface IAgendaSummaryCache
{
    /// <summary>
    /// Returns the summary stored under exactly <paramref name="key"/>, when one is stored and younger than
    /// <see cref="AgendaOptions.SummaryCacheTtl"/>.
    /// </summary>
    bool TryGet(AgendaSummaryCacheKey key, [NotNullWhen(true)] out AgendaCachedSummary? summary);

    /// <summary>
    /// Stores <paramref name="summary"/> under <paramref name="key"/>, restarting its TTL, and evicts to keep the
    /// cache within <see cref="AgendaOptions.MaxCachedSummaries"/>.
    /// </summary>
    void Store(AgendaSummaryCacheKey key, AgendaCachedSummary summary);
}

/// <summary>
/// What an agenda summary is cached under. Every component must match, compared ordinally, for a hit: the
/// OpenEMR site, the clinician it was generated for, the patient, the appointment and the clinic day.
/// </summary>
/// <param name="Site">The OpenEMR site the roster was read from.</param>
/// <param name="ClinicianIdentity">The clinician whose roster and authorization produced the summary.</param>
/// <param name="PatientId">The rostered patient.</param>
/// <param name="AppointmentId">The FHIR <c>Appointment</c> id the row came from.</param>
/// <param name="ClinicDay">The clinic day the roster was for.</param>
public readonly record struct AgendaSummaryCacheKey(
    string Site, string ClinicianIdentity, string PatientId, string AppointmentId, DateOnly ClinicDay);

/// <summary>The cached part of an agenda row: the summary text, its safety flags and when it was generated.</summary>
/// <param name="Summary">The summary text.</param>
/// <param name="SafetyFlags">The domain-constraint flags raised on it.</param>
/// <param name="GeneratedAt">
/// The start of the load that generated it, re-served as <see cref="AgendaRow.SummaryAsOf"/> on a hit so a reload
/// never presents it as current.
/// </param>
public sealed record AgendaCachedSummary(
    string Summary, IReadOnlyList<DomainConstraintFlag> SafetyFlags, DateTimeOffset GeneratedAt);
