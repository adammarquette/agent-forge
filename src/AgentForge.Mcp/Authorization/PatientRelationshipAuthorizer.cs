using System.Collections.Concurrent;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using Microsoft.Extensions.Logging;
using Refit;

namespace AgentForge.Mcp.Authorization;

/// <summary>
/// Resolves <see cref="PatientRelationshipGate"/>'s inputs from OpenEMR: the clinic day's
/// appointments, read with the requester's own token. Registered scoped, and memoized for the
/// lifetime of that scope.
/// </summary>
/// <remarks>
/// The memo is not an optimization detail: one agent turn dispatches its tool calls in parallel
/// (<c>AgentOrchestrator</c>'s <c>Task.WhenAll</c>), so an un-memoized lookup would fire one
/// calendar search per tool call and race them. The cached value is the <see cref="Task{TResult}"/>
/// itself, so concurrent askers share one in-flight lookup rather than starting several. A decision
/// therefore lives exactly as long as one turn (or one agenda fan-out branch) - short enough that a
/// schedule change cannot go unnoticed for long, and long enough to keep the check off the
/// per-tool-call path.
/// <para>
/// The key is clinic day + site + identity + patient and every part of it is load-bearing, so
/// <c>PatientRelationshipAuthorizerTests</c> pins each one on a single instance rather than relying
/// on identity happening to be fixed per DI scope today - dropping the identity from the key would
/// let one requester's permit answer for the next. The clinic day is what the cached rule is
/// *about* ("is there an appointment today"), so it is also what makes a cached answer expire:
/// with it in the key the memo is self-bounding whatever the registered lifetime turns out to be,
/// rather than resting on the scope alone. A separate change.
/// </para>
/// <para>
/// One sharp edge, unreachable today: the first caller's <see cref="CancellationToken"/> is
/// captured into the memoized task, so in a scope where two callers passed different tokens the
/// second would inherit the first's cancellation. Every scope today has a single request token.
/// </para>
/// </remarks>
public sealed class PatientRelationshipAuthorizer(
    IOpenEmrFhirClient fhirClient,
    ClinicClock clinicClock,
    ILogger<PatientRelationshipAuthorizer> logger) : IPatientRelationshipAuthorizer
{
    // Neither a FHIR id nor an OpenEMR site segment can contain a newline, so the composite key is
    // unambiguous without escaping.
    private const char KeySeparator = '\n';

    private readonly ConcurrentDictionary<string, Lazy<Task<PatientRelationshipDecision>>> _decisions =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<PatientRelationshipDecision> AuthorizeAsync(
        string site, string? clinicianIdentity, string patientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clinicianIdentity) || string.IsNullOrEmpty(patientId))
        {
            return Task.FromResult(PatientRelationshipDecision.Unresolved);
        }

        // The clinic day leads the key and is also the exact filter the decision is resolved from,
        // so a memoized answer can never outlive the calendar day it was derived from.
        var clinicDay = clinicClock.TodayDateSearchValue;
        var key = string.Join(KeySeparator, clinicDay, site, clinicianIdentity, patientId);
        return _decisions.GetOrAdd(
            key,
            _ => new Lazy<Task<PatientRelationshipDecision>>(
                () => ResolveAsync(site, clinicianIdentity, patientId, clinicDay, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private async Task<PatientRelationshipDecision> ResolveAsync(
        string site, string clinicianIdentity, string patientId, string clinicDay, CancellationToken cancellationToken)
    {
        try
        {
            var appointments = await fhirClient
                .GetAppointmentsAsync(site, clinicDay, cancellationToken)
                .ConfigureAwait(false);

            return new PatientRelationshipDecision(
                PatientRelationshipGate.Authorize(appointments, clinicianIdentity, patientId),
                appointments.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AccessTokenExpiredException)
        {
            // Fail closed. Refused-because-unresolvable and refused-because-unrelated are
            // indistinguishable to the requester by design, so this line is the only thing that
            // tells an operator which one happened. An expired session is deliberately NOT
            // caught: it is not an authorization decision, and swallowing it told a clinician
            // there was no care relationship to their own patient - and audited that as a refusal
            PatientRelationshipAuthorizerLog.RelationshipUnresolvable(logger, clinicianIdentity, ex.GetType(), StatusCodeOf(ex));
            return PatientRelationshipDecision.Unresolved;
        }
    }

    // Separates "the token cannot read the calendar" (401, as) from an outage.
    private static int? StatusCodeOf(Exception ex) => ex switch
    {
        ApiException api => (int)api.StatusCode,
        HttpRequestException { StatusCode: { } status } => (int)status,
        _ => null,
    };
}
