using System.Diagnostics.Metrics;

namespace AgentForge.Observability;

/// <inheritdoc cref="IAgentForgeMetrics" />
public sealed class AgentForgeMetrics : IAgentForgeMetrics, IDisposable
{
    /// <summary>Meter name every OTel <c>MeterProvider</c> registration must include (Program.cs).</summary>
    public const string MeterName = "AgentForge";

    /// <summary>
    /// Explicit bucket boundaries for <c>agentforge.extraction_confidence</c>, which records a fraction in
    /// <c>[0,1]</c>. The default OpenTelemetry boundaries start at <c>0</c> and jump to <c>5</c>, so every
    /// value the instrument can carry would land in one bucket and the histogram would degenerate into the
    /// average it exists to avoid. These span exactly the range, leave the fabrication floor alone in the
    /// first bucket (<c>0</c> — not one checkable quote was located), and keep enough resolution across the
    /// middle to tell a partly-grounded document from a well-grounded one. A fully grounded document does
    /// share the top bucket with a nearly-grounded one; <c>agentforge.extraction_field_outcomes</c> is where
    /// "was any quote absent at all" is answered exactly.
    /// </summary>
    public static readonly IReadOnlyList<double> ExtractionConfidenceBuckets = [0, 0.25, 0.5, 0.75, 1];

    /// <summary>
    /// Explicit bucket boundaries (seconds) for <c>agentforge.agent_turn.duration</c>, applied by the
    /// <c>AddView</c> in <c>Program.cs</c>. OpenTelemetry .NET's defaults put <b>no boundary between 10
    /// and 50 except 25</b>, so <c>histogram_quantile</c> interpolates across a 25-second-wide band
    /// exactly where <c>NFR-PERF-1</c>'s 26s budget and the 25.8s baseline both sit — a quantile from
    /// that bucket cannot tell 25.8 from 26.0. These put <b>26 on a boundary</b> and step every 2s
    /// through 20–30s so the budgeted range resolves. Kept here, beside the instrument, rather than
    /// inline in the host, so a test can reach both sides of the pin: <c>AlertRuleThresholdTests</c>
    /// parses <c>AgentForgeHighTurnLatencyP95</c>'s threshold out of the shipped rule file and asserts
    /// it is one of these boundaries, so moving either side alone reddens the suite.
    /// </summary>
    public static readonly IReadOnlyList<double> AgentTurnDurationBucketBoundariesSeconds =
    [
        0.5, 1, 2, 5, 10, 15, 20, 22, 24, 26, 28, 30, 35, 40, 50, 75, 100,
    ];

    /// <summary>
    /// Explicit bucket boundaries (seconds) for <c>agentforge.evidence_retrieval.duration</c>, applied by an
    /// <c>AddView</c> in <c>Program.cs</c>. The defaults leave <c>NFR-SLO-W2-1</c>'s 6s target inside a 5-10s
    /// bucket; these put <b>6 on a boundary</b> and step every second through 5-15s, below which the 3.16s
    /// measured mean sits. <c>AlertRuleThresholdTests</c> pins
    /// <c>AgentForgeHighEvidenceRetrievalLatencyP95</c>'s threshold against this list, and
    /// <c>Week2HistogramExportTests</c> that the exported series carries exactly it.
    /// </summary>
    public static readonly IReadOnlyList<double> EvidenceRetrievalDurationBucketBoundariesSeconds =
    [
        0.25, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 20, 30, 60,
    ];

    /// <summary>
    /// Explicit bucket boundaries (seconds) for <c>agentforge.document_ingestion.duration</c>, applied by an
    /// <c>AddView</c> in <c>Program.cs</c>. The defaults leave the 11s ingestion target inside a 10-25s bucket;
    /// these put <b>11 on a boundary</b>, step every second through 5-15s around it, and reach 120s so a slow
    /// outlier lands in a finite bucket rather than <c>+Inf</c>. <c>Week2HistogramExportTests</c> pins that the
    /// exported series carries exactly this list.
    /// </summary>
    public static readonly IReadOnlyList<double> DocumentIngestionDurationBucketBoundariesSeconds =
    [
        0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 20, 30, 45, 60, 120,
    ];

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _agentTurnsTotal;
    private readonly Histogram<double> _agentTurnDurationSeconds;
    private readonly Counter<long> _toolCallsTotal;
    private readonly Histogram<double> _toolCallDurationSeconds;
    private readonly Counter<long> _verificationResultsTotal;
    private readonly Counter<long> _authorizationDecisionsTotal;
    private readonly Counter<long> _outOfScopeToolCallsTotal;
    private readonly Counter<long> _expiredSessionRefusalsTotal;
    private readonly Counter<long> _llmTokensTotal;
    private readonly Counter<double> _llmCostUsdTotal;
    private readonly Counter<long> _documentIngestionsTotal;
    private readonly Histogram<double> _documentIngestionDurationSeconds;
    private readonly Histogram<double> _workerDurationSeconds;
    private readonly Counter<long> _routingDecisionsTotal;
    private readonly Counter<long> _evidenceRetrievalsTotal;
    private readonly Histogram<double> _evidenceRetrievalDurationSeconds;
    private readonly Histogram<long> _evidenceRetrievalResults;
    private readonly Histogram<double> _rerankDurationSeconds;
    private readonly Counter<long> _retrievalDegradationsTotal;
    private readonly Counter<long> _citationQuoteMatchesTotal;
    private readonly Histogram<double> _extractionConfidence;
    private readonly Counter<long> _extractionFieldOutcomesTotal;

    /// <summary>Creates the meter and every instrument it publishes under <see cref="MeterName"/>.</summary>
    public AgentForgeMetrics()
    {
        _agentTurnsTotal = _meter.CreateCounter<long>(
            "agentforge.agent_turns", unit: "{turn}", description: "Completed agent turns, by outcome.");
        _agentTurnDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.agent_turn.duration", unit: "s", description: "Wall-clock duration of one agent turn.");
        _toolCallsTotal = _meter.CreateCounter<long>(
            "agentforge.tool_calls", unit: "{call}", description: "MCP tool calls dispatched, by tool and outcome.");
        _toolCallDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.tool_call.duration", unit: "s", description: "Wall-clock duration of one MCP tool call.");
        _verificationResultsTotal = _meter.CreateCounter<long>(
            "agentforge.verification_results", unit: "{result}", description: "Verification-gate outcomes, by pass/fail.");
        // The other Week 1 decision outcome (FR-AUTH-2/UC-4). Its own instrument, never a tag on
        // agentforge.tool_calls: a refusal is correct behaviour and must not move the tool-failure
        // rate AgentForgeHighToolFailureRate pages on.
        _authorizationDecisionsTotal = _meter.CreateCounter<long>(
            "agentforge.authorization_decisions", unit: "{decision}",
            description: "Patient-access decisions below the model, by permit/refuse and bounded reason.");
        // The NG1 scope guardrail's only run-time signal, and its own instrument for the same reason
        // as the row above. Untagged on purpose: the attempted tool name is model-supplied, so it
        // belongs in a log line and not on an exported label.
        _outOfScopeToolCallsTotal = _meter.CreateCounter<long>(
            "agentforge.out_of_scope_tool_calls", unit: "{call}",
            description: "Tool calls refused because the catalog does not offer that tool (REQUIREMENTS.md 12.4 NG1).");
        // The one-hour SMART token wall's own rate. Its own instrument rather than a value on
        // agentforge.authorization_decisions: that series means entitlement, and an aged-out token
        // decides nothing about it - which is why a separate change stopped the authorizer answering expiry
        // with a refusal at all. Untagged beyond `surface`, because the two dimensions that would
        // make it richer - the session and the expiry instant - are the two that would make a dead
        // session worth replaying.
        _expiredSessionRefusalsTotal = _meter.CreateCounter<long>(
            "agentforge.expired_session_refusals", unit: "{refusal}",
            description: "Refusals caused by an expired SMART access token, by the surface that refused.");
        _llmTokensTotal = _meter.CreateCounter<long>(
            "agentforge.llm_tokens", unit: "{token}", description: "LLM tokens consumed, by direction.");
        _llmCostUsdTotal = _meter.CreateCounter<double>(
            "agentforge.llm_cost_usd", unit: "{USD}", description: "Estimated LLM cost.");

        // Week 2 instruments (FR-OBS-W2-1). Dimensions are bounded, low-cardinality, and PHI-free
        // (outcome/worker/node tags only - never a patient value or document text).
        _documentIngestionsTotal = _meter.CreateCounter<long>(
            "agentforge.document_ingestions", unit: "{document}", description: "Document-ingestion attempts, by outcome.");
        _documentIngestionDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.document_ingestion.duration", unit: "s", description: "Wall-clock duration of one ingestion attempt.");
        _workerDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.worker.duration", unit: "s", description: "Wall-clock duration of one supervisor-graph worker, by worker.");
        _routingDecisionsTotal = _meter.CreateCounter<long>(
            "agentforge.routing_decisions", unit: "{decision}", description: "Supervisor routing decisions (handoffs), by from/to node.");
        _evidenceRetrievalsTotal = _meter.CreateCounter<long>(
            "agentforge.evidence_retrievals", unit: "{retrieval}", description: "Evidence-retrieval calls, by hit/miss outcome.");
        _evidenceRetrievalDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.evidence_retrieval.duration", unit: "s", description: "Wall-clock duration of one evidence-retrieval call.");
        _evidenceRetrievalResults = _meter.CreateHistogram<long>(
            "agentforge.evidence_retrieval.results", unit: "{snippet}", description: "Snippets returned by one evidence-retrieval call.");
        _rerankDurationSeconds = _meter.CreateHistogram<double>(
            "agentforge.rerank.duration", unit: "s",
            description: "Wall-clock duration of one rerank call that returned (reranked or unranked); a degraded call is counted on agentforge.retrieval_degradations instead.");
        _retrievalDegradationsTotal = _meter.CreateCounter<long>(
            "agentforge.retrieval_degradations", unit: "{degradation}",
            description: "Retrieval stages degraded (a half or the reranker failed; the pipeline continued without it), by stage.");
        // Its own instrument rather than a tag on document_ingestions: an unlocatable quote does not fail
        // the ingest, so it must not move the ingestion outcome rate.
        _citationQuoteMatchesTotal = _meter.CreateCounter<long>(
            "agentforge.citation_quote_matches", unit: "{citation}",
            description: "Extraction citation quote-location outcomes, by exact / unlocatable / unchecked.");
        // FR-OBS-W2-2's "extraction confidence per document". A histogram, not a gauge or an average: the
        // population is bimodal - a digital PDF grounds every fact and a scan grounds none - so their mean
        // describes no document that exists. The recorded value is the share of the document's CHECKABLE
        // citation quotes that were located, so the axis means one thing end to end; a document with nothing
        // to check is absent from it rather than parked mid-scale (DocumentIngestionService explains why).
        // Explicit buckets for the reason on the constant, and AgentForgeMetricsTests asserts they reach
        // this instrument and not only that the constant is well formed.
        _extractionConfidence = _meter.CreateHistogram<double>(
            "agentforge.extraction_confidence", unit: "{confidence}",
            description: "Share of one ingested document's checkable citation quotes located verbatim in its own text (1.0 all, 0.0 none). Grounding/locatability, NOT model-reported; documents with nothing to check are not observed.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = ExtractionConfidenceBuckets });
        // The other half: an outcome per fact, so the rate has a denominator. `field` is the fact's kind from
        // a closed set of literals, never the extracted field's model-supplied name.
        _extractionFieldOutcomesTotal = _meter.CreateCounter<long>(
            "agentforge.extraction_field_outcomes", unit: "{fact}",
            description: "Field-level extraction grounding outcomes, by bounded field kind and exact / unlocatable / unchecked.");
    }

    /// <inheritdoc />
    public void RecordAgentTurn(AgentTurnType turnType, bool succeeded, TimeSpan duration)
    {
        _agentTurnsTotal.Add(1, new KeyValuePair<string, object?>("outcome", succeeded ? "success" : "failure"));
        _agentTurnDurationSeconds.Record(
            duration.TotalSeconds, new KeyValuePair<string, object?>("turn_type", turnType.ToWireName()));
    }

    /// <inheritdoc />
    public void RecordToolCall(string toolName, bool succeeded, TimeSpan duration)
    {
        _toolCallsTotal.Add(
            1,
            new KeyValuePair<string, object?>("tool", toolName),
            new KeyValuePair<string, object?>("outcome", succeeded ? "success" : "failure"));
        _toolCallDurationSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("tool", toolName));
    }

    /// <inheritdoc />
    public void RecordVerificationResult(bool passed) =>
        _verificationResultsTotal.Add(1, new KeyValuePair<string, object?>("outcome", passed ? "pass" : "fail"));

    /// <inheritdoc />
    public void RecordAuthorizationDecision(bool permitted, string reason) =>
        _authorizationDecisionsTotal.Add(
            1,
            new KeyValuePair<string, object?>("outcome", permitted ? "permit" : "refuse"),
            new KeyValuePair<string, object?>("reason", reason));

    /// <inheritdoc />
    public void RecordOutOfScopeToolCall() => _outOfScopeToolCallsTotal.Add(1);

    /// <inheritdoc />
    public void RecordExpiredSessionRefusal(string surface) =>
        _expiredSessionRefusalsTotal.Add(1, new KeyValuePair<string, object?>("surface", surface));

    /// <inheritdoc />
    public void RecordLlmUsage(int inputTokens, int outputTokens, decimal estimatedCostUsd)
    {
        _llmTokensTotal.Add(inputTokens, new KeyValuePair<string, object?>("direction", "input"));
        _llmTokensTotal.Add(outputTokens, new KeyValuePair<string, object?>("direction", "output"));
        _llmCostUsdTotal.Add((double)estimatedCostUsd);
    }

    /// <inheritdoc />
    public void RecordDocumentIngestion(string outcome, TimeSpan duration)
    {
        _documentIngestionsTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        _documentIngestionDurationSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <inheritdoc />
    public void RecordWorkerLatency(string worker, TimeSpan duration) =>
        _workerDurationSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("worker", worker));

    /// <inheritdoc />
    public void RecordRoutingDecision(string fromNode, string toNode) =>
        _routingDecisionsTotal.Add(
            1,
            new KeyValuePair<string, object?>("from", fromNode),
            new KeyValuePair<string, object?>("to", toNode));

    /// <inheritdoc />
    public void RecordEvidenceRetrieval(bool hit, int resultCount, TimeSpan duration, EvidenceRetrievalEntryPoint entryPoint)
    {
        var entry = new KeyValuePair<string, object?>("entry_point", entryPoint.ToWireName());
        _evidenceRetrievalsTotal.Add(1, new KeyValuePair<string, object?>("outcome", hit ? "hit" : "miss"), entry);
        _evidenceRetrievalDurationSeconds.Record(duration.TotalSeconds, entry);
        _evidenceRetrievalResults.Record(resultCount, entry);
    }

    /// <inheritdoc />
    public void RecordRerankLatency(TimeSpan duration) => _rerankDurationSeconds.Record(duration.TotalSeconds);

    /// <inheritdoc />
    public void RecordRetrievalDegradation(string stage) =>
        _retrievalDegradationsTotal.Add(1, new KeyValuePair<string, object?>("stage", stage));

    /// <inheritdoc />
    public void RecordCitationQuoteMatch(string outcome) =>
        _citationQuoteMatchesTotal.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void RecordExtractionConfidence(string documentType, double confidence) =>
        _extractionConfidence.Record(
            confidence, new KeyValuePair<string, object?>("document_type", documentType));

    /// <inheritdoc />
    public void RecordExtractionFieldOutcome(string field, string outcome) =>
        _extractionFieldOutcomesTotal.Add(
            1,
            new KeyValuePair<string, object?>("field", field),
            new KeyValuePair<string, object?>("outcome", outcome));

    // Every instance shares MeterName, so a listener tells this one's instruments apart only by the Meter
    // object itself.
    internal Meter Meter => _meter;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
