namespace AgentForge.Api.Launch;

/// <summary>
/// The launch was well-formed and the requester authenticated, but they have no clinical
/// relationship to the launch patient (FR-AUTH-2). Distinct from its base
/// <see cref="SmartLaunchException"/> because the two mean opposite things to a caller: a
/// malformed or replayed callback is a client error to fix, an entitlement refusal is the system
/// working, and only the latter is a 403.
/// </summary>
public sealed class SmartLaunchAuthorizationException(string message) : SmartLaunchException(message);
