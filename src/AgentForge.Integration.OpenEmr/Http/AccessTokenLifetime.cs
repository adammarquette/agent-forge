namespace AgentForge.Integration.OpenEmr.Http;

/// <summary>
/// The one definition of "this OpenEMR access token is no longer usable", shared by every layer
/// that has to decide it — the session read that refuses to hand a dead session out, the hub method
/// that refuses to start a turn on one, and <see cref="AuthHandler"/> guarding the call itself.
/// </summary>
/// <remarks>
/// A one-line rule in four places is a one-line rule that drifts, and the boundary is the part that
/// would drift: <c>&lt;=</c>, not <c>&lt;</c>. At the expiry instant OpenEMR has already stopped
/// accepting the token, and the round trip only widens the gap — so the boundary sits on the
/// refusing side, where being wrong costs a re-launch rather than a 401.
/// </remarks>
public static class AccessTokenLifetime
{
    /// <summary>Whether a token expiring at <paramref name="expiresAt"/> is already dead at <paramref name="now"/>.</summary>
    public static bool HasExpired(DateTimeOffset expiresAt, DateTimeOffset now) => expiresAt <= now;
}
