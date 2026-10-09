using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// The Gemini <see cref="ILlmProvider"/> (ARCHITECTURE.md section 12), on <c>generateContent</c>. Selected by
/// <c>Llm__Provider=Gemini</c>; behind the same resilience handler, and the same never-log-the-body rule, as
/// <c>AnthropicLlmProvider</c>.
/// </summary>
public sealed class GeminiLlmProvider(
    IGeminiGenerativeLanguageApi api, IOptions<LlmProviderOptions> options, ILogger<GeminiLlmProvider> logger)
    : ILlmProvider
{
    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var wireRequest = GeminiRequestMapper.Map(request);
        var startedAt = Stopwatch.GetTimestamp();
        GeminiGenerateContentResponse wireResponse;
        try
        {
            wireResponse = await api.GenerateContentAsync(options.Value.Model, wireRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex) when (!string.IsNullOrEmpty(ex.Content))
        {
            var reason = GeminiErrorReason.From(ex);
            LogFailure(ex, startedAt, reason);

            // Status and the fixed error status only - the body's message can quote the request back.
            throw new HttpRequestException(
                $"Gemini generateContent call failed: status {(int)ex.StatusCode}, error status {reason.ErrorStatus}.",
                ex,
                ex.StatusCode);
        }
        catch (Exception ex)
        {
            // A bodyless provider error, or the resilience pipeline's own timeout and circuit-breaker types.
            LogFailure(
                ex, startedAt, ex is ApiException apiException ? GeminiErrorReason.From(apiException) : GeminiErrorReason.NoResponse);
            throw;
        }

        var response = GeminiResponseMapper.Map(
            wireResponse,
            options.Value.InputPricePerMillionTokensUsd,
            options.Value.OutputPricePerMillionTokensUsd);

        var latencyMs = ElapsedMs(startedAt);
        var usage = response.Usage;
        GeminiLlmProviderLog.CallCompleted(
            logger,
            options.Value.Model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.EstimatedCostUsd,
            response.StopReason,
            latencyMs);

        return response;
    }

    private void LogFailure(Exception exception, long startedAt, GeminiErrorReason reason)
    {
        var failedAfterMs = ElapsedMs(startedAt);
        var statusCode = exception is ApiException apiException ? (int)apiException.StatusCode : (int?)null;
        GeminiLlmProviderLog.CallFailed(
            logger, options.Value.Model, statusCode, exception.GetType().Name, reason.ErrorStatus, failedAfterMs);
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
