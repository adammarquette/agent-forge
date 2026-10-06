namespace AgentForge.Api.Launch;

/// <summary>
/// The CSRF state and PKCE verifier minted for one in-flight SMART launch, held between the initial
/// redirect and the callback in a DataProtection-encrypted browser cookie (<see cref="PendingLaunchCookie"/>).
/// </summary>
/// <param name="State">Opaque value echoed back by the authorization server; verified at the callback.</param>
/// <param name="CodeVerifier">RFC 7636 PKCE verifier, exchanged for the token at the callback.</param>
public sealed record PendingLaunchContext(string State, string CodeVerifier);
