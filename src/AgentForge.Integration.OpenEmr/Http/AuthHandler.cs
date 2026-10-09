using System.Net.Http.Headers;

namespace AgentForge.Integration.OpenEmr.Http;

/// <summary>
/// Attaches the clinician's bearer token to every outbound OpenEMR call
/// (CONVENTIONS.md §4). The token itself is never hard-coded and never logged; it is
/// resolved per-request from <see cref="IAccessTokenProvider"/>. No token means no call: FR-AUTH-1
/// requires an unauthenticated request be rejected before any tool runs, so this layer refuses to
/// send rather than letting an unauthenticated call go out for OpenEMR's own 401 to catch after
/// the fact - and a token whose lifetime has run out is the same condition.
/// </summary>
/// <remarks>
/// The expiry check is the part that is not obvious. OpenEMR issues one-hour access tokens; the
/// BFF session holding one slides on every request, so an <em>active</em> session routinely
/// outlives its own token. Until this check existed the only thing that noticed was OpenEMR, one
/// 401 per FHIR read, and NFR-REL-1's degradation turned that into a completed brief written from
/// almost no chart - 24 of 30 Week 1 tool calls on the live deployment.
/// </remarks>
public sealed class AuthHandler(IAccessTokenProvider tokenProvider, TimeProvider timeProvider) : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            throw new UnauthenticatedRequestException(
                "No access token is available for this request - refusing to send it unauthenticated (FR-AUTH-1).");
        }

        if (tokenProvider.AccessTokenExpiresAt is { } expiresAt
            && AccessTokenLifetime.HasExpired(expiresAt, timeProvider.GetUtcNow()))
        {
            throw new AccessTokenExpiredException(
                "This SMART session's OpenEMR access token has expired - launch AgentForge again from the " +
                "chart to continue. The call was refused here rather than sent for OpenEMR to reject.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
