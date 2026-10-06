using AgentForge.Llm.Anthropic;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Api.Health;

/// <summary>
/// Registers the typed <see cref="HttpClient"/> <see cref="LlmProviderHealthCheck"/> probes through.
/// </summary>
public static class LlmProviderHealthCheckRegistration
{
    /// <summary>
    /// Adds the check's typed client behind <see cref="AnthropicAuthHandler"/>, the same handler every
    /// model call goes through, so the probe proves the key as well as the route. Registered here rather
    /// than inline so the wiring is a testable claim: without the handler the probe goes out keyless and
    /// every deployment reads 401.
    /// </summary>
    public static IServiceCollection AddLlmProviderHealthCheckClient(this IServiceCollection services)
    {
        services.TryAddTransient<AnthropicAuthHandler>();
        services.AddHttpClient<LlmProviderHealthCheck>().AddHttpMessageHandler<AnthropicAuthHandler>();
        return services;
    }
}
