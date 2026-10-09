using System.Net;
using AgentForge.Llm;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Readiness check for the LLM provider dependency (NFR-HEALTH-1, NFR-REL-2): an authenticated GET of
/// the configured model on the configured provider - Anthropic's <c>/v1/models/{model}</c> or Gemini's
/// <c>/v1beta/models/{model}</c> - which spends no tokens and answers 200 only when the
/// base URL, the API key and the model all resolve - the three things every clinical turn needs.
/// Bounded by <see cref="ReadinessOptions.ProbeTimeout"/>.
/// </summary>
/// <remarks>
/// The status mapping is decided, not defaulted (<c>ARCHITECTURE.md</c> D17):
/// 2xx is <see cref="HealthStatus.Healthy"/>; 429 is <see cref="HealthStatus.Degraded"/>, because a
/// throttle is on the account every instance shares, so shedding this one relieves nothing; every other
/// status - a rejected key, an unknown route or model, a provider outage - and no answer at all are
/// <see cref="HealthStatus.Unhealthy"/>. The key is attached by the same auth handler every model call to
/// that provider goes through (<c>AnthropicAuthHandler</c> or <c>GeminiAuthHandler</c>), so a probe that
/// lost it reads 401 or 403 and fails closed.
/// <para>
/// <b>Cost:</b> each probe is one authenticated provider request on the configured key. <c>/ready</c> is
/// public, so the host serves this check through <see cref="CachedReadinessCheck{TCheck}"/>: at most one
/// probe per <see cref="ReadinessOptions.ResultCacheTtl"/> per process, however often <c>/ready</c> is called,
/// since the probe runs detached from callers that abort. Whether the provider counts
/// it against the Messages API limit is unverified; <c>ARCHITECTURE.md</c> section 11 records the open question.
/// </para>
/// </remarks>
public sealed class LlmProviderHealthCheck(
    HttpClient httpClient,
    IOptions<LlmProviderOptions> options,
    IOptions<ReadinessOptions> readinessOptions) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var llm = options.Value;
        var uri = $"{llm.BaseUrl.TrimEnd('/')}/{ModelsPathFor(llm.Provider)}/{Uri.EscapeDataString(llm.Model)}";
        var probeTimeout = readinessOptions.Value.ProbeTimeout;

        var outcome = await ReadinessProbe.GetAsync(httpClient, uri, probeTimeout, cancellationToken)
            .ConfigureAwait(false);
        using var response = outcome.Response;

        if (response is null)
        {
            return HealthCheckResult.Unhealthy(
                $"LLM provider {outcome.DescribeFailure(probeTimeout)}.", outcome.Failure);
        }

        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return HealthCheckResult.Healthy($"LLM provider accepted the key and knows the configured model ({status}).");
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests
            ? HealthCheckResult.Degraded($"LLM provider rate-limited the readiness probe ({status}) - the key was accepted; whether model calls are throttled too is not established.")
            : HealthCheckResult.Unhealthy($"LLM provider responded {status} to the configured model lookup - model calls will fail.");
    }

    private static string ModelsPathFor(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.Anthropic => "v1/models",
        LlmProviderKind.Gemini => "v1beta/models",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown LlmProviderKind."),
    };
}
