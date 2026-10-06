using System.Net.Http;
using AgentForge.Llm;

namespace AgentForge.Evals.Answer;

/// <summary>
/// Replays one answer case's pinned model turns, in order - the answer-path analogue of pinning the tool
/// call an injection would have produced. Nothing about the turn is generated, so the case is reproducible
/// and no live API is needed; what is under test is what the shipped orchestrator and verifier do to a
/// fixed draft.
/// </summary>
/// <remarks>
/// Running out of turns throws rather than returning an empty response: a script one turn short would
/// otherwise degrade to the deterministic fallback and the case would read as a passing M5 result.
/// </remarks>
internal sealed class ScriptedLlmProvider(IReadOnlyList<ModelTurnFixture> script, string caseId) : ILlmProvider
{
    private int _turn;

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        if (_turn >= script.Count)
        {
            throw new InvalidOperationException(
                $"Answer case '{caseId}' ran out of pinned model turns after {script.Count} - the orchestrator " +
                "asked for one more. Add the turn rather than letting the case degrade for the wrong reason.");
        }

        var turn = script[_turn++];

        if (turn.NeverReturns)
        {
            // REQUIREMENTS.md §13.1's "tool slow / hits deadline" row. The case configures a tiny TurnDeadline, so the
            // orchestrator's own linked token cancels this long before the delay elapses - the delay is only
            // here so a misconfigured case fails loudly instead of hanging the gate.
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Answer case '{caseId}' pinned a never-returning model turn, but the turn deadline never fired.");
        }

        if (turn.FailsWith is { } failure)
        {
            throw ProviderFailure(failure, caseId);
        }

        var toolCalls = (turn.ToolCalls ?? [])
            .Select((call, index) => new LlmToolCall($"eval-tool-use-{index}", call.ToolName, call.ArgumentsJson))
            .ToArray();

        var stopReason = toolCalls.Length > 0 ? LlmStopReason.ToolUse : ParseStopReason(turn.StopReason, caseId);
        return new LlmResponse(turn.Text ?? string.Empty, toolCalls, stopReason, new LlmUsage(0, 0, 0m));
    }

    /// <summary>
    /// The exception the provider raises once its own resilience pipeline is exhausted - which is the state
    /// REQUIREMENTS.md §13.1's "LLM timeout / provider error" and "LLM rate-limit / quota" rows describe, not the
    /// first transient failure. The message reaches the degradation log verbatim, so it carries no PHI.
    /// </summary>
    private static Exception ProviderFailure(string failure, string caseId) => failure switch
    {
        "timeout" => new TimeoutException(
            "Synthetic provider timeout: no response before the per-attempt timeout, retries exhausted."),
        "rate_limit" => new HttpRequestException(
            "Synthetic provider rate limit: HTTP 429 Too Many Requests, retries exhausted."),
        "provider_error" => new HttpRequestException(
            "Synthetic provider error: HTTP 503 Service Unavailable, retries exhausted."),
        _ => throw new InvalidOperationException($"Unknown fails_with '{failure}' in answer case '{caseId}'."),
    };

    private static LlmStopReason ParseStopReason(string? stopReason, string caseId) => stopReason switch
    {
        null or "end_turn" => LlmStopReason.EndTurn,
        "max_tokens" => LlmStopReason.MaxTokens,
        "other" => LlmStopReason.Other,
        _ => throw new InvalidOperationException($"Unknown stop_reason '{stopReason}' in answer case '{caseId}'."),
    };
}
