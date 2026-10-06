using AgentForge.Integration.OpenEmr.Http;

namespace AgentForge.Mcp.Authorization;

/// <summary>
/// Resolves the FR-AUTH-2 relationship between a requester and a patient against live OpenEMR
/// data. Separated from <see cref="PatientRelationshipGate"/> (which decides) so callers can be
/// unit-tested against a fake, and so the lookup can be memoized per scope.
/// </summary>
public interface IPatientRelationshipAuthorizer
{
    /// <summary>
    /// Whether <paramref name="clinicianIdentity"/> may access <paramref name="patientId"/>'s data
    /// on <paramref name="site"/>, and how many clinic-day appointments that was decided against.
    /// </summary>
    /// <exception cref="AccessTokenExpiredException">
    /// The requester's SMART session has aged out. Not an entitlement outcome - see the remarks.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. Also not an entitlement outcome.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Every entitlement outcome is a returned refusal, never an exception.</b> No requester
    /// identity, no patient, an empty clinic day, no matching provider participant, and a lookup
    /// that errored are all a <see cref="PatientRelationshipDecision"/> with <c>IsRelated: false</c>
    /// - the last of them because falling open on a failed lookup would turn every OpenEMR outage
    /// into an authorization bypass.
    /// </para>
    /// <para>
    /// <b>The two exceptions above escape because neither one decides anything.</b> This check
    /// reads <c>Appointment</c> with the requester's own token, so an expired session reaches it
    /// before anything else - and converting that into a refusal told a clinician there was no care
    /// relationship to their own patient, and wrote an FR-AUTH-4 denial saying so, when the true
    /// answer is that the session ended and only a fresh SMART launch recovers it. Cancellation is
    /// the same shape: a caller that gave up has not answered the entitlement question either.
    /// </para>
    /// <para>
    /// <b>So a caller must handle the expired session</b> the way it already handles an
    /// unauthenticated one - 401, or the hub's session-expired message - and must neither audit it
    /// as an FR-AUTH-4 refusal nor count it as an authorization decision, because the audit trail
    /// and <c>agentforge_authorization_decisions_total</c> both mean entitlement and an aged-out
    /// token is not an entitlement fact. Cancellation propagates as it does everywhere else; it is
    /// named above only so that what escapes is a closed list.
    /// METRICS.md §3
    /// </para>
    /// </remarks>
    Task<PatientRelationshipDecision> AuthorizeAsync(
        string site, string? clinicianIdentity, string patientId, CancellationToken cancellationToken);
}
