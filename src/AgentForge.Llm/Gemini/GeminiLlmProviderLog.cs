using Microsoft.Extensions.Logging;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// Source-generated log messages for <see cref="GeminiLlmProvider"/> (CA1848), the same one-line-per-call shape as
/// <c>AnthropicLlmProviderLog</c> so one query reads both, with <c>provider=gemini</c> appended (FR-OBS-1).
/// <para>
/// Counts, identifiers and outcomes only - no prompt, no completion, no key (CONVENTIONS.md §7,
/// NFR-SEC-1).
/// </para>
/// </summary>
internal static partial class GeminiLlmProviderLog
{
    [LoggerMessage(Level = LogLevel.Information, EventId = 9103, Message =
        "llm.call model={Model} in_tokens={InputTokens} out_tokens={OutputTokens} cost_usd={CostUsd} " +
        "stop_reason={StopReason} latency_ms={LatencyMs} outcome=ok provider=gemini")]
    public static partial void CallCompleted(
        ILogger logger, string model, int inputTokens, int outputTokens, decimal costUsd,
        LlmStopReason stopReason, long latencyMs);

    // Every way a call can fail, as for Anthropic. {ErrorStatus} is google.rpc.Code's fixed name, allow-listed
    // (GeminiErrorReason), and "none" when the failure had no response.
    [LoggerMessage(Level = LogLevel.Warning, EventId = 9104, Message =
        "llm.call model={Model} status={StatusCode} reason={Reason} error_status={ErrorStatus} " +
        "latency_ms={LatencyMs} outcome=error provider=gemini")]
    public static partial void CallFailed(
        ILogger logger, string model, int? statusCode, string reason, string errorStatus, long latencyMs);
}
