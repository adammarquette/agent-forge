namespace AgentForge.Evals;

/// <summary>
/// What one golden case produced, in the shape the rubrics score — so an extraction case, an
/// authorization case and an answer-path case are graded by the same evaluator rather than three that can
/// drift apart.
/// </summary>
/// <param name="Succeeded">Data came back: the schema gate passed, the requester was entitled and the tool
/// ran, or the turn shipped a model-synthesized answer rather than degrading to the deterministic fallback.</param>
/// <param name="ResultJson">The canonical extraction, the tool result the model would have seen, or the
/// verified answer that actually reached the clinician.</param>
/// <param name="RejectionReason">Why it was refused, when it was.</param>
/// <param name="Logs">Every log line the run emitted, across all participating loggers.</param>
/// <param name="SuppressedLines">Lines FR-VERIF-1 removed from the draft before it shipped. Answer cases only.</param>
/// <param name="ConstraintRuleIds">Rule ids FR-VERIF-2 raised this turn - M2's numerator. Answer cases only.</param>
/// <param name="RetrievedChunkIds">Guideline chunk ids the hybrid retriever returned this turn, in the order
/// it returned them - so retrieval_hit can score membership and ordering from the same field. Evidence cases
/// only; null elsewhere, which is distinct from an empty list (the out-of-corpus result).</param>
/// <param name="DegradedStages">Stages the retriever recorded a degradation for ("sparse", "dense",
/// "rerank"). Evidence cases only. Empty means the pipeline ran whole, which is an assertion in its own
/// right: a stage that fails silently is the failure mode RecordRetrievalDegradation exists to surface.</param>
/// <param name="Fault">The exception the pipeline under test threw instead of returning, as
/// "<c>Type: message</c>". Non-null means nothing was checked, so every rubric the case declares fails.
/// a separate change</param>
internal sealed record CaseOutcome(
    bool Succeeded,
    string? ResultJson,
    string? RejectionReason,
    IReadOnlyList<CapturedLog> Logs,
    IReadOnlyList<string>? SuppressedLines = null,
    IReadOnlyList<string>? ConstraintRuleIds = null,
    IReadOnlyList<string>? RetrievedChunkIds = null,
    IReadOnlyList<string>? DegradedStages = null,
    string? Fault = null);
