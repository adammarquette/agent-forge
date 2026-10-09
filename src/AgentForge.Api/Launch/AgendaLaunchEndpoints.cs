using AgentForge.Api.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Launch;

/// <summary>
/// Thin HTTP layer around <see cref="AgendaLaunchService"/> - the mirror image of
/// <see cref="LaunchEndpoints"/>: reads/writes the browser session and issues redirects, leaving
/// the actual launch/callback logic in the already-tested service (ARCHITECTURE.md §19).
/// </summary>
public static class AgendaLaunchEndpoints
{
    private const string PendingLaunchFlow = "agenda";

    /// <summary>Maps the Daily Agenda SMART launch and callback endpoints.</summary>
    public static IEndpointRouteBuilder MapAgendaLaunchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/agenda/launch", HandleLaunch)
            .WithName("AgendaLaunch")
            .WithSummary("Begin the Daily Agenda (roster) SMART launch.")
            .Produces(StatusCodes.Status302Found);

        endpoints.MapGet("/agenda/callback", HandleCallbackAsync)
            .WithName("AgendaLaunchCallback")
            .WithSummary("OAuth redirect back from OpenEMR; establishes the agenda session.")
            .Produces(StatusCodes.Status302Found)
            .Produces<string>(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static IResult HandleLaunch(
        HttpContext httpContext, AgendaLaunchService launchService, IDataProtectionProvider dataProtection,
        TimeProvider timeProvider, IOptions<BffOptions> bffOptions, string? iss, string? launch)
    {
        _ = iss; // Present per SMART launch (INTERFACES.md A.3); not needed beyond the configured connection (v1: single fixed OpenEMR deployment).
        var (authorizeUrl, pending) = launchService.BeginLaunch(launch);

        // A cookie, not the session: this route is unauthenticated.
        PendingLaunchCookie.Add(httpContext, PendingLaunchFlow, pending, dataProtection, timeProvider, bffOptions.Value);

        return Results.Redirect(authorizeUrl.ToString());
    }

    private static async Task<IResult> HandleCallbackAsync(
        HttpContext httpContext, AgendaLaunchService launchService, IDataProtectionProvider dataProtection,
        TimeProvider timeProvider, IOptions<BffOptions> bffOptions, string code, string state)
    {
        var pending = PendingLaunchCookie.Take(
            httpContext, PendingLaunchFlow, state, dataProtection, timeProvider, bffOptions.Value);
        if (pending is null)
        {
            return Results.BadRequest("No pending agenda launch for this session.");
        }

        AgendaSessionContext session;
        try
        {
            session = await launchService.CompleteLaunchAsync(code, state, pending, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (AgendaLaunchException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        httpContext.Session.SaveAgendaSession(session);
        await httpContext.Session.CommitAsync(httpContext.RequestAborted).ConfigureAwait(false);

        // Prefix the reverse-proxy PathBase (/agentforge) - a bare "/agenda" redirect lands at the
        // proxy root, which isn't routed to this service, so it 404s.
        return Results.Redirect(httpContext.Request.PathBase.Add(bffOptions.Value.AgendaPath).ToString());
    }
}
