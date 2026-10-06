namespace AgentForge.Evals;

/// <summary>One golden-set eval case. Deterministic: the stubbed model response (extraction) or the pinned
/// tool call and clinic-day fixture (authorization) is fixed, so the case's pass/fail is reproducible and a
/// regression in the pipeline flips it (the hard-gate mechanism).</summary>
internal sealed record GoldenCase
{
    public required string Id { get; init; }

    /// <summary>
    /// The failure mode this case defends against — the boundary, invariant or regression risk, not a
    /// restatement of <see cref="Id"/> (FR-EVAL-1: "each documents the failure mode it guards"). Required,
    /// and <see cref="GoldenCaseLoader"/> refuses a case that leaves it blank, so a new case cannot skip it.
    /// The console gate prints it beside a failing case: a red build should say what broke, not only which
    /// id did.
    /// </summary>
    public required string Guards { get; init; }

    /// <summary>Capability under test: "extraction", "authorization", "answer" or "evidence".</summary>
    public required string Category { get; init; }

    /// <summary>"lab_pdf" or "intake_form". Extraction cases only.</summary>
    public string? DocType { get; init; }

    /// <summary>IANA media type of the (synthetic) document.</summary>
    public string MediaType { get; init; } = "application/pdf";

    /// <summary>The response the (stubbed) vision model returns. Extraction cases only.</summary>
    public string? StubModelResponse { get; init; }

    /// <summary>
    /// The adversarial scenario under test. Authorization cases only.
    /// </summary>
    public AuthorizationScenario? Authorization { get; init; }

    /// <summary>The Week 1 answer-path scenario under test. Answer cases only.</summary>
    public AnswerScenario? Answer { get; init; }

    /// <summary>The Week 2 evidence-retrieval scenario under test. Evidence cases only.</summary>
    public EvidenceScenario? Evidence { get; init; }

    /// <summary>Whether the case is expected to yield data: extraction passed the schema gate, the
    /// requester was entitled to the patient and the tool ran, the turn shipped a model-synthesized
    /// answer rather than degrading to the deterministic fallback, or — for an evidence case — the
    /// retriever returned at least one guideline chunk.</summary>
    public required bool ExpectSuccess { get; init; }

    /// <summary>Substrings that must appear in the canonical extraction, the tool result
    /// (factually_consistent) or the verified answer that shipped (grounded_answer /
    /// transparent_degradation).</summary>
    public IReadOnlyList<string>? ExpectedValues { get; init; }

    /// <summary>Values that must NEVER appear in logs for this case (no_phi_in_logs).</summary>
    public IReadOnlyList<string>? PhiTokens { get; init; }

    /// <summary>Which boolean rubrics apply to this case.</summary>
    public required IReadOnlyList<string> Rubrics { get; init; }
}

/// <summary>
/// One FR-AUTH-2 / FR-AUTH-3 adversarial scenario: who is asking, whose record the session is bound to,
/// what the clinic day looks like, and the tool call a fully-compromised model emitted.
/// </summary>
/// <remarks>
/// <see cref="InjectedInstruction"/> is documentation, not an input. Authorization is enforced *below* the
/// model (FR-AUTH-3), so the honest worst case is not "does the model resist the instruction?" but "the
/// model obeyed it completely — does the tool layer still hold?". <see cref="ToolCall"/> is therefore the
/// call the injection would have produced, pinned, which is also what keeps the case deterministic: no
/// model is consulted.
/// </remarks>
internal sealed record AuthorizationScenario
{
    /// <summary>What is being attempted: role_confusion, document_injection, user_turn_injection,
    /// patient_scope_widening or site_scope_widening.</summary>
    public required string Vector { get; init; }

    /// <summary>Prose description of the requester, for the report and for the reader.</summary>
    public required string RequesterRole { get; init; }

    /// <summary>The authenticated requester, or null when no identity is in scope (FR-AUTH-1).</summary>
    public string? RequesterIdentity { get; init; }

    /// <summary>OpenEMR multi-site segment the session is bound to.</summary>
    public required string Site { get; init; }

    /// <summary>The patient the session is bound to — what the dispatcher forces on every call.</summary>
    public required string SessionPatientId { get; init; }

    /// <summary>The adversarial text this case describes. Never fed to anything; see the type remarks.</summary>
    public string? InjectedInstruction { get; init; }

    /// <summary>The clinic day the relationship gate decides against.</summary>
    public required IReadOnlyList<AppointmentFixture> ClinicDayAppointments { get; init; }

    /// <summary>Makes the appointment lookup throw, so the fail-closed path is the one under test.</summary>
    public bool AppointmentLookupFails { get; init; }

    /// <summary>The tool call dispatched, exactly as a compromised model would have emitted it.</summary>
    public required ToolCallFixture ToolCall { get; init; }

    /// <summary>Substrings that must NOT appear in the tool result — the M3 disclosure check.</summary>
    public IReadOnlyList<string>? ForbiddenValues { get; init; }

    /// <summary>Log substrings the attempt must have produced (FR-AUTH-3 "attempt is logged", FR-AUTH-4).</summary>
    public required IReadOnlyList<string> ExpectedLogFragments { get; init; }
}

/// <summary>
/// One Week 1 <b>answer-path</b> scenario: the chart the tools serve, the model turns the orchestrator is
/// fed, and what the verified answer must and must not contain. This is the population M1 (groundedness),
/// M2 (constraint recall) and M5 (transparent degradation) are stated over and had none of until now
/// (METRICS.md §2). A separate change
/// </summary>
/// <remarks>
/// <para>
/// The case pins the <em>model</em>, exactly as an authorization case pins the tool call: the shipped
/// <c>AgentOrchestrator</c>, <c>ClinicalResponseVerifier</c>, <c>SourceAttributionEngine</c>,
/// <c>CardiologyConstraintEngine</c> and <c>ToolResultJsonScanner</c> all run for real, and what is scored
/// is what they did to a fixed draft. No model is consulted, so the tier stays deterministic.
/// </para>
/// <para>
/// Source ids in <see cref="Chart"/> are FHIR-shaped on purpose - <c>SourceAttributionEngine</c>'s citation
/// pattern resolves <c>[ResourceType/Id]</c> only for ids matching <c>[A-Za-z0-9.-]</c>, so the
/// authorization harness's <c>{site}:{patientId}:{n}</c> keys would never parse as a citation at all.
/// </para>
/// </remarks>
internal sealed record AnswerScenario
{
    /// <summary>Which success metric this case's population belongs to: "M1", "M2" or "M5".</summary>
    public required string Metric { get; init; }

    /// <summary>
    /// What the fixture <em>is</em> and what the case asserts about it, in prose - for the console report
    /// and for the reader. Deliberately mechanical: <b>why</b> the case exists, and what breaks in production
    /// without it, belongs in the case's top-level <c>guards</c> string, which the loader will
    /// require and the gate prints beside a failing case.
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>OpenEMR multi-site segment the session is bound to.</summary>
    public required string Site { get; init; }

    /// <summary>The one patient the session is scoped to.</summary>
    public required string PatientId { get; init; }

    /// <summary>The synthetic chart the tools return this turn. Null serves an empty patient record.</summary>
    public AnswerChartFixture? Chart { get; init; }

    /// <summary>Tools that fail instead of returning data - REQUIREMENTS.md §13.1's "tool call fails" row.</summary>
    public IReadOnlyList<ToolFailureFixture>? FailingTools { get; init; }

    /// <summary>The model's turns, in order - one per LLM call the orchestrator makes.</summary>
    public required IReadOnlyList<ModelTurnFixture> ModelScript { get; init; }

    /// <summary>Overrides <c>AgentOptions.TurnDeadline</c>, for the "tool slow / hits deadline" row.</summary>
    public int? TurnDeadlineMs { get; init; }

    /// <summary>Fragments that must be suppressed by FR-VERIF-1 <b>and</b> absent from the shipped answer.</summary>
    public IReadOnlyList<string>? ExpectedSuppressed { get; init; }

    /// <summary>Fragments the shipped answer must never contain - the anti-fabrication half of M5.</summary>
    public IReadOnlyList<string>? ForbiddenAnswerFragments { get; init; }

    /// <summary>
    /// Log substrings the turn must have produced. REQUIREMENTS.md §13.1's "never fail silently" invariant has two
    /// halves - the degradation is visible to the user <b>and</b> recorded in observability - and an answer
    /// that says the right thing while logging nothing satisfies only one of them.
    /// </summary>
    public IReadOnlyList<string>? ExpectedLogFragments { get; init; }

    /// <summary>Rule ids the seeded chart must make <c>CardiologyConstraintEngine</c> raise - M2's numerator.</summary>
    public IReadOnlyList<string>? ExpectedFlagRuleIds { get; init; }

    /// <summary>
    /// Marks a near-miss control: a chart deliberately outside every rule, which must raise nothing.
    /// Recall measured without one is unfalsifiable - an engine that flagged every chart would score 100%.
    /// </summary>
    public bool ExpectNoConstraintFlags { get; init; }

    /// <summary>
    /// Marks a case pinning <c>SourceAttributionEngine</c>'s documented limit: a claim phrased without any
    /// <c>ClinicalFactKeywords</c> term ships uncited. Counted and printed <b>separately</b> from M1's own
    /// number, because folding a known escape into a 100% reading is how a metric stops meaning anything.
    /// </summary>
    public bool KnownKeywordEscape { get; init; }

    /// <summary>
    /// Marks a case pinning NG1's prompt-only limit: a treatment recommendation that cites correctly passes
    /// FR-VERIF-1 and ships, because grounding decides whether a claim is supported and never what kind of
    /// act it performs. Counted and printed <b>beside</b> M1's number, so a green run cannot be read as "no
    /// recommendation can ship". A separate change
    /// </summary>
    public bool KnownScopeEscape { get; init; }
}

/// <summary>The synthetic chart one answer case's tools serve. Entirely invented - never real PHI.</summary>
internal sealed record AnswerChartFixture
{
    public string DisplayName { get; init; } = "Synthetic Testpatient";

    public IReadOnlyList<ProblemFixture> Problems { get; init; } = [];

    public IReadOnlyList<MedicationFixture> Medications { get; init; } = [];

    public IReadOnlyList<AllergyFixture> Allergies { get; init; } = [];

    public IReadOnlyList<LabFixture> Labs { get; init; } = [];
}

/// <summary>One active problem. <c>Id</c> is the FHIR resource id a citation resolves against.</summary>
internal sealed record ProblemFixture
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public string ClinicalStatus { get; init; } = "active";
}

/// <summary>One medication on the list.</summary>
internal sealed record MedicationFixture
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public string? Dosage { get; init; }

    public string Status { get; init; } = "active";
}

/// <summary>One allergy/intolerance.</summary>
internal sealed record AllergyFixture
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public string ClinicalStatus { get; init; } = "active";
}

/// <summary>One laboratory Observation - the values the domain-constraint rules read.</summary>
internal sealed record LabFixture
{
    public required string Id { get; init; }

    public required string Display { get; init; }

    public double? Value { get; init; }

    public string? Unit { get; init; }

    public double? ReferenceRangeLow { get; init; }

    public double? ReferenceRangeHigh { get; init; }

    /// <summary>
    /// When the result was drawn. Null - the default, and what every case carried before <c>a separate change</c> -
    /// serves an Observation with no <c>effectiveDateTime</c>, a real FHIR shape that
    /// <c>ObservationRecency</c> sorts to <see cref="DateTimeOffset.MinValue"/>: it loses to <b>any</b>
    /// dated result for the same code, and survives only when nothing dated shares that code. Set it to put
    /// a rule's recency ranking under the gate rather than under unit tests alone (<c>ARCHITECTURE.md</c>
    /// §9.3) - a case seeding a superseded result is what tells a ranked rule from one that reads the whole
    /// history. Two undated results tie, and the tie keeps the order the tool returned them in, so a case
    /// relying on which one wins must date them.
    /// </summary>
    public DateTimeOffset? EffectiveDateTime { get; init; }
}

/// <summary>A tool that fails this turn, and the error text the dispatcher would have returned.</summary>
internal sealed record ToolFailureFixture
{
    public required string ToolName { get; init; }

    /// <summary>The user-facing message <c>McpToolDispatcher</c> puts in its <c>{"error": …}</c> result.</summary>
    public required string Error { get; init; }
}

/// <summary>
/// One pinned model turn - the answer-path analogue of <see cref="ToolCallFixture"/>. Exactly one of
/// <see cref="Text"/>, <see cref="ToolCalls"/>, <see cref="FailsWith"/> and <see cref="NeverReturns"/>
/// describes what the provider does on that call.
/// </summary>
internal sealed record ModelTurnFixture
{
    /// <summary>The draft answer this turn returns.</summary>
    public string? Text { get; init; }

    /// <summary>Tools this turn asks for; non-empty makes the turn a tool-use turn.</summary>
    public IReadOnlyList<ToolCallFixture>? ToolCalls { get; init; }

    /// <summary>"end_turn" (default), "max_tokens" or "other" - REQUIREMENTS.md §13.1's malformed-output row.</summary>
    public string? StopReason { get; init; }

    /// <summary>Makes the provider throw after its own retries: "timeout", "rate_limit" or "provider_error".</summary>
    public string? FailsWith { get; init; }

    /// <summary>Makes the call hang until the turn deadline cancels it - the "tool slow / hits deadline" row.</summary>
    public bool NeverReturns { get; init; }
}

/// <summary>One seeded appointment on the clinic day the gate reads.</summary>
internal sealed record AppointmentFixture
{
    public required string PatientId { get; init; }

    /// <summary>Raw FHIR actor reference, e.g. <c>Practitioner/{id}</c> or <c>Person/{id}</c>.</summary>
    public required string ProviderReference { get; init; }

    /// <summary>FHIR <c>Appointment.status</c>.</summary>
    public required string Status { get; init; }
}

/// <summary>The tool call under test.</summary>
internal sealed record ToolCallFixture
{
    public required string ToolName { get; init; }

    public string ArgumentsJson { get; init; } = "{}";
}

/// <summary>
/// One Week 2 <b>evidence-retrieval</b> scenario: the guideline corpus, what each half of hybrid retrieval
/// ranked, what the reranker did with the fused pool, and what the composed answer must and must not carry.
/// This is the population the eval gate had nothing in at all - hybrid RAG shipped scored by no case, so a
/// regression in RRF fusion, in the rerank call or in the degradation path passed
/// unnoticed. A separate change
/// </summary>
/// <remarks>
/// <para>
/// The case pins the two retrieval <em>halves</em> and the reranker, exactly as an answer case pins the
/// model and an authorization case pins the tool call: the shipped <c>HybridEvidenceRetriever</c>,
/// <c>ReciprocalRankFusion</c>, <c>EvidenceAgentSupervisor</c>, <c>ClinicalResponseVerifier</c> and
/// <c>SourceAttributionEngine</c> all run for real. Nothing about fusion, ordering or degradation is
/// re-implemented in the harness, so changing any of them flips cases here. No database, no embedding
/// call and no rerank API key.
/// </para>
/// <para>
/// Chunk ids here are readable synthetic slugs; in production <c>FtsEvidenceRetriever</c> returns the
/// chunk's GUID as text. What has to hold is the <b>citation charset</b> - <c>SourceAttributionEngine</c>
/// resolves <c>[ResourceType/Id]</c> only for ids matching <c>[A-Za-z0-9.-]</c>, which a GUID and a slug
/// both satisfy and an underscore does not.
/// </para>
/// </remarks>
internal sealed record EvidenceScenario
{
    /// <summary>
    /// What the fixture <em>is</em> and what the case asserts about it, in prose - for the console report
    /// and for the reader. The same split the answer cases carry: <b>why</b> the case exists belongs in the
    /// top-level <c>guards</c> string, which the loader requires.
    /// </summary>
    public required string Intent { get; init; }

    /// <summary>The one patient the question is scoped to.</summary>
    public required string PatientId { get; init; }

    /// <summary>The clinician's question, handed to the retriever verbatim.</summary>
    public required string Question { get; init; }

    /// <summary>The guideline chunks this case's corpus holds, keyed for retrieval by chunk id.</summary>
    public required IReadOnlyList<GuidelineChunkFixture> Corpus { get; init; }

    /// <summary>Chunk ids the sparse (FTS) half returns, best-first. Empty models a query it matches nothing for.</summary>
    public IReadOnlyList<string> SparseRanking { get; init; } = [];

    /// <summary>Chunk ids the dense (pgvector) half returns, best-first.</summary>
    public IReadOnlyList<string> DenseRanking { get; init; } = [];

    /// <summary>
    /// Chunk ids the reranker returns, best-first. Null leaves the RRF-fused order in place the way an
    /// empty rerank response does; it is <b>not</b> the same as <see cref="FailingStages"/> naming
    /// "rerank", which is the exception path.
    /// </summary>
    public IReadOnlyList<string>? RerankRanking { get; init; }

    /// <summary>
    /// The reranker returns all of <see cref="RerankRanking"/> rather than the <c>topK</c> it was asked
    /// for. <c>IReranker</c> promises at most <c>topK</c> and the Cohere provider sends it as <c>top_n</c>,
    /// so a reranker that honours it hides <c>HybridEvidenceRetriever</c>'s own cap on the reranked path;
    /// this is the only way a case can reach that cap. A separate change
    /// </summary>
    public bool RerankIgnoresTopK { get; init; }

    /// <summary>
    /// Retrieval stages that throw this run: "sparse", "dense" or "rerank". The hybrid retriever must
    /// degrade past each one rather than propagate, and must record having done so.
    /// </summary>
    public IReadOnlyList<string> FailingStages { get; init; } = [];

    /// <summary>
    /// Stages <c>RecordRetrievalDegradation</c> must have been called with, as a set. Compared for
    /// <b>equality</b>, not containment, so a case that expects none and silently degrades anyway is red -
    /// which is the whole failure mode this slice exists to surface.
    /// </summary>
    public IReadOnlyList<string> ExpectedDegradedStages { get; init; } = [];

    /// <summary>Chunk ids the retrieved set must contain (retrieval_hit's set-membership half).</summary>
    public IReadOnlyList<string>? ExpectedChunkIds { get; init; }

    /// <summary>The chunk id that must rank <b>first</b> in what the retriever returned - the rerank-ordering half.</summary>
    public string? ExpectedTopChunkId { get; init; }

    /// <summary>
    /// The whole retrieved list, in order, exactly. Pins a degradation as <em>deterministic</em> rather
    /// than merely survivable: the RRF-fused order a failed reranker falls back to is a computed order, and
    /// naming it is what tells "kept the fused order" from "returned whatever arrived first".
    /// </summary>
    public IReadOnlyList<string>? ExpectedChunkOrder { get; init; }

    /// <summary>
    /// Marks the out-of-corpus control: the query must retrieve <b>nothing</b>. Without one, retrieval_hit
    /// is a membership check no retriever can fail by returning more - a retriever that returned the whole
    /// corpus for every query would sweep every hit case.
    /// </summary>
    public bool ExpectNoEvidence { get; init; }

    /// <summary>
    /// Citations (<c>Guideline/&lt;chunkId&gt;</c>, unbracketed) the shipped answer must carry, each
    /// resolving to a chunk this turn actually retrieved.
    /// </summary>
    public IReadOnlyList<string>? ExpectedGuidelineCitations { get; init; }

    /// <summary>
    /// Citations the critic must have suppressed: absent from the shipped answer <b>and</b> present in the
    /// suppressed-claims list. Recording a fabricated citation is not the same as removing it.
    /// </summary>
    public IReadOnlyList<string>? ExpectedSuppressedCitations { get; init; }

    /// <summary>Fragments the shipped answer must never contain - the anti-invention half.</summary>
    public IReadOnlyList<string>? ForbiddenAnswerFragments { get; init; }

    /// <summary>Log substrings the run must have produced, e.g. the degradation warning.</summary>
    public IReadOnlyList<string>? ExpectedLogFragments { get; init; }

    /// <summary>The model's turns - the composer makes exactly one call, so one turn.</summary>
    public required IReadOnlyList<ModelTurnFixture> ModelScript { get; init; }
}

/// <summary>One synthetic guideline chunk in an evidence case's corpus. Entirely invented - never real PHI.</summary>
internal sealed record GuidelineChunkFixture
{
    /// <summary>Stable chunk id, and the <c>[Guideline/&lt;id&gt;]</c> citation token.</summary>
    public required string ChunkId { get; init; }

    /// <summary>Guideline document the chunk came from.</summary>
    public required string DocumentId { get; init; }

    /// <summary>Section heading within that document.</summary>
    public required string Section { get; init; }

    /// <summary>The snippet text the reranker scores and the composer is shown.</summary>
    public required string Text { get; init; }
}

/// <summary>The committed baseline the gate compares against.</summary>
internal sealed record EvalBaseline
{
    /// <summary>
    /// The absolute floor for each <b>tier</b>, keyed by the tier name a category declares. Two tiers ship:
    /// <c>safety</c> at <c>1.0</c>, where a single failing case must block and the regression arm therefore
    /// has nothing to add, and <c>quality</c> at <c>0.80</c>, chosen so the >5% regression arm is reachable.
    /// No default — an unknown or absent tier is a gate failure rather than a lenient fallback, which is
    /// what stops a rubric added later from inheriting the looser floor in silence. A separate change
    /// </summary>
    public required IReadOnlyDictionary<string, double> PassThresholds { get; init; }

    /// <summary>Maximum tolerated drop from a category's baseline before the build fails.</summary>
    public double MaxRegression { get; init; } = 0.05;

    /// <summary>Per-category baseline pass rate and tier membership.</summary>
    public required IReadOnlyDictionary<string, CategoryBaseline> Categories { get; init; }

    /// <summary>
    /// How many cases pin each documented limit, keyed by <see cref="EvalGatePolicy.KeywordBoundaryEscape"/>
    /// and <see cref="EvalGatePolicy.ScopeEscape"/>. A run whose count differs fails the gate, so a limit
    /// stops being reported only by a deliberate edit here. A separate change
    /// </summary>
    public IReadOnlyDictionary<string, int> PinnedEscapes { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>One category's recorded baseline and the policy tier it is governed by.</summary>
internal sealed record CategoryBaseline
{
    /// <summary>The pass rate this repository last recorded for the category; the regression arm measures
    /// the drop from here.</summary>
    public required double Baseline { get; init; }

    /// <summary>Which <see cref="EvalBaseline.PassThresholds"/> entry sets this category's absolute floor.
    /// <b>Required</b>, so adding a rubric forces the safety-or-quality decision to be made and written
    /// down rather than defaulted into. A separate change</summary>
    public required string Tier { get; init; }
}
