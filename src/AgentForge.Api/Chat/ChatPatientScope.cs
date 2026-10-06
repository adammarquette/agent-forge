using AgentForge.Api.Session;

namespace AgentForge.Api.Chat;

/// <summary>
/// The site and patient an outbox message was delivered for. Server-side only: it is how
/// <see cref="IChatMessageOutbox"/> keeps one session's messages apart across a patient switch, and never
/// part of the <see cref="ChatMessage"/> the client receives.
/// </summary>
/// <param name="Site">OpenEMR multi-site segment - part of the patient's identity, since ids are unique only within a site.</param>
/// <param name="PatientId">The patient the message is about.</param>
public sealed record ChatPatientScope(string Site, string PatientId)
{
    /// <summary>The scope of <paramref name="session"/>'s current launch context.</summary>
    public static ChatPatientScope From(PatientSessionContext session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(session.Site, session.PatientId);
    }
}
