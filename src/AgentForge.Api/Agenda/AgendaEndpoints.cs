using AgentForge.Api.Chat;
using AgentForge.Api.Launch;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Agenda;

/// <summary>
/// The Daily Agenda's own HTTP surface (ARCHITECTURE.md §19): the roster fetch, and the
/// drill-down that hands a selected patient off to the existing, completely unchanged
/// single-patient chat. Thin by design - the same shape as <see cref="LaunchEndpoints"/> and
/// <see cref="AgendaLaunchEndpoints"/>, leaving the real logic in already-tested services.
/// </summary>
public static class AgendaEndpoints
{
    /// <summary>Maps the Daily Agenda's roster and drill-down endpoints.</summary>
    public static IEndpointRouteBuilder MapAgendaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/agenda", HandleGetAgendaAsync)
            .WithName("AgendaRoster")
            .WithSummary("The launched clinician's roster for the clinic day.")
            .Produces<AgendaResponsePayload>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        endpoints.MapPost("/agenda/select-patient", HandleSelectPatientAsync)
            .WithName("AgendaSelectPatient")
            .WithSummary("Scope the session to one rostered patient and redirect to the chat SPA.")
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> HandleGetAgendaAsync(
        HttpContext httpContext, AgendaRosterService rosterService,
        ExpiredSessionSignal expiredSession, TimeProvider timeProvider)
    {
        var session = await GetAuthenticatedAgendaSessionOrNullAsync(httpContext, timeProvider).ConfigureAwait(false);
        if (session is null)
        {
            return Results.Unauthorized();
        }

        AgendaResult agenda;
        try
        {
            agenda = await rosterService.BuildAgendaAsync(httpContext.Session.Id, session, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (AccessTokenExpiredException)
        {
            // The roster fan-out is the longest-running flow in the product, so "live at the session
            // read, dead by the clinic-day search" is a real window here rather than a theoretical
            // one. Same answer as an already-expired session, and counted as one.
            expiredSession.Record(ExpiredSessionSurface.Agenda, httpContext.Session.Id);
            return Results.Unauthorized();
        }

        httpContext.Session.SaveAgendaRoster(agenda.Rows.Select(r => r.PatientId));
        await httpContext.Session.CommitAsync(httpContext.RequestAborted).ConfigureAwait(false);

        return Results.Ok(ToPayload(agenda));
    }

    private static async Task<IResult> HandleSelectPatientAsync(
        HttpContext httpContext, IOptions<BffOptions> bffOptions, ILoggerFactory loggerFactory,
        ICorrelationIdAccessor correlationIdAccessor, TimeProvider timeProvider, string patientId)
    {
        var session = await GetAuthenticatedAgendaSessionOrNullAsync(httpContext, timeProvider).ConfigureAwait(false);
        if (session is null)
        {
            return Results.Unauthorized();
        }

        var roster = httpContext.Session.TryGetAgendaRoster();
        var logger = loggerFactory.CreateLogger(nameof(AgendaEndpoints));
        if (!AgendaRosterGate.AuthorizeAndAudit(
                roster, session.ClinicianIdentity, patientId, correlationIdAccessor.CorrelationId,
                loggerFactory.CreateLogger<AccessAudit>()))
        {
            AgendaEndpointsLog.PatientNotInRoster(logger, session.ClinicianIdentity);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var patientSession = new PatientSessionContext(
            session.AccessToken, session.Site, patientId, session.ClinicianIdentity, session.ExpiresAt);
        httpContext.Session.SavePatientSession(patientSession);
        await httpContext.Session.CommitAsync(httpContext.RequestAborted).ConfigureAwait(false);

        // Prefix the reverse-proxy PathBase (/agentforge), same as the launch endpoints - a bare
        // "/index.html" redirect lands at the proxy root and 404s (the drill-down's 404-in-tab bug).
        return Results.Redirect(httpContext.Request.PathBase.Add(bffOptions.Value.ChatPath).ToString());
    }

    private static async Task<AgendaSessionContext?> GetAuthenticatedAgendaSessionOrNullAsync(
        HttpContext httpContext, TimeProvider timeProvider)
    {
        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        return httpContext.Session.TryGetAgendaSession(timeProvider);
    }

    internal static AgendaResponsePayload ToPayload(AgendaResult result) => new(
        [.. result.Rows.Select(r => new AgendaRowPayload(
            r.PatientId,
            r.DisplayName,
            r.ScheduledStart,
            r.Summary,
            [.. r.SafetyFlags.Select(f => new SafetyFlagPayload(f.RuleId, f.Description, [.. f.Sources.Select(s => s.Citation)]))],
            r.Failed,
            r.FailureReason,
            r.SummaryAsOf))],
        result.AsOf);
}
