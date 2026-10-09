using AgentForge.Llm;
using AgentForge.Llm.Anthropic;
using AgentForge.Llm.Gemini;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Registers the typed <see cref="HttpClient"/> <see cref="LlmProviderHealthCheck"/> probes through.
/// </summary>
public static class LlmProviderHealthCheckRegistration
{
    /// <summary>
    /// Adds the check's typed client behind the auth handler of the configured provider -
    /// <see cref="AnthropicAuthHandler"/> or <see cref="GeminiAuthHandler"/>, the same one every model call goes
    /// through - so the probe proves the key as well as the route. Registered here rather than inline so the wiring
    /// is a testable claim: without the handler the probe goes out keyless and every deployment reads 401
    /// </summary>
    public static IServiceCollection AddLlmProviderHealthCheckClient(this IServiceCollection services)
    {
        services.TryAddTransient<AnthropicAuthHandler>();
        services.TryAddTransient<GeminiAuthHandler>();
        services.AddHttpClient<LlmProviderHealthCheck>().AddHttpMessageHandler(sp =>
            sp.GetRequiredService<IOptions<LlmProviderOptions>>().Value.Provider == LlmProviderKind.Gemini
                ? sp.GetRequiredService<GeminiAuthHandler>()
                : sp.GetRequiredService<AnthropicAuthHandler>());
        return services;
    }
}
