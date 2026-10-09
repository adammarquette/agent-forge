using System.Diagnostics;
using System.Text.Json;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.Verification;
using Microsoft.Extensions.Logging;

namespace AgentForge.Agents;

/// <summary>
/// The Week 2 supervisor: a small, typed, inspectable graph (ARCHITECTURE-DOCUMENTS.md §6). It routes explicitly —
/// intake-extractor (when a document is attached) → evidence-retriever → answer-composer → critic — logging
/// every handoff. The critic is the Week 1 <see cref="IClinicalResponseVerifier"/>, reused as a node: it
/// suppresses uncited claims and surfaces domain-constraint flags. The supervisor never fabricates; on a
/// worker failure it degrades and continues rather than crashing (NFR-REL-1).
/// </summary>
public sealed class EvidenceAgentSupervisor : IEvidenceAgentSupervisor
{
    private const int DefaultTopK = 5;
    private const string SupervisorNode = EvidenceGraphNodes.Supervisor;

    private readonly IDocumentExtractor _extractor;
    private readonly IEvidenceRetriever _retriever;
    private readonly IDerivedFactStore _factStore;
    private readonly ILlmProvider _llm;
    private readonly IClinicalResponseVerifier _verifier;
    private readonly IAgentForgeMetrics _metrics;
    private readonly ILogger<EvidenceAgentSupervisor> _logger;

    /// <summary>Creates the supervisor.</summary>
    public EvidenceAgentSupervisor(
        IDocumentExtractor extractor,
        IEvidenceRetriever retriever,
        IDerivedFactStore factStore,
        ILlmProvider llm,
        IClinicalResponseVerifier verifier,
        IAgentForgeMetrics metrics,
        ILogger<EvidenceAgentSupervisor> logger)
    {
        _extractor = extractor;
        _retriever = retriever;
        _factStore = factStore;
        _llm = llm;
        _verifier = verifier;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EvidenceAgentResult> RunAsync(EvidenceAgentRequest request, CancellationToken cancellationToken)
    {
        // One supervisor span per run, each worker a child of it (NFR-TRACE-W2). Names, routes, outcomes and
        // counts only - never the patient, the question, the document or its values (ARCHITECTURE-DOCUMENTS.md §12).
        using var supervisorSpan = AgentForgeActivitySource.Instance.StartActivity(EvidenceTracing.SupervisorSpan);
        try
        {
            return await RunGraphAsync(request, supervisorSpan, cancellationToken);
        }
        catch (Exception ex)
        {
            EvidenceTracing.RecordFailure(supervisorSpan, ex);
            throw;
        }
    }

    private async Task<EvidenceAgentResult> RunGraphAsync(
        EvidenceAgentRequest request, Activity? supervisorSpan, CancellationToken cancellationToken)
    {
        var handoffs = new List<HandoffEvent>();
        string? factsJson = null;
        IReadOnlyList<DocumentCitation> documentCitations = [];

        // 1. intake-extractor - only when a document is attached this turn.
        if (request.Document is { } document)
        {
            using var span = StartWorker(handoffs, supervisorSpan, SupervisorNode, EvidenceGraphNodes.IntakeExtractor, "document attached; extraction needed");
            try
            {
                var extractStart = Stopwatch.GetTimestamp();
                var extraction = await _extractor.ExtractAsync(
                    document.DocumentType, document.Content, document.MediaType, cancellationToken);
                _metrics.RecordWorkerLatency(EvidenceGraphNodes.IntakeExtractor, Stopwatch.GetElapsedTime(extractStart));

                if (extraction.Succeeded)
                {
                    factsJson = extraction.CanonicalJson;
                    documentCitations = DocumentCitationExtractor.Extract(factsJson, document.DocumentType);
                    span?.SetTag(EvidenceTracing.Outcome, "extracted");
                }
                else
                {
                    span?.SetTag(EvidenceTracing.Outcome, "rejected");
                    EvidenceAgentSupervisorLog.ExtractionDegraded(_logger);
                    Route(handoffs, supervisorSpan, EvidenceGraphNodes.IntakeExtractor, SupervisorNode, "extraction rejected; continuing without document facts");
                }
            }
            catch (Exception ex)
            {
                EvidenceTracing.RecordFailure(span, ex);
                throw;
            }
        }

        // Project the extracted patient labs into citable facts once - reused by the composer (to cite
        // them) and the critic (to resolve those citations). Empty for non-lab extractions.
        var labFacts = ExtractLabFacts(factsJson);

        // Load facts ingested for this patient BEFORE this turn (the pre-visit E2 path): the brief must
        // surface these, not only a document attached this turn (UC-9). Read-only; empty when none on file.
        var storedFacts = await _factStore.GetByPatientAsync(request.PatientId, cancellationToken);
        var priorFacts = ProjectDerivedFacts(storedFacts);
        // Surface persisted facts as fetchable click-to-source citations (each carries its OpenEMR
        // DocumentReference id, so the client renders the source PDF from OpenEMR) alongside any in-turn ones.
        documentCitations = [.. documentCitations, .. DerivedFactCitationProjector.Project(storedFacts)];
        if (priorFacts.Count > 0)
        {
            Route(handoffs, supervisorSpan, SupervisorNode, EvidenceGraphNodes.AnswerComposer,
                $"{priorFacts.Count} pre-ingested document fact(s) on file for the patient");
        }

        // 2. evidence-retriever.
        IReadOnlyList<EvidenceSnippet> evidence;
        using (var span = StartWorker(handoffs, supervisorSpan, SupervisorNode, EvidenceGraphNodes.EvidenceRetriever, "question needs guideline evidence"))
        {
            try
            {
                var retrieveStart = Stopwatch.GetTimestamp();
                evidence = await _retriever.RetrieveAsync(request.Question, DefaultTopK, cancellationToken);
                var retrieveElapsed = Stopwatch.GetElapsedTime(retrieveStart);
                _metrics.RecordWorkerLatency(EvidenceGraphNodes.EvidenceRetriever, retrieveElapsed);
                _metrics.RecordEvidenceRetrieval(evidence.Count > 0, evidence.Count, retrieveElapsed, EvidenceRetrievalEntryPoint.EvidenceAsk);
                span?.SetTag(EvidenceTracing.Outcome, evidence.Count > 0 ? "hit" : "miss");
                span?.SetTag(EvidenceTracing.SnippetCount, evidence.Count);
            }
            catch (Exception ex)
            {
                EvidenceTracing.RecordFailure(span, ex);
                throw;
            }
        }

        // 3. answer-composer.
        string draft;
        using (var span = StartWorker(handoffs, supervisorSpan, SupervisorNode, EvidenceGraphNodes.AnswerComposer, "facts + evidence assembled"))
        {
            try
            {
                var composeStart = Stopwatch.GetTimestamp();
                draft = await ComposeAsync(request.Question, factsJson, labFacts, priorFacts, evidence, cancellationToken);
                _metrics.RecordWorkerLatency(EvidenceGraphNodes.AnswerComposer, Stopwatch.GetElapsedTime(composeStart));
                span?.SetTag(EvidenceTracing.Outcome, "composed");
            }
            catch (Exception ex)
            {
                EvidenceTracing.RecordFailure(span, ex);
                throw;
            }
        }

        // 4. critic = the Week 1 verification gate, reused as a node.
        VerificationResult verification;
        using (var span = StartWorker(handoffs, supervisorSpan, EvidenceGraphNodes.AnswerComposer, EvidenceGraphNodes.Critic, "draft ready for verification"))
        {
            try
            {
                var criticStart = Stopwatch.GetTimestamp();
                verification = _verifier.Verify(draft, BuildToolResults(factsJson, labFacts, priorFacts, evidence));
                _metrics.RecordWorkerLatency(EvidenceGraphNodes.Critic, Stopwatch.GetElapsedTime(criticStart));
                span?.SetTag(EvidenceTracing.Outcome, verification.Passed ? "passed" : "suppressed");
                span?.SetTag(EvidenceTracing.SuppressedClaims, verification.SuppressedClaims.Count);
                span?.SetTag(EvidenceTracing.ConstraintFlags, verification.ConstraintFlags.Count);
            }
            catch (Exception ex)
            {
                EvidenceTracing.RecordFailure(span, ex);
                throw;
            }
        }

        Route(handoffs, supervisorSpan, EvidenceGraphNodes.Critic, SupervisorNode,
            verification.Passed ? "all claims grounded" : $"{verification.SuppressedClaims.Count} uncited claim(s) suppressed");
        supervisorSpan?.SetTag(EvidenceTracing.HandoffCount, handoffs.Count);

        return new EvidenceAgentResult
        {
            Answer = verification.VerifiedAnswer,
            SafetyFlags = verification.ConstraintFlags,
            SuppressedClaims = verification.SuppressedClaims,
            Handoffs = handoffs,
            ExtractedFactsJson = factsJson,
            DocumentCitations = documentCitations,
            Evidence = evidence,
        };
    }

    // Records the handoff, then opens the worker's span as a child of the supervisor span (the ambient one),
    // tagged with who routed to it and why - the same decision the handoff log line carries.
    private Activity? StartWorker(
        List<HandoffEvent> handoffs, Activity? supervisorSpan, string from, string worker, string reason)
    {
        Route(handoffs, supervisorSpan, from, worker, reason);
        var span = AgentForgeActivitySource.Instance.StartActivity(EvidenceTracing.WorkerSpanPrefix + worker);
        span?.SetTag(EvidenceTracing.Worker, worker);
        span?.SetTag(EvidenceTracing.RouteFrom, from);
        span?.SetTag(EvidenceTracing.RouteReason, reason);
        return span;
    }

    private void Route(List<HandoffEvent> handoffs, Activity? supervisorSpan, string from, string to, string reason)
    {
        handoffs.Add(new HandoffEvent(from, to, reason));
        EvidenceAgentSupervisorLog.Handoff(_logger, from, to, reason);
        _metrics.RecordRoutingDecision(from, to);
        supervisorSpan?.AddEvent(new ActivityEvent(EvidenceTracing.HandoffEvent, tags: new ActivityTagsCollection
        {
            [EvidenceTracing.RouteFrom] = from,
            [EvidenceTracing.RouteTo] = to,
            [EvidenceTracing.RouteReason] = reason,
        }));
    }

    private async Task<string> ComposeAsync(
        string question, string? factsJson, IReadOnlyList<LabFact> labFacts,
        IReadOnlyList<DerivedFactView> priorFacts, IReadOnlyList<EvidenceSnippet> evidence,
        CancellationToken cancellationToken)
    {
        var facts = factsJson ?? "(no document facts on file)";
        var labText = labFacts.Count == 0
            ? "(no patient lab values extracted)"
            : string.Join("\n", labFacts.Select(l =>
                $"[Lab/{l.Slug}] {l.TestName}: {l.Value}{(l.Unit is null ? "" : $" {l.Unit}")}"
                + $"{(l.ReferenceRange is null ? "" : $" (ref {l.ReferenceRange})")}{(l.Abnormal == true ? " [ABNORMAL]" : "")}"));
        var derivedText = priorFacts.Count == 0
            ? "(no prior-document facts on file)"
            : string.Join("\n", priorFacts.Select(d =>
                $"[Derived/{d.Slug}] {d.FactType}: {d.Value} (source document {d.SourceDocRef}{(d.Page is null ? "" : $", page {d.Page}")})"));
        var evidenceText = evidence.Count == 0
            ? "(no guideline evidence found)"
            : string.Join("\n", evidence.Select(e => $"[Guideline/{e.ChunkId}] {e.DocumentId} - {e.Section}: {e.Text}"));

        var userContent =
            $"Question: {question}\n\n"
            + $"Patient lab values (cite each with the token shown):\n{labText}\n\n"
            + $"Patient facts from prior documents, source_type derived (cite each with the token shown):\n{derivedText}\n\n"
            + $"Full extracted document facts (JSON):\n{facts}\n\n"
            + $"Guideline evidence:\n{evidenceText}";

        var response = await _llm.CompleteAsync(
            new LlmRequest(EvidenceComposerPrompt.System, [LlmMessage.FromText(LlmRole.User, userContent)]),
            cancellationToken);

        // Meter the composer's LLM call, the same way the Week-1 orchestrator does - the evidence graph
        // bypasses AgentOrchestrator, so without this the Week 2 flow's tokens/cost never reach the metrics.
        _metrics.RecordLlmUsage(response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.EstimatedCostUsd);

        return response.Content;
    }

    // Hand the extracted facts and retrieved evidence to the critic as the tool results the answer must be
    // grounded in - the same shape the Week 1 orchestrator passes to the verifier.
    private static List<string> BuildToolResults(
        string? factsJson, IReadOnlyList<LabFact> labFacts, IReadOnlyList<DerivedFactView> priorFacts,
        IReadOnlyList<EvidenceSnippet> evidence)
    {
        var results = new List<string>();
        if (factsJson is not null)
        {
            results.Add(factsJson);
        }

        if (labFacts.Count > 0)
        {
            // Project extracted labs to the scanner's citation shape (ResourceType "Lab" + Id), so a
            // [Lab/<slug>] citation for a patient-specific value resolves instead of being suppressed.
            var labRecords = labFacts
                .Select(l => new LabToolResult("Lab", l.Slug, l.TestName, l.Value))
                .ToArray();
            results.Add(JsonSerializer.Serialize(labRecords, AgentsJsonContext.Default.LabToolResultArray));
        }

        if (priorFacts.Count > 0)
        {
            // Same mechanism for pre-ingested document facts (ResourceType "Derived" + Id slug), so a
            // [Derived/<slug>] citation resolves and the fact isn't suppressed as an uncited claim.
            var derivedRecords = priorFacts
                .Select(d => new DerivedToolResult("Derived", d.Slug, d.FactType, d.Value))
                .ToArray();
            results.Add(JsonSerializer.Serialize(derivedRecords, AgentsJsonContext.Default.DerivedToolResultArray));
        }

        if (evidence.Count > 0)
        {
            // Project to the citation shape the Week 1 attribution scanner recognizes (ResourceType + Id),
            // so [Guideline/<chunkId>] citations in the answer resolve instead of being suppressed.
            var records = evidence
                .Select(e => new EvidenceToolResult("Guideline", e.ChunkId, e.Section, e.Text))
                .ToArray();
            results.Add(JsonSerializer.Serialize(records, AgentsJsonContext.Default.EvidenceToolResultArray));
        }

        return results;
    }

    // Parses the extracted lab facts (the LabPdf schema's "tests" array) into citable facts. Returns empty
    // for non-lab extractions (e.g. intake forms carry no "tests" array), which keep the prior behavior.
    private static List<LabFact> ExtractLabFacts(string? factsJson)
    {
        if (factsJson is null)
        {
            return [];
        }

        var facts = new List<LabFact>();
        try
        {
            using var document = JsonDocument.Parse(factsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tests", out var tests)
                || tests.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var test in tests.EnumerateArray())
            {
                if (test.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = ReadString(test, "test_name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var slug = Slugify(name);
                if (slug.Length == 0)
                {
                    continue;
                }

                facts.Add(new LabFact(
                    slug, name, ReadString(test, "value") ?? "?",
                    ReadString(test, "unit"), ReadString(test, "reference_range"), ReadBool(test, "abnormal_flag")));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return facts;
    }

    // Reduce a test name to the citation-id charset the attribution regex accepts ([A-Za-z0-9-.]); dropping
    // spaces is what lets a multi-word name like "LDL Cholesterol" cite as [Lab/LDLCholesterol].
    private static string Slugify(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.').ToArray());

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    // One extracted patient lab value in citable form (Slug is the whitespace-free [Lab/<slug>] id).
    private sealed record LabFact(
        string Slug, string TestName, string Value, string? Unit, string? ReferenceRange, bool? Abnormal);

    // Projects pre-ingested facts to a citable view. Slug is a short per-fact id (the [Derived/<slug>] token);
    // Value prefers the citation's verbatim quote/value, which already reads as e.g. "Potassium 5.9 (H) mmol/L".
    // A fact with no quote/value is skipped rather than surfaced as an empty, uncitable line.
    private static List<DerivedFactView> ProjectDerivedFacts(IReadOnlyList<DerivedFact> facts)
    {
        var views = new List<DerivedFactView>(facts.Count);
        foreach (var fact in facts)
        {
            var value = fact.Citation.QuoteOrValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var sourceDocRef = fact.Document?.OpenEmrDocumentReferenceId ?? fact.Citation.SourceId;
            views.Add(new DerivedFactView(
                fact.Id.ToString("N")[..8], fact.FactType, value, sourceDocRef, fact.Citation.PageOrSection));
        }

        return views;
    }

    // One pre-ingested document fact in citable form (Slug is the [Derived/<slug>] id; SourceDocRef anchors
    // the citation to the OpenEMR source document).
    private sealed record DerivedFactView(
        string Slug, string FactType, string Value, string SourceDocRef, string? Page);
}
