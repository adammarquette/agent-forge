using AgentForge.Agent;
using AgentForge.Verification;
using Microsoft.Extensions.Options;

namespace AgentForge.Evals.Answer;

/// <summary>
/// Runs one Week 1 answer-path case through the shipped composition path: <c>AgentOrchestrator</c> →
/// <c>ClinicalResponseVerifier</c> → the real <c>SourceAttributionEngine</c> and
/// <c>CardiologyConstraintEngine</c> over <c>CardiologyConstraintRules.Default</c>, with citations and
/// constraint input recovered by the real <c>ToolResultJsonScanner</c>. Nothing about verification or
/// degradation is re-implemented here; only the model's turns and the chart are fixtures.
/// reference: REQUIREMENTS.md §7.3 (FR-VERIF-0/1/2), §13.1 (degradation), §14 (M1, M2, M5)
/// </summary>
internal static class AnswerCaseHarness
{
    // Long enough that no case hits it incidentally; the deadline cases override it to a millisecond.
    private static readonly TimeSpan DefaultTurnDeadline = TimeSpan.FromSeconds(60);

    public static async Task<CaseOutcome> RunAsync(GoldenCase testCase, CancellationToken cancellationToken)
    {
        var scenario = testCase.Answer
            ?? throw new InvalidOperationException($"Answer case '{testCase.Id}' has no 'answer' block.");

        var logs = new List<CapturedLog>();
        var verifier = new ClinicalResponseVerifier(
            new SourceAttributionEngine(),
            new CardiologyConstraintEngine(
                CardiologyConstraintRules.Default, new CapturingLogger<CardiologyConstraintEngine>(logs)),
            new CapturingLogger<ClinicalResponseVerifier>(logs));

        var deadline = scenario.TurnDeadlineMs is { } ms ? TimeSpan.FromMilliseconds(ms) : DefaultTurnDeadline;
        var orchestrator = new AgentOrchestrator(
            new ScriptedLlmProvider(scenario.ModelScript, testCase.Id),
            new ScriptedToolDispatcher(scenario, testCase.Id),
            verifier,
            new NoOpMetrics(),
            Options.Create(new AgentOptions { TurnDeadline = deadline }),
            new CapturingLogger<AgentOrchestrator>(logs));

        var result = await orchestrator.StartBriefAsync(scenario.Site, scenario.PatientId, cancellationToken);

        // Succeeded means "the turn shipped a model-synthesized answer": a deterministic fallback is the
        // honest degradation, not a success, and M5's population needs both sides to be distinguishable.
        return new CaseOutcome(
            Succeeded: !result.IsDeterministicFallback,
            ResultJson: result.Answer,
            RejectionReason: result.IsDeterministicFallback
                ? "degraded to the deterministic, non-LLM fallback (REQUIREMENTS.md §13.1)"
                : null,
            Logs: logs,
            SuppressedLines: [.. result.SuppressedClaims.Select(claim => claim.Line)],
            ConstraintRuleIds: [.. result.SafetyFlags.Select(flag => flag.RuleId)]);
    }
}
