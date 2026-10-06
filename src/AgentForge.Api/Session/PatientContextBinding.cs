using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentForge.Api.Session;

/// <summary>
/// Binds a chat page to the patient it was rendered for. <c>GET /patient</c> hands the page a key for the
/// session's current site and patient; the page presents it on every hub connection, reconnects included;
/// <c>ChatHub</c> refuses a connection whose key no longer matches the session's patient. Needed because the
/// session id outlives a patient switch: another tab's drill-down, or a second launch on the same cookie,
/// changes the patient under a page that is still showing the previous one. A separate change
/// </summary>
/// <remarks>
/// A one-way digest of the server-side session id, site and patient id. It travels in the hub URL's query
/// string, which a reverse proxy logs, so it must name neither the patient nor the session. It authenticates
/// nothing - the session cookie does that - it only says which patient the page shows.
/// </remarks>
public static class PatientContextBinding
{
    /// <summary>The hub URL query parameter the page presents its key in.</summary>
    public const string QueryParameter = "context";

    // 128 bits: the key is compared, never searched, so width only has to rule out an accidental match.
    private const int Bytes = 16;

    /// <summary>The key a page rendered for <paramref name="session"/>'s current patient presents.</summary>
    public static string KeyFor(string sessionId, PatientSessionContext session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(session);

        return Convert.ToHexStringLower(Digest(sessionId, session));
    }

    /// <summary>
    /// Whether <paramref name="presentedKey"/> is the key for <paramref name="session"/>'s current site and
    /// patient. A missing or malformed key is a mismatch: nothing then says which patient the page shows.
    /// </summary>
    public static bool Matches(string? presentedKey, string sessionId, PatientSessionContext session)
    {
        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length != Bytes * 2)
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(presentedKey);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, Digest(sessionId, session));
    }

    // Length-prefixed, so no choice of site and patient id can collide with another split of the same bytes.
    private static byte[] Digest(string sessionId, PatientSessionContext session)
    {
        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId.Length}:{sessionId}{session.Site.Length}:{session.Site}{session.PatientId.Length}:{session.PatientId}");
        return SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, Bytes).ToArray();
    }
}
