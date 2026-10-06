using Microsoft.AspNetCore.Builder;

namespace AgentForge.Api.Observability;

/// <summary>Composition-root helper for <see cref="CorrelationIdMiddleware"/>.</summary>
internal static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds the correlation-id scope to the pipeline. Register it before anything whose work
    /// should appear in the trace - session load, static files, routing, endpoints.
    /// </summary>
    internal static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}
