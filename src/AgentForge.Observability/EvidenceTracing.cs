using System.Diagnostics;

namespace AgentForge.Observability;

/// <summary>
/// Span names and attribute keys for the Week 2 evidence flow (NFR-TRACE-W2, ARCHITECTURE-DOCUMENTS.md §6 and §10):
/// the ask root, the supervisor and its workers, and the extraction and retrieval sub-calls inside them.
/// Every value written under these keys is a node name, a fixed routing reason, an outcome literal or a count:
/// the no-PHI rule for traces (ARCHITECTURE-DOCUMENTS.md §12) binds a span exactly as it binds a metric label.
/// </summary>
public static class EvidenceTracing
{
    /// <summary>The application root span of one <c>POST /evidence/ask</c> request.</summary>
    public const string AskSpan = "evidence.ask";

    /// <summary>The supervisor's span; every worker span is its child.</summary>
    public const string SupervisorSpan = "evidence.supervisor";

    /// <summary>Prefix of a worker span's name; the worker's node name follows it.</summary>
    public const string WorkerSpanPrefix = "worker.";

    /// <summary>Span event recorded on the supervisor span for every handoff.</summary>
    public const string HandoffEvent = "handoff";

    /// <summary>The request's correlation id - the key that joins a trace to its log lines.</summary>
    public const string CorrelationId = "agentforge.correlation_id";

    /// <summary>What a span's step came to, from a closed set of literals.</summary>
    public const string Outcome = "agentforge.outcome";

    /// <summary>The worker a span belongs to.</summary>
    public const string Worker = "agentforge.worker";

    /// <summary>The node that routed to this step.</summary>
    public const string RouteFrom = "agentforge.route.from";

    /// <summary>The node a handoff routed to.</summary>
    public const string RouteTo = "agentforge.route.to";

    /// <summary>Why the supervisor routed this way - the reason the handoff log line carries.</summary>
    public const string RouteReason = "agentforge.route.reason";

    /// <summary>How many handoffs the run made.</summary>
    public const string HandoffCount = "agentforge.handoff_count";

    /// <summary>How many guideline snippets the retriever returned.</summary>
    public const string SnippetCount = "agentforge.evidence.snippet_count";

    /// <summary>How many claims the critic suppressed.</summary>
    public const string SuppressedClaims = "agentforge.critic.suppressed_claims";

    /// <summary>How many domain-constraint flags the critic raised.</summary>
    public const string ConstraintFlags = "agentforge.critic.constraint_flags";

    /// <summary>Prefix of a retrieval stage span's name; the stage (sparse, dense, fusion, rerank) follows it.</summary>
    public const string RetrievalStageSpanPrefix = "retrieval.";

    /// <summary>The span around the extractor's vision-model call, a child of the intake-extractor worker span.</summary>
    public const string ExtractionVlmSpan = "extraction.vlm";

    /// <summary>The retrieval stage a span times - the same literal <c>RecordRetrievalDegradation</c> labels its counter with.</summary>
    public const string RetrievalStage = "agentforge.retrieval.stage";

    /// <summary>How many candidates a retrieval stage produced (a half, fusion) or was handed (rerank).</summary>
    public const string CandidateCount = "agentforge.retrieval.candidate_count";

    /// <summary>Why a retrieval stage did not run, so a skipped stage cannot be read as a fast one.</summary>
    public const string SkipReason = "agentforge.retrieval.skip_reason";

    /// <summary>The document type extracted, as its wire name.</summary>
    public const string DocumentType = "agentforge.document.type";

    /// <summary>The OTel GenAI semantic-convention key for a model call's input tokens.</summary>
    public const string InputTokens = "gen_ai.usage.input_tokens";

    /// <summary>The OTel GenAI semantic-convention key for a model call's output tokens.</summary>
    public const string OutputTokens = "gen_ai.usage.output_tokens";

    /// <summary>The OTel semantic-convention key for a failure's exception type.</summary>
    public const string ErrorType = "error.type";

    /// <summary>
    /// Marks <paramref name="span"/> failed by the exception's <em>type</em>. Never its message: that is free
    /// text, and a message built from a request can carry PHI.
    /// </summary>
    public static void RecordFailure(Activity? span, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (span is null)
        {
            return;
        }

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        span.SetStatus(ActivityStatusCode.Error, type);
        span.SetTag(ErrorType, type);
    }
}
