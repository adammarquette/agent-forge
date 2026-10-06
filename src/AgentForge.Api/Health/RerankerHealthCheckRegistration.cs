using AgentForge.Retrieval.Cohere;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Api.Health;

/// <summary>
/// Registers the typed <see cref="HttpClient"/> <see cref="RerankerHealthCheck"/> probes through.
/// </summary>
public static class RerankerHealthCheckRegistration
{
    /// <summary>
    /// Adds the check's typed client behind <see cref="CohereAuthHandler"/>, the same handler every Cohere
    /// call goes through, so the probe proves the key as well as the route. Registered here rather than
    /// inline so the wiring is a testable claim.
    /// </summary>
    public static IServiceCollection AddRerankerHealthCheckClient(this IServiceCollection services)
    {
        services.TryAddTransient<CohereAuthHandler>();
        services.AddHttpClient<RerankerHealthCheck>().AddHttpMessageHandler<CohereAuthHandler>();
        return services;
    }
}
