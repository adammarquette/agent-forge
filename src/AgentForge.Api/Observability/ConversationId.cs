using System.Security.Cryptography;
using System.Text;

namespace AgentForge.Api.Observability;

/// <summary>
/// The log key that joins the turns of one chat. <see cref="CorrelationIdMiddleware"/> establishes
/// an id per request, and <c>ChatSessionCoordinator</c> opens its scope per turn, so a correlation
/// id answers "what happened in this turn" and nothing answers "which conversation was it"
/// (FR-OBS-1, <c>REQUIREMENTS.md</c> UC-2).
/// </summary>
/// <remarks>
/// Derived from the session id rather than minted, so it needs no storage and is stable across
/// turns for free. It is a <b>one-way</b> derivation because the input is the server-side session
/// key: logging that value would put a credential-shaped string in the same index as everything
/// else, and the <c>no_phi_in_logs</c> eval rubric scores per-case PHI tokens, not secrets, so
/// nothing downstream would catch it.
/// </remarks>
public static class ConversationId
{
    // 64 bits of a SHA-256 digest. Wide enough that a collision is not a practical concern at any
    // log volume this system will see, short enough to read in a line beside the correlation id.
    private const int Bytes = 8;

    /// <summary>Derives the stable, non-reversible conversation id for <paramref name="sessionId"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null, empty or whitespace.</exception>
    public static string From(string sessionId)
    {
        // A blank session id has no conversation to key on. Hashing it anyway would join every
        // such turn under one plausible-looking id, which reads as data rather than as the bug.
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return Convert.ToHexStringLower(digest.AsSpan(0, Bytes));
    }
}
