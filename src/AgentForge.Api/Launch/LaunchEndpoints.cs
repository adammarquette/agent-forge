using System.Text;
using AgentForge.Api.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Launch;

/// <summary>
/// Thin HTTP layer around <see cref="SmartLaunchService"/>: reads/writes the browser session and
/// issues redirects, leaving the actual launch/callback logic in the already-tested service.
/// </summary>
public static class LaunchEndpoints
{
    private const string PendingLaunchFlow = "chart";

    /// <summary>Maps the SMART EHR launch and callback endpoints.</summary>
    public static IEndpointRouteBuilder MapLaunchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/launch", HandleLaunch)
            .WithName("SmartLaunch")
            .WithSummary("Begin the per-patient SMART EHR launch.")
            .Produces(StatusCodes.Status302Found);

        endpoints.MapGet("/callback", HandleCallbackAsync)
            .WithName("SmartLaunchCallback")
            .WithSummary("OAuth redirect back from OpenEMR; establishes the patient session.")
            .Produces(StatusCodes.Status302Found)
            .Produces<string>(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static IResult HandleLaunch(
        HttpContext httpContext, SmartLaunchService launchService, IDataProtectionProvider dataProtection,
        TimeProvider timeProvider, IOptions<BffOptions> bffOptions, string? iss, string? launch)
    {
        _ = iss; // Present per SMART launch (INTERFACES.md A.3); not needed beyond the configured connection (v1: single fixed OpenEMR deployment).
        var (authorizeUrl, pending) = launchService.BeginLaunch(launch);

        // A cookie, not the session: this route is unauthenticated. A separate change
        PendingLaunchCookie.Add(httpContext, PendingLaunchFlow, pending, dataProtection, timeProvider, bffOptions.Value);

        return Results.Redirect(authorizeUrl.ToString());
    }

    private static async Task<IResult> HandleCallbackAsync(
        HttpContext httpContext, SmartLaunchService launchService, IDataProtectionProvider dataProtection,
        TimeProvider timeProvider, IOptions<BffOptions> bffOptions, string code, string state)
    {
        var pending = PendingLaunchCookie.Take(
            httpContext, PendingLaunchFlow, state, dataProtection, timeProvider, bffOptions.Value);
        if (pending is null)
        {
            return Results.BadRequest("No pending SMART launch for this session.");
        }

        PatientSessionContext session;
        try
        {
            session = await launchService.CompleteLaunchAsync(code, state, pending, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        // Order matters: the authorization refusal derives from SmartLaunchException, and it is a
        // 403 (entitlement), not a 400 (malformed callback) - REQUIREMENTS.md section 11's authorization-denied
        // row. The message is PatientAccessRefusal.UserFacingMessage, which carries no patient detail.
        catch (SmartLaunchAuthorizationException ex)
        {
            return Results.Text(ex.Message, "text/plain", Encoding.UTF8, StatusCodes.Status403Forbidden);
        }
        catch (SmartLaunchException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        httpContext.Session.SavePatientSession(session);
        await httpContext.Session.CommitAsync(httpContext.RequestAborted).ConfigureAwait(false);

        // Prefix the reverse-proxy PathBase (/agentforge) - a bare "/index.html" redirect lands at
        // the proxy root, which isn't routed to this service, so it 404s.
        return Results.Redirect(httpContext.Request.PathBase.Add(bffOptions.Value.ChatPath).ToString());
    }
}
