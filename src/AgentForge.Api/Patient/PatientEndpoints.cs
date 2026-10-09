using AgentForge.Api.Session;
using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Patient;

/// <summary>
/// The per-patient landing page's one data endpoint: the launched patient's identity + FHIR
/// reachability, for the confirmation page served at <c>index.html</c>. Thin by design - the same
/// shape as <c>AgendaEndpoints</c>, leaving the work in <see cref="PatientContextService"/>.
/// </summary>
public static class PatientEndpoints
{
    /// <summary>Maps <c>GET /patient</c> - the launched-patient context confirmation.</summary>
    public static IEndpointRouteBuilder MapPatientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/patient", HandleGetPatientAsync)
            .WithName("PatientContext")
            .WithSummary("The launched patient's context for the confirmation page.")
            .Produces<PatientContextResponsePayload>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return endpoints;
    }

    // Internal for its unit tests. The payload carries the key the page's hub connection presents.
    internal static async Task<IResult> HandleGetPatientAsync(
        HttpContext httpContext, PatientContextService service, TimeProvider timeProvider)
    {
        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        // An aged-out session is refused here rather than rendering a page of unreachable
        // sections - the read itself will not return one.
        var session = httpContext.Session.TryGetPatientSession(timeProvider);
        if (session is null)
        {
            return Results.Unauthorized();
        }

        var result = await service.BuildAsync(session, httpContext.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToPayload(result, PatientContextBinding.KeyFor(httpContext.Session.Id, session)));
    }

    private static PatientContextResponsePayload ToPayload(PatientContextResult result, string contextKey) => new(
        result.PatientId,
        result.DisplayName,
        result.BirthDate,
        result.Gender,
        result.Demographics,
        result.Problems,
        result.Medications,
        result.Allergies,
        contextKey);
}
