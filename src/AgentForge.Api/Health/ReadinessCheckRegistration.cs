using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Api.Health;

/// <summary>
/// Registers the <c>/ready</c> checks (tag <c>ready</c>) and the typed clients they probe through.
/// </summary>
public static class ReadinessCheckRegistration
{
    /// <summary>Tag <c>/ready</c> selects its checks by.</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Adds the five readiness checks. The three that leave the deployment - the model provider, OpenEMR
    /// and Cohere - are wrapped in <see cref="CachedReadinessCheck{TCheck}"/>, because <c>/ready</c> is
    /// public and each call otherwise became one request to each. Prometheus and the
    /// vector index sit on the deployment's own network and cost nothing to ask, so they stay live; the
    /// vector index's answer also has to change the moment this build's migrations complete.
    /// The host registers <c>IVectorIndexProbe</c> itself.
    /// </summary>
    public static IServiceCollection AddReadinessChecks(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(typeof(ReadinessResultCache<>));

        services.AddHttpClient<OpenEmrHealthCheck>();
        services.AddLlmProviderHealthCheckClient();
        services.AddHttpClient<ObservabilityHealthCheck>();
        // Registered even where Week 2 is not wired: the check reports Cohere:ApiKey unconfigured rather
        // than say nothing. A raw GET of /v1/models, never a rerank call. A separate change
        services.AddRerankerHealthCheckClient();

        services.AddHealthChecks()
            .AddCheck<CachedReadinessCheck<OpenEmrHealthCheck>>("openemr", tags: [ReadyTag])
            .AddCheck<CachedReadinessCheck<LlmProviderHealthCheck>>("llm-provider", tags: [ReadyTag])
            .AddCheck<ObservabilityHealthCheck>("observability", tags: [ReadyTag])
            .AddCheck<VectorIndexHealthCheck>("vector-index", tags: [ReadyTag])
            .AddCheck<CachedReadinessCheck<RerankerHealthCheck>>("reranker", tags: [ReadyTag]);
        return services;
    }
}
