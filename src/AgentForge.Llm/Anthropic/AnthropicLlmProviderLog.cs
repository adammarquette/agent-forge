using Microsoft.Extensions.Logging;

namespace AgentForge.Llm.Anthropic;

/// <summary>
/// Source-generated log messages for <see cref="AnthropicLlmProvider"/> (CA1848). One line per
/// model call, written inside whatever correlation scope is open, so an LLM interaction is
/// reconstructable from logs alone (FR-OBS-1) rather than only from aggregate metrics.
/// <para>
/// Counts, identifiers and outcomes only. An LLM request carries chart content and its response
/// carries the synthesized narrative, so **no prompt or completion text is ever logged here** -
/// this is the highest-risk line in the codebase for that (CONVENTIONS.md §7, NFR-SEC-1).
/// </para>
/// </summary>
internal static partial class AnthropicLlmProviderLog
{
    [LoggerMessage(Level = LogLevel.Information, EventId = 9101, Message =
        "llm.call model={Model} in_tokens={InputTokens} out_tokens={OutputTokens} cost_usd={CostUsd} " +
        "stop_reason={StopReason} latency_ms={LatencyMs} outcome=ok")]
    public static partial void CallCompleted(
        ILogger logger, string model, int inputTokens, int outputTokens, decimal costUsd,
        LlmStopReason stopReason, long latencyMs);

    // Written for *every* way a call can fail, not just a provider error response: the resilience
    // pipeline surfaces attempt-timeout and circuit-open as its own exception types, and that
    // degrade path is the one most worth reconstructing (REQUIREMENTS.md §13.1). {StatusCode} is
    // null when the call never reached a response; {Reason} is the exception's type name only. The
    // provider's error body can echo request fields, so it reaches no log at all: {ErrorType} is
    // Anthropic's fixed error.type and {RequestId} its opaque request-id header, each allow-listed
    // (AnthropicErrorReason), and "none" when the failure had no response. A separate change
    [LoggerMessage(Level = LogLevel.Warning, EventId = 9102, Message =
        "llm.call model={Model} status={StatusCode} reason={Reason} error_type={ErrorType} " +
        "request_id={RequestId} latency_ms={LatencyMs} outcome=error")]
    public static partial void CallFailed(
        ILogger logger, string model, int? statusCode, string reason, string errorType, string requestId,
        long latencyMs);
}
