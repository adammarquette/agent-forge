namespace AgentForge.Observability;

/// <summary>
/// Application-level metrics feeding the observability dashboard (ARCHITECTURE.md §11,
/// FR-OBS-2/3): agent-turn outcomes/latency, tool-call outcomes/latency, verification pass/fail
/// rate, authorization permit/refuse decisions, out-of-scope tool calls, expired-session refusals,
/// and LLM token/cost
/// accounting. Wrapped behind an
/// interface - rather than call sites using <see cref="System.Diagnostics.Metrics.Meter"/>
/// directly - so unit tests can fake it like every other external dependency
/// instead of needing a live metrics listener.
/// </summary>
public interface IAgentForgeMetrics
{
    /// <summary>
    /// Records one completed agent turn. <paramref name="turnType"/> becomes the exported
    /// <c>turn_type</c> label on the duration histogram — the dimension
    /// <c>AgentForgeHighTurnLatencyP95</c> filters on so it evaluates <c>NFR-PERF-1</c>'s budgeted
    /// <c>RequestBrief</c> population instead of every orchestrator turn.
    /// <b>Only the histogram carries it</b>: the sibling count feeds
    /// <c>AgentForgeHighTurnErrorRate</c>, which sums across all turns, so a dimension there would
    /// buy nothing and was left for whoever needs a per-type error rate.
    /// </summary>
    void RecordAgentTurn(AgentTurnType turnType, bool succeeded, TimeSpan duration);

    /// <summary>Records one MCP tool call dispatched to the model.</summary>
    void RecordToolCall(string toolName, bool succeeded, TimeSpan duration);

    /// <summary>Records one verification-gate outcome (FR-VERIF-0).</summary>
    void RecordVerificationResult(bool passed);

    /// <summary>
    /// Records one FR-AUTH-2 / UC-4 patient-access decision — the permit-or-refuse the dispatcher
    /// makes below the model — in a series of its own. <b>Deliberately not the tool-call series:</b>
    /// a refusal is the system working, and counting it as a tool failure would page an operator
    /// for correct behaviour (FR-OBS-4). <paramref name="reason"/> becomes an exported label, so it
    /// must come from a bounded, enum-like set (<c>AuthorizationDecisionReason</c>) and never carry
    /// a patient, a requester, a resource id or free text.
    /// </summary>
    void RecordAuthorizationDecision(bool permitted, string reason);

    /// <summary>
    /// Records one tool call the dispatcher refused because the catalog does not offer that tool —
    /// the run-time signal behind <c>REQUIREMENTS.md</c> §12.4's NG1 scope guardrail.
    /// <b>Deliberately not the tool-call series</b>, for the same reason as
    /// <see cref="RecordAuthorizationDecision"/>: the refusal is correct behaviour and must not move
    /// the rate <c>AgentForgeHighToolFailureRate</c> pages on. <b>And deliberately unlabelled</b> —
    /// the only distinguishing value available is the attempted tool name, which the model supplies
    /// and can be any string it emits, so putting it on an exported label would be unbounded
    /// cardinality sourced from model output. The name goes to the log line instead.
    /// </summary>
    void RecordOutOfScopeToolCall();

    /// <summary>
    /// Records one refusal caused by the requester's SMART session having aged out — the operator's
    /// only rate for the one-hour token wall. <b>Deliberately neither
    /// <see cref="RecordAuthorizationDecision"/> nor the turn series:</b> an expired token answers
    /// no entitlement question, and describing it as a refusal is the clinical falsehood a separate change
    /// removed — it told a clinician there was no care relationship to their own patient. Turn
    /// failures do move on it, but they cannot say <i>expiry</i>.
    /// <paramref name="surface"/> becomes an exported label, so it comes from the closed set
    /// <c>ExpiredSessionSurface.All</c> and never from a patient, a session, a correlation id or the
    /// token. There is nothing else to tag it with: the expiry instant and the token are the two
    /// values that would make the record of a dead session worth replaying.
    /// </summary>
    void RecordExpiredSessionRefusal(string surface);

    /// <summary>Records token/cost accounting for one LLM call.</summary>
    void RecordLlmUsage(int inputTokens, int outputTokens, decimal estimatedCostUsd);

    // Week 2 (Multimodal Evidence Agent, ARCHITECTURE-DOCUMENTS.md §10 / FR-OBS-W2-1): the supervisor graph and the
    // ingestion path were previously invisible to telemetry. These make document ingestion, per-worker
    // latency, routing, and evidence-retrieval observable without any PHI in the emitted dimensions.

    /// <summary>
    /// Records one document-ingestion attempt (pre-visit path), by outcome: <c>ingested</c>,
    /// <c>already_ingested</c>, <c>extraction_rejected</c>, or <b><c>error</c></b> — the extractor or the
    /// store threw rather than returning. Before <c>error</c> existed, a throw recorded
    /// nothing at all, so a total extractor outage moved neither this histogram nor
    /// <c>AgentForgeHighExtractionFailureRate</c>'s ratio — only the ingestion rate, to zero, which reads
    /// exactly like nobody uploading. <c>error</c> joins both the counter and the duration histogram on the
    /// same call as every other outcome: the elapsed time before a throw is real latency, and
    /// <c>{outcome="ingested"}</c> — what <c>NFR-SLO-W2-1</c>'s ingestion SLO filters on — excludes it either
    /// way. Cancellation is not <c>error</c>: the caller giving up is not the extractor failing.
    /// </summary>
    void RecordDocumentIngestion(string outcome, TimeSpan duration);

    /// <summary>Records the wall-clock latency of one Week 2 graph worker (e.g. intake-extractor, evidence-retriever, answer-composer, critic).</summary>
    void RecordWorkerLatency(string worker, TimeSpan duration);

    /// <summary>Records one supervisor routing decision (a logged handoff), by originating and destination node.</summary>
    void RecordRoutingDecision(string fromNode, string toNode);

    /// <summary>
    /// Records one evidence-retrieval call: whether it hit (returned any snippets), how many, how long it took,
    /// and which call site ran it - every site that calls the retriever records here, so the duration series
    /// covers the retrieval stage rather than one entry point.
    /// </summary>
    void RecordEvidenceRetrieval(bool hit, int resultCount, TimeSpan duration, EvidenceRetrievalEntryPoint entryPoint);

    /// <summary>Records the wall-clock latency of one rerank call that returned (reranked or unranked); a degraded call is recorded by <see cref="RecordRetrievalDegradation"/> instead.</summary>
    void RecordRerankLatency(TimeSpan duration);

    /// <summary>Records one degraded retrieval stage — a half or the reranker failed and the pipeline continued without it — by stage (sparse/dense/rerank).</summary>
    void RecordRetrievalDegradation(string stage);

    /// <summary>
    /// Records one extraction citation's quote-location outcome — the only visible signal behind the
    /// extraction prompt's VERBATIM rule. The ratio of <c>unlocatable</c> to
    /// <c>exact</c> is what turns a fabricating model, or a matcher that regressed, into a trend somebody
    /// can see rather than one clinician noticing a highlight in the wrong place.
    /// <paramref name="outcome"/> becomes an exported label, so it comes from
    /// <c>CitationQuoteMatchExtensions.ToWireName</c> and never from the quote, the document or a patient.
    /// </summary>
    void RecordCitationQuoteMatch(string outcome);

    /// <summary>
    /// Records the grounding confidence of <b>one ingested document</b> — the share of its <i>checkable</i>
    /// citation quotes that were located verbatim in the document's own text, that is
    /// <c>exact / (exact + unlocatable)</c> over the facts derived from it (FR-OBS-W2-2's "extraction
    /// confidence per document").
    /// <b>It is a locatability score, not a model-reported one</b>: <c>1.0</c> means every quote the
    /// extractor could check was found in the document's own text, <c>0.0</c> that none of them was — the
    /// fabricated-fact signal. A panel that plots it as "confidence" without saying so invites exactly the
    /// wrong read.
    /// <b>A document with nothing checkable is not recorded at all</b> (a scan or an image page: every
    /// citation resolves <c>unchecked</c>). "Nothing could be verified" is a different kind of answer from
    /// "some quotes were absent", and putting it on this axis would sort it against partly-fabricated
    /// documents; it is counted instead on <c>agentforge.extraction_field_outcomes</c>, and the shortfall
    /// against <c>agentforge.document_ingestions</c> is how many such documents arrived. Averaging the raw
    /// per-fact scores would do exactly what this avoids — see
    /// <c>DocumentIngestionService.RecordExtractionGrounding</c>.
    /// A histogram rather than an average, because the population is bimodal — a digital PDF grounds every
    /// fact and a scan grounds none — and their mean describes no document that exists.
    /// <paramref name="documentType"/> becomes an exported label and so comes from
    /// <c>ClinicalDocumentTypeExtensions.ToWireName</c>: a two-member enum, never a document id, a patient or
    /// a field value.
    /// </summary>
    void RecordExtractionConfidence(string documentType, double confidence);

    /// <summary>
    /// Records one derived fact's grounding outcome under the kind of field it is, so a <b>pass rate per
    /// field</b> is derivable — every fact is counted whatever it scored, which is the denominator the rate
    /// needs (FR-OBS-W2-2's "extraction field-level pass rate").
    /// <b>Both arguments become exported labels and both are bounded and PHI-free.</b>
    /// <paramref name="field"/> comes from <c>DerivedFactType.ToFieldLabel</c> — the fact's <i>kind</i>, a
    /// closed set of literals — and deliberately <b>not</b> the extracted field's name, which is model output
    /// and would mint a permanent series per string the model emits as well as putting document text on a
    /// label. <paramref name="outcome"/> comes from <c>CitationQuoteMatchExtensions.ToWireName</c>. Neither
    /// ever carries a field <i>value</i>.
    /// </summary>
    void RecordExtractionFieldOutcome(string field, string outcome);
}
