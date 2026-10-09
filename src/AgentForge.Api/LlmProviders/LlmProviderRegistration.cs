using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Llm.Anthropic;
using AgentForge.Llm.Gemini;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.Api.LlmProviders;

/// <summary>
/// Registers the model tier: the validated <see cref="LlmProviderOptions"/>, one Refit client per provider and the
/// <see cref="ILlmProvider"/> that <c>Llm__Provider</c> selects (ARCHITECTURE.md section 12).
/// </summary>
public static class LlmProviderRegistration
{
    /// <summary>
    /// Binds and validates <see cref="LlmProviderOptions"/> on start, so an unknown <c>Llm__Provider</c> stops the
    /// host booting, and resolves <see cref="ILlmProvider"/> to the configured provider. Both clients are wired the
    /// same way - auth handler, correlation id, the LLM resilience budget - and only the selected one is ever
    /// called. Requires an <see cref="ICorrelationIdAccessor"/>.
    /// </summary>
    public static IServiceCollection AddLlmProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(LlmProviderOptions.SectionName);
        services.AddOptions<LlmProviderOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // LLM synthesis routinely runs longer than the framework's 10s/30s HTTP defaults, so the heaviest
        // agenda-summary prompt was tripping the attempt timeout and degrading to the deterministic fallback
        // . Read from the same config the validated options bind to, which still fail fast.
        var attemptTimeout = TimeSpan.FromSeconds(
            section.GetValue<int?>(nameof(LlmProviderOptions.AttemptTimeoutSeconds))
                ?? LlmProviderOptions.DefaultAttemptTimeoutSeconds);
        var totalTimeout = TimeSpan.FromSeconds(
            section.GetValue<int?>(nameof(LlmProviderOptions.TotalRequestTimeoutSeconds))
                ?? LlmProviderOptions.DefaultTotalRequestTimeoutSeconds);

        services.TryAddTransient<AnthropicAuthHandler>();
        services.TryAddTransient<GeminiAuthHandler>();
        services.TryAddTransient<CorrelationIdHandler>();

        AddModelClient<IAnthropicMessagesApi, AnthropicAuthHandler>(services, attemptTimeout, totalTimeout);
        AddModelClient<IGeminiGenerativeLanguageApi, GeminiAuthHandler>(services, attemptTimeout, totalTimeout);

        services.AddScoped<AnthropicLlmProvider>();
        services.AddScoped<GeminiLlmProvider>();
        services.AddScoped<ILlmProvider>(sp =>
            sp.GetRequiredService<IOptions<LlmProviderOptions>>().Value.Provider switch
            {
                LlmProviderKind.Anthropic => sp.GetRequiredService<AnthropicLlmProvider>(),
                LlmProviderKind.Gemini => sp.GetRequiredService<GeminiLlmProvider>(),
                var unknown => throw new InvalidOperationException(
                    $"{LlmProviderOptions.SectionName}:{nameof(LlmProviderOptions.Provider)} '{unknown}' has no ILlmProvider."),
            });

        return services;
    }

    private static void AddModelClient<TApi, TAuthHandler>(
        IServiceCollection services, TimeSpan attemptTimeout, TimeSpan totalTimeout)
        where TApi : class
        where TAuthHandler : DelegatingHandler
    {
        services.AddRefitClient<TApi>()
            .ConfigureHttpClient((sp, client) =>
            {
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<LlmProviderOptions>>().Value.BaseUrl);
                // HttpClient's outer timeout must exceed the pipeline's total, or it cancels first.
                client.Timeout = totalTimeout + TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<TAuthHandler>()
            // FR-OBS-1 covers the LLM hop too; without it a model call is the one boundary the trace cannot cross.
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = attemptTimeout;
                options.TotalRequestTimeout.Timeout = totalTimeout;
                // Handler invariant: SamplingDuration must be >= 2x AttemptTimeout.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(attemptTimeout.TotalSeconds * 2);
                options.Retry.MaxRetryAttempts = 2;
            });
    }
}
