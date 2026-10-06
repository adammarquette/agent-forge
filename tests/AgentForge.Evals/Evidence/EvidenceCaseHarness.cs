using AgentForge.Agents;
using AgentForge.Evals.Answer;
using AgentForge.Retrieval;
using AgentForge.Verification;

namespace AgentForge.Evals.Evidence;

/// <summary>
/// Runs one Week 2 evidence-retrieval case through the shipped composition path: the real
/// <c>HybridEvidenceRetriever</c> (RRF fusion, rerank, deterministic degradation) behind the
/// <c>EvidenceAgentSupervisor</c>, whose critic is the Week 1 <c>ClinicalResponseVerifier</c> over the real
/// <c>SourceAttributionEngine</c> and <c>CardiologyConstraintEngine</c>. Only the two retrieval halves, the
/// reranker and the model's turn are fixtures — nothing about fusion, ordering, citation resolution or
/// degradation is re-implemented here.
/// reference: ARCHITECTURE-DOCUMENTS.md §5 (hybrid RAG), §8 (the gate), §10 (degradation); a separate change
/// </summary>
internal static class EvidenceCaseHarness
{
    public static async Task<CaseOutcome> RunAsync(GoldenCase testCase, CancellationToken cancellationToken)
    {
        var scenario = testCase.Evidence
            ?? throw new InvalidOperationException($"Evidence case '{testCase.Id}' has no 'evidence' block.");

        var logs = new List<CapturedLog>();
        var metrics = new RecordingMetrics();
        var corpus = ScriptedCorpus.Index(scenario, testCase.Id);

        var retriever = new HybridEvidenceRetriever(
            new ScriptedSparseRetriever(scenario, corpus, testCase.Id),
            new ScriptedDenseRetriever(scenario, corpus, testCase.Id),
            new ScriptedReranker(scenario, testCase.Id),
            metrics,
            new CapturingLogger<HybridEvidenceRetriever>(logs));

        var verifier = new ClinicalResponseVerifier(
            new SourceAttributionEngine(),
            new CardiologyConstraintEngine(
                CardiologyConstraintRules.Default, new CapturingLogger<CardiologyConstraintEngine>(logs)),
            new CapturingLogger<ClinicalResponseVerifier>(logs));

        var supervisor = new EvidenceAgentSupervisor(
            new UnusedDocumentExtractor(),
            retriever,
            new EmptyDerivedFactStore(),
            new ScriptedLlmProvider(scenario.ModelScript, testCase.Id),
            verifier,
            metrics,
            new CapturingLogger<EvidenceAgentSupervisor>(logs));

        var result = await supervisor.RunAsync(
            new EvidenceAgentRequest { PatientId = scenario.PatientId, Question = scenario.Question },
            cancellationToken);

        // Succeeded means "guideline evidence reached the composer", which is what
        // RecordEvidenceRetrieval's own `hit` flag means. The out-of-corpus control is the false side, and
        // the population needs both to be distinguishable.
        return new CaseOutcome(
            Succeeded: result.Evidence.Count > 0,
            ResultJson: result.Answer,
            RejectionReason: result.Evidence.Count == 0
                ? "no guideline evidence was retrieved for this query (ARCHITECTURE-DOCUMENTS.md §5)"
                : null,
            Logs: logs,
            SuppressedLines: [.. result.SuppressedClaims.Select(claim => claim.Line)],
            ConstraintRuleIds: [.. result.SafetyFlags.Select(flag => flag.RuleId)],
            RetrievedChunkIds: [.. result.Evidence.Select(snippet => snippet.ChunkId)],
            DegradedStages: metrics.DegradedStages);
    }
}
