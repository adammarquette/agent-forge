using System.Globalization;
using System.Text.Json;
using AgentForge.Integration.OpenEmr.Http;
using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Session;

/// <summary>
/// Reads and writes <see cref="PatientSessionContext"/> to the ASP.NET Core session - the
/// server-side, cookie-keyed store the token never leaves (ARCHITECTURE.md D11).
/// </summary>
public static class SessionExtensions
{
    private const string AccessTokenKey = "patient-session.access-token";
    private const string SiteKey = "patient-session.site";
    private const string PatientIdKey = "patient-session.patient-id";
    private const string ClinicianIdentityKey = "patient-session.clinician-identity";
    private const string ExpiresAtKey = "patient-session.expires-at";

    /// <summary>Saves <paramref name="context"/> into <paramref name="session"/>.</summary>
    public static void SavePatientSession(this ISession session, PatientSessionContext context)
    {
        session.SetString(AccessTokenKey, context.AccessToken);
        session.SetString(SiteKey, context.Site);
        session.SetString(PatientIdKey, context.PatientId);
        session.SetString(ClinicianIdentityKey, context.ClinicianIdentity);
        session.SetString(ExpiresAtKey, FormatInstant(context.ExpiresAt));
    }

    /// <summary>
    /// Reads the patient session from <paramref name="session"/>, or <see langword="null"/> if no
    /// session was saved, it is only partially populated, or its access token has expired by
    /// <paramref name="timeProvider"/>'s clock - a half-populated session must never be treated as
    /// authenticated (the product is single-patient-scoped for its entire lifetime), and neither
    /// must an aged-out one.
    /// </summary>
    /// <remarks>
    /// The expiry check lives here, in the one call every HTTP surface and the hub already make,
    /// rather than in each of them: there is exactly one way to obtain a session and it cannot
    /// return a dead one, so a surface added later inherits the refusal instead of having to
    /// remember it. What it cannot cover is a token that dies *after* the read - see
    /// <see cref="AccessTokenExpiredException"/>'s callers. A separate change
    /// </remarks>
    public static PatientSessionContext? TryGetPatientSession(this ISession session, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var accessToken = session.GetString(AccessTokenKey);
        var site = session.GetString(SiteKey);
        var patientId = session.GetString(PatientIdKey);
        var clinicianIdentity = session.GetString(ClinicianIdentityKey);
        var expiresAt = TryReadInstant(session.GetString(ExpiresAtKey));

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(site) ||
            string.IsNullOrEmpty(patientId) || string.IsNullOrEmpty(clinicianIdentity) ||
            expiresAt is null)
        {
            return null;
        }

        return AccessTokenLifetime.HasExpired(expiresAt.Value, timeProvider.GetUtcNow())
            ? null
            : new PatientSessionContext(accessToken, site, patientId, clinicianIdentity, expiresAt.Value);
    }

    /// <summary>Removes the patient session from <paramref name="session"/>, if present.</summary>
    public static void ClearPatientSession(this ISession session)
    {
        session.Remove(AccessTokenKey);
        session.Remove(SiteKey);
        session.Remove(PatientIdKey);
        session.Remove(ClinicianIdentityKey);
        session.Remove(ExpiresAtKey);
    }

    private const string AgendaAccessTokenKey = "agenda-session.access-token";
    private const string AgendaSiteKey = "agenda-session.site";
    private const string AgendaClinicianIdentityKey = "agenda-session.clinician-identity";
    private const string AgendaExpiresAtKey = "agenda-session.expires-at";

    /// <summary>
    /// Saves <paramref name="context"/> into <paramref name="session"/>, under a distinct key
    /// prefix from <see cref="SavePatientSession"/> so a clinician mid-flow on both a single-patient
    /// launch and an agenda launch in the same browser session can't have one clobber the other
    /// (ARCHITECTURE.md §19).
    /// </summary>
    public static void SaveAgendaSession(this ISession session, AgendaSessionContext context)
    {
        session.SetString(AgendaAccessTokenKey, context.AccessToken);
        session.SetString(AgendaSiteKey, context.Site);
        session.SetString(AgendaClinicianIdentityKey, context.ClinicianIdentity);
        session.SetString(AgendaExpiresAtKey, FormatInstant(context.ExpiresAt));
    }

    /// <summary>
    /// Reads the agenda session from <paramref name="session"/>, or <see langword="null"/> if no
    /// session was saved, it is only partially populated, or its access token has expired - same
    /// reasoning as <see cref="TryGetPatientSession"/>, which carries it.
    /// </summary>
    public static AgendaSessionContext? TryGetAgendaSession(this ISession session, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var accessToken = session.GetString(AgendaAccessTokenKey);
        var site = session.GetString(AgendaSiteKey);
        var clinicianIdentity = session.GetString(AgendaClinicianIdentityKey);
        var expiresAt = TryReadInstant(session.GetString(AgendaExpiresAtKey));

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(site) ||
            string.IsNullOrEmpty(clinicianIdentity) || expiresAt is null)
        {
            return null;
        }

        return AccessTokenLifetime.HasExpired(expiresAt.Value, timeProvider.GetUtcNow())
            ? null
            : new AgendaSessionContext(accessToken, site, clinicianIdentity, expiresAt.Value);
    }

    /// <summary>Removes the agenda session from <paramref name="session"/>, if present.</summary>
    public static void ClearAgendaSession(this ISession session)
    {
        session.Remove(AgendaAccessTokenKey);
        session.Remove(AgendaSiteKey);
        session.Remove(AgendaClinicianIdentityKey);
        session.Remove(AgendaExpiresAtKey);
    }

    private const string AgendaRosterPatientIdsKey = "agenda-session.roster-patient-ids";

    /// <summary>
    /// Saves the patient ids from the most recently fetched agenda - the drill-down gate
    /// (<see cref="Agenda.AgendaRosterGate"/>) checks a selected patient against this, so a client
    /// can never select a patient outside what its own agenda actually returned.
    /// </summary>
    public static void SaveAgendaRoster(this ISession session, IEnumerable<string> patientIds) =>
        session.SetString(AgendaRosterPatientIdsKey, JsonSerializer.Serialize(patientIds));

    /// <summary>
    /// Reads the roster saved by <see cref="SaveAgendaRoster"/>, or an empty set if none was ever
    /// saved - deliberately not nullable, since the gate can use an empty set directly (it
    /// correctly rejects everything) without a separate null check.
    /// </summary>
    public static IReadOnlySet<string> TryGetAgendaRoster(this ISession session)
    {
        var json = session.GetString(AgendaRosterPatientIdsKey);
        if (string.IsNullOrEmpty(json))
        {
            return new HashSet<string>();
        }

        return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
    }

    // Round-trip ("O") and invariant culture, both load-bearing: this instant decides whether a
    // session is still usable, so a formatter that dropped sub-second precision or read back at the
    // container's culture would refuse a live session or admit a dead one.
    private static string FormatInstant(DateTimeOffset instant) =>
        instant.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an instant written by <see cref="FormatInstant"/>, or <see langword="null"/> when the
    /// value is absent or unreadable. Absent is the normal case for a session written by a build
    /// from before the expiry was recorded at all - and it must read as "not authenticated", so a
    /// rolling deploy lands those users on a re-launch rather than on an unbounded session.
    /// </summary>
    private static DateTimeOffset? TryReadInstant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            ? instant
            : null;
}
