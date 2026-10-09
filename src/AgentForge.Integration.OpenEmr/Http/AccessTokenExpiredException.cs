namespace AgentForge.Integration.OpenEmr.Http;

/// <summary>
/// The session's OpenEMR access token has passed its expiry, so the call was refused before it was
/// sent. Distinct from <see cref="UnauthenticatedRequestException"/> (there was never a token) and
/// from a downstream 401 (this call never left the process): the session exists and is otherwise
/// valid, it has simply aged out, and only a fresh SMART launch recovers it.
/// </summary>
/// <remarks>
/// Its own type because the two conditions it separates are not the same conversation with the
/// clinician. OpenEMR issues one-hour access tokens while the BFF session holding one slides on
/// every request, so an active session outlives its own token - and until this type existed the
/// result was an anonymous 401 on every FHIR read, which graceful degradation (NFR-REL-1) turned
/// into a brief written from almost no chart.
/// </remarks>
public sealed class AccessTokenExpiredException : Exception
{
    /// <summary>Creates a new <see cref="AccessTokenExpiredException"/>.</summary>
    public AccessTokenExpiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
