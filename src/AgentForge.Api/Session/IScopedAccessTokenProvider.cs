using AgentForge.Integration.OpenEmr.Http;

namespace AgentForge.Api.Session;

/// <summary>
/// The settable half of <see cref="IAccessTokenProvider"/> - a hub method or endpoint adopts one
/// session's token once at the start of a call, and every downstream OpenEMR call within that same
/// logical flow reads it back through <see cref="IAccessTokenProvider"/>.
/// </summary>
public interface IScopedAccessTokenProvider : IAccessTokenProvider
{
    /// <summary>The token to hand out for the remainder of this scope, or <see langword="null"/> if unset.</summary>
    string? AccessToken { get; }

    /// <summary>
    /// Adopts <paramref name="accessToken"/> and the instant it stops being accepted for the
    /// remainder of this scope.
    /// </summary>
    /// <remarks>
    /// The two arrive together and there is deliberately no way to set one without the other: a
    /// token carrying no expiry beside it is indistinguishable from a live one, which is exactly
    /// how a dead token went on being attached to every FHIR call for the rest of a session
    /// </remarks>
    void Adopt(string accessToken, DateTimeOffset expiresAt);
}
