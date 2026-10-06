using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Auth;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Launch;

/// <summary>
/// Drives the SMART EHR launch authorization-code flow end to end (INTERFACES.md A.3):
/// starts the redirect to OpenEMR's authorize endpoint, then completes the callback by validating
/// state and exchanging the code for a token - producing the <see cref="PatientSessionContext"/>
/// the BFF holds server-side for the rest of the session (ARCHITECTURE.md D11). Kept independent
/// of <see cref="Microsoft.AspNetCore.Http.HttpContext"/> so it is directly unit-testable; the
/// minimal-API endpoints are the thin layer that reads/writes the session around it.
/// </summary>
public sealed class SmartLaunchService(
    IOpenEmrAuthClient authClient,
    IPatientRelationshipAuthorizer relationshipAuthorizer,
    IScopedAccessTokenProvider tokenProvider,
    ICorrelationIdAccessor correlationIdAccessor,
    IOptions<OpenEmrOptions> openEmrOptions,
    IOptions<BffOptions> bffOptions,
    TimeProvider timeProvider,
    ILogger<SmartLaunchService> logger,
    ILogger<AccessAudit> auditLogger)
{
    /// <summary>
    /// Builds the authorize redirect for a launch carrying <paramref name="launchToken"/> (the
    /// SMART <c>launch</c> parameter). Returns the URL to redirect the browser to, and the pending
    /// context the caller must persist (a browser cookie) until the callback arrives.
    /// </summary>
    public (Uri AuthorizeUrl, PendingLaunchContext Pending) BeginLaunch(string? launchToken)
    {
        var options = openEmrOptions.Value;
        var pkce = PkceGenerator.Generate();
        var state = Guid.NewGuid().ToString("n");

        var request = new AuthorizeRequest(
            AuthorizeEndpoint: $"{options.BaseUrl.TrimEnd('/')}/oauth2/{options.Site}/authorize",
            ClientId: options.ClientId,
            RedirectUri: BuildCallbackUri(),
            Scopes: options.Scopes,
            State: state,
            Pkce: pkce,
            // The FHIR base, not the bare server base - OpenEMR rejects the latter with
            // "invalid_request - Aud parameter did not match authorized server" (confirmed live
            // against the QA server; masked until now behind a never-registered OpenEmr__ClientId).
            Aud: $"{options.BaseUrl.TrimEnd('/')}/apis/{options.Site}/fhir",
            Launch: launchToken);

        return (AuthorizeUrlBuilder.Build(request), new PendingLaunchContext(state, pkce.CodeVerifier));
    }

    /// <summary>
    /// Completes the launch: validates <paramref name="state"/> against <paramref name="pending"/>,
    /// exchanges <paramref name="code"/> for a token, and returns the resulting patient session.
    /// </summary>
    /// <exception cref="SmartLaunchException">
    /// The state does not match (possible CSRF), or the token response carries no launch patient
    /// context.
    /// </exception>
    /// <exception cref="SmartLaunchAuthorizationException">
    /// The authenticated requester has no clinical relationship to the launch patient (FR-AUTH-2).
    /// </exception>
    public async Task<PatientSessionContext> CompleteLaunchAsync(
        string code, string state, PendingLaunchContext pending, CancellationToken cancellationToken)
    {
        if (!string.Equals(state, pending.State, StringComparison.Ordinal))
        {
            throw new SmartLaunchException(
                "SMART launch callback state did not match the pending launch - rejecting as a possible CSRF attempt.");
        }

        var options = openEmrOptions.Value;
        var token = await authClient.ExchangeAuthorizationCodeAsync(
            options.Site, code, BuildCallbackUri(), options.ClientId, pending.CodeVerifier, options.ClientSecret, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(token.Patient))
        {
            throw new SmartLaunchException(
                "Token response carried no launch patient context - this product is single-patient-scoped " +
                "and cannot proceed without one (INTERFACES.md A.3).");
        }

        // FR-AUTH-4: every patient-data access must be attributable to who made it. A session with
        // no clinician identity could never be audited, so it must not be allowed to start.
        var introspection = await authClient.IntrospectAsync(
                options.Site, token.AccessToken, options.ClientId, options.ClientSecret, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(introspection.Subject))
        {
            SmartLaunchServiceLog.IntrospectionMissingSubject(logger, introspection.ClientId, introspection.Active);
            throw new SmartLaunchException(
                "Token introspection returned no subject claim - cannot establish an audited clinician " +
                "identity for this session (FR-AUTH-4).");
        }

        if (!introspection.Active)
        {
            SmartLaunchServiceLog.IntrospectionInactive(logger, introspection.ClientId);
            throw new SmartLaunchException(
                "Token introspection reports the token is not active - a revoked or expired token " +
                "cannot start a session (FR-AUTH-4).");
        }

        // Diagnostic only - the gate below still decides. OpenEMR narrows a token to the client's
        // registered scopes without an error, so this is the one place the sidecar can see it.
        if ((introspection.Scope ?? token.Scope) is { } grantedScope)
        {
            var dropped = SmartLaunchScopes.MissingFrom(
                options.Scopes.Where(SmartLaunchScopes.IsResourceScope), grantedScope);
            if (dropped.Count > 0)
            {
                SmartLaunchServiceLog.RequestedScopesNotGranted(
                    logger, introspection.ClientId ?? options.ClientId, string.Join(' ', dropped));
            }
        }

        // FR-AUTH-2, at the earliest point the pair (requester, patient) is both known and
        // settled. OpenEMR's own ACLs are by feature, not by patient panel (ARCHITECTURE.md §5.3),
        // so the chart the EHR let this user open says nothing about entitlement - which is why an
        // `admin` launch used to brief on any chart in the database. A separate change. The
        // relationship lookup is itself a FHIR read, so it runs as the launching user's own token.
        //
        // This is the fail-fast copy of a check McpToolDispatcher makes again below the model
        // (FR-AUTH-3). Refusing here is what makes the denial a clean one - no session is created,
        // so there is no conversation to phrase an attack in and nothing to degrade.
        // The token and the instant it dies, together - the relationship lookup below is itself a
        // FHIR read made with it, so it goes through AuthHandler's expiry guard like every other.
        var expiresAt = ResolveExpiry(introspection, token)
            ?? throw new SmartLaunchException(
                "Neither token introspection nor the token response advertised a lifetime for this "
                + "access token - a session whose expiry is unknown cannot be bounded, and an unbounded "
                + "one goes on presenting as authenticated long after OpenEMR stops accepting it "
                + "");
        tokenProvider.Adopt(token.AccessToken, expiresAt);
        var decision = await relationshipAuthorizer
            .AuthorizeAsync(options.Site, introspection.Subject, token.Patient, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.IsRelated)
        {
            AccessAuditLog.RecordRefusal(
                auditLogger,
                introspection.Subject,
                token.Patient,
                "smart_launch",
                PatientAccessRefusal.AuditReason,
                decision.AuditCount,
                correlationIdAccessor.CorrelationId);

            throw new SmartLaunchAuthorizationException(PatientAccessRefusal.UserFacingMessage);
        }

        return new PatientSessionContext(
            token.AccessToken, options.Site, token.Patient, introspection.Subject, expiresAt);
    }

    /// <summary>
    /// When OpenEMR will stop accepting this token: introspection's <c>exp</c> first, since it is
    /// the authorization server's own answer, then the token response's <c>expires_in</c> - RFC 7662
    /// makes <c>exp</c> optional and this fork's introspection has a history of not following the
    /// RFC (see the inactive-with-a-subject case this class already guards). Null when neither is
    /// advertised.
    /// </summary>
    private DateTimeOffset? ResolveExpiry(IntrospectionResponse introspection, TokenResponse token) =>
        introspection.ExpiresAtUnixSeconds is { } exp
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : token.ExpiresIn is > 0 and { } lifetimeSeconds
                ? timeProvider.GetUtcNow().AddSeconds(lifetimeSeconds)
                : null;

    private string BuildCallbackUri() => $"{bffOptions.Value.PublicBaseUrl.TrimEnd('/')}{bffOptions.Value.CallbackPath}";
}
