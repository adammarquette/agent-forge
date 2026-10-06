using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace AgentForge.Llm.Anthropic;

/// <summary>
/// The v1 <see cref="ILlmProvider"/> implementation (ARCHITECTURE.md D12) - Sonnet-class grounded
/// summarization via the Anthropic Messages API.
/// </summary>
public sealed class AnthropicLlmProvider(
    IAnthropicMessagesApi api, IOptions<LlmProviderOptions> options, ILogger<AnthropicLlmProvider> logger)
    : ILlmProvider
{
    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var wireRequest = AnthropicRequestMapper.Map(request, options.Value.Model);
        var startedAt = Stopwatch.GetTimestamp();
        AnthropicMessageResponse wireResponse;
        try
        {
            wireResponse = await api.CreateMessageAsync(wireRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (!string.IsNullOrEmpty(ex.Content))
        {
            var reason = AnthropicErrorReason.From(ex);
            LogFailure(ex, startedAt, reason);

            // AgentOrchestrator logs this Message verbatim on fallback (REQUIREMENTS.md §13.1), so it carries the status,
            // the fixed error type and the request id - never the body, whose "message" can quote the request
            // back. A plain HttpRequestException keeps ILlmProvider transport-agnostic.
            throw new HttpRequestException(
                $"Anthropic Messages API call failed: status {(int)ex.StatusCode}, error type {reason.ErrorType}, " +
                $"request id {reason.RequestId}.",
                ex,
                ex.StatusCode);
        }
        catch (Exception ex)
        {
            // Everything else a call can fail as - a bodyless provider error, and the whole
            // resilience-exhaustion degrade path, which arrives as the pipeline's own timeout and
            // circuit-breaker types rather than as an ApiException. Logged, then rethrown unchanged.
            LogFailure(
                ex, startedAt, ex is ApiException apiException ? AnthropicErrorReason.From(apiException) : AnthropicErrorReason.NoResponse);
            throw;
        }

        var response = AnthropicResponseMapper.Map(
            wireResponse,
            options.Value.InputPricePerMillionTokensUsd,
            options.Value.OutputPricePerMillionTokensUsd);

        var latencyMs = ElapsedMs(startedAt);
        var usage = response.Usage;
        AnthropicLlmProviderLog.CallCompleted(
            logger,
            options.Value.Model,
            usage.InputTokens,
            usage.OutputTokens,
            usage.EstimatedCostUsd,
            response.StopReason,
            latencyMs);

        return response;
    }

    private void LogFailure(Exception exception, long startedAt, AnthropicErrorReason reason)
    {
        var failedAfterMs = ElapsedMs(startedAt);
        var statusCode = exception is ApiException apiException ? (int)apiException.StatusCode : (int?)null;
        AnthropicLlmProviderLog.CallFailed(
            logger, options.Value.Model, statusCode, exception.GetType().Name, reason.ErrorType, reason.RequestId,
            failedAfterMs);
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
