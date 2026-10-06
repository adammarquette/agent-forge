using System.Diagnostics.CodeAnalysis;
using AgentForge.Api.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Chat;

/// <summary>
/// Resolves the caller's patient session for <see cref="ChatHub"/> traffic while the request is
/// still an ordinary HTTP request, and hands the result to the hub through
/// <see cref="HttpContext.Items"/> (<see cref="ChatHubSessionItems"/>). ASP.NET Core does not
/// support session state inside SignalR: under long-polling the hub is handed a clone of the
/// connecting request that carries <c>Items</c> but not <c>ISessionFeature</c>, so the hub reading
/// <c>HttpContext.Session</c> itself threw on that transport. Every transport's connecting request
/// does run this middleware, and every transport's hub sees its <c>Items</c>. A separate change
/// </summary>
/// <remarks>
/// Must run after <c>UseSession()</c>. Touches only requests under <see cref="ChatHub.Route"/>, and
/// resolves through <see cref="Session.SessionExtensions.TryGetPatientSession"/> - the same call every HTTP
/// surface makes - so an expired or half-populated session reaches the hub as no session at all,
/// exactly as it did before.
/// </remarks>
public sealed class ChatHubSessionMiddleware(RequestDelegate next, TimeProvider timeProvider)
{
    /// <summary>Resolves the session for hub requests, then passes every request on.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.StartsWithSegments(ChatHub.Route))
        {
            await context.Session.LoadAsync(context.RequestAborted).ConfigureAwait(false);
            ChatHubSessionItems.Set(context, context.Session.Id, context.Session.TryGetPatientSession(timeProvider));
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// The <see cref="HttpContext.Items"/> entries <see cref="ChatHubSessionMiddleware"/> writes and
/// <see cref="ChatHub"/> reads. Keyed by private object instances rather than strings, so nothing
/// but this type can write an entry the hub will trust. Server-side only: nothing here reaches a
/// cookie, a claim, a header or a log line.
/// </summary>
public static class ChatHubSessionItems
{
    private static readonly object SessionIdKey = new();
    private static readonly object SessionKey = new();

    /// <summary>
    /// Records the resolved session for this request: its server-side id always, and the patient
    /// context when there is a live one.
    /// </summary>
    public static void Set(HttpContext httpContext, string sessionId, PatientSessionContext? session)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        httpContext.Items[SessionIdKey] = sessionId;
        httpContext.Items[SessionKey] = session;
    }

    /// <summary>
    /// Reads what <see cref="Set"/> recorded. <see langword="false"/> means nothing resolved the
    /// session for this request at all, which is a composition fault rather than an unauthenticated
    /// caller; <see langword="true"/> with a <see langword="null"/> <paramref name="session"/> is a
    /// browser with no live patient session.
    /// </summary>
    public static bool TryGet(
        HttpContext httpContext,
        [NotNullWhen(true)] out string? sessionId,
        out PatientSessionContext? session)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        sessionId = httpContext.Items.TryGetValue(SessionIdKey, out var id) ? id as string : null;
        session = sessionId is not null && httpContext.Items.TryGetValue(SessionKey, out var value)
            ? value as PatientSessionContext
            : null;
        return sessionId is not null;
    }
}

/// <summary>Registers <see cref="ChatHubSessionMiddleware"/>.</summary>
public static class ChatHubSessionMiddlewareExtensions
{
    /// <summary>Adds <see cref="ChatHubSessionMiddleware"/>; call after <c>UseSession()</c>.</summary>
    public static IApplicationBuilder UseChatHubSession(this IApplicationBuilder app) =>
        app.UseMiddleware<ChatHubSessionMiddleware>();
}
