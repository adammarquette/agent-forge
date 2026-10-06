using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Agents.Ingestion;

/// <summary>
/// Orchestrates document ingestion (ARCHITECTURE-DOCUMENTS.md §4): content-hash idempotency → extract → persist the
/// derived facts, each citing the OpenEMR <c>DocumentReference</c> the front desk already uploaded to. The
/// source document is authoritative in OpenEMR (uploaded natively), so the sidecar never writes it — it only
/// derives facts. Runs pre-visit so the clinician's turn just reads ready facts. Degrades deterministically
/// (a schema-gate rejection persists nothing) and cancellation propagates.
/// </summary>
public sealed class DocumentIngestionService : IDocumentIngestionService
{
    private readonly IDocumentExtractor _extractor;
    private readonly IDerivedFactStore _store;
    private readonly IDerivedFactMapper _mapper;
    private readonly IAgentForgeMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DocumentIngestionService> _logger;

    /// <summary>Creates the ingestion orchestrator.</summary>
    public DocumentIngestionService(
        IDocumentExtractor extractor,
        IDerivedFactStore store,
        IDerivedFactMapper mapper,
        IAgentForgeMetrics metrics,
        TimeProvider timeProvider,
        ILogger<DocumentIngestionService> logger)
    {
        _extractor = extractor;
        _store = store;
        _mapper = mapper;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DocumentIngestionResult> IngestAsync(
        DocumentIngestionRequest request, CancellationToken cancellationToken = default)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        var contentHash = ContentHash.Compute(request.Content);

        // 1. Idempotency: the same bytes are never extracted or recorded twice (W2-D3).
        var existing = await _store.FindByContentHashAsync(contentHash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            DocumentIngestionServiceLog.AlreadyIngested(_logger);
            _metrics.RecordDocumentIngestion("already_ingested", _timeProvider.GetElapsedTime(startTimestamp));
            return DocumentIngestionResult.AlreadyIngested(
                contentHash, existing.OpenEmrDocumentReferenceId, existing.DerivedFacts.Count);
        }

        // 2. Extract; a schema-gate rejection persists nothing ("vision without invention").
        // A throw here (provider unreachable, timeout, a rate limit surviving Polly) used to propagate with
        // NO ingestion sample recorded at all - the outage this instrument exists to see was invisible to
        // it. "error" restores that: it is a fourth outcome, not extraction_rejected, because a
        // rejection is the schema gate working and a throw is the extractor not answering. Cancellation is
        // excluded, same line AgentOrchestrator.RunTurnAsync already draws: the caller giving up is not the
        // extractor failing.
        DocumentExtractionResult extraction;
        try
        {
            extraction = await _extractor
                .ExtractAsync(request.DocumentType, request.Content, request.MediaType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DocumentIngestionServiceLog.ExtractionThrew(_logger, ex.GetType());
            _metrics.RecordDocumentIngestion("error", _timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }

        if (!extraction.Succeeded)
        {
            DocumentIngestionServiceLog.ExtractionRejected(_logger);
            _metrics.RecordDocumentIngestion("extraction_rejected", _timeProvider.GetElapsedTime(startTimestamp));
            return DocumentIngestionResult.ExtractionRejected(contentHash, extraction.RejectionReason);
        }

        // 3. Persist the derived facts, each citing the source DocumentReference the front desk uploaded to.
        // CreatedAt/IngestedAt are app-set (no DB default); stamp one timestamp across the document and facts.
        var now = _timeProvider.GetUtcNow();
        var facts = _mapper.Map(extraction, request.DocumentReferenceId);
        foreach (var fact in facts)
        {
            fact.CreatedAt = now;
        }

        var document = new IngestedDocument
        {
            PatientId = request.PatientId,
            DocumentType = request.DocumentType,
            ContentHash = contentHash,
            OpenEmrDocumentReferenceId = request.DocumentReferenceId,
            IngestedAt = now,
            DerivedFacts = [.. facts],
        };
        // The same blind spot exists one step later: a store throw after a successful extraction was
        // equally unrecorded (review). Same outcome, so the responder does not need to
        // tell "the VLM never answered" from "the database write failed" apart from the counter alone -
        // both mean the attempt did not reach "ingested", which is the only fact this instrument is for.
        try
        {
            await _store.AddAsync(document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DocumentIngestionServiceLog.PersistThrew(_logger, ex.GetType());
            _metrics.RecordDocumentIngestion("error", _timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }

        DocumentIngestionServiceLog.Ingested(_logger, facts.Count);
        _metrics.RecordDocumentIngestion("ingested", _timeProvider.GetElapsedTime(startTimestamp));
        RecordExtractionGrounding(request.DocumentType, facts);
        return DocumentIngestionResult.Ingested(contentHash, request.DocumentReferenceId, facts.Count);
    }

    // FR-OBS-W2-2's two extraction signals, emitted only here - on the path that actually persisted facts.
    // Re-posting the same bytes returns AlreadyIngested above and must not re-count a population that is
    // already in the histogram, and a rejected extraction has no facts to score. A separate change
    //
    // Per DOCUMENT for the confidence, per FACT for the outcome: one observation each way is what makes the
    // first a distribution over documents (a 500-fact lab cannot outvote fifty intake forms) and the second a
    // rate with a real denominator.
    //
    // The document's number is the LOCATED FRACTION over its CHECKABLE citations - exact / (exact +
    // unlocatable) - and deliberately not the mean of the per-fact scores. The per-fact score is an ordinal
    // code, not a quantity: 0.5 means "there was nothing to check against", which is a different kind of
    // answer from "half as well grounded", and averaging the three codes puts them on one axis where the
    // order is wrong. A document 40% of whose quotes were searched for and not found would average 0.6 and
    // plot ABOVE a scan nothing could be checked in (0.5), and nine located quotes would dilute one
    // fabricated one to 0.9. The located fraction means one thing from end to end - the share of this
    // document's verifiable citations that were verified - and a document with nothing checkable records no
    // observation at all rather than a number that would sort against those. Such documents stay visible:
    // every one of their facts is counted `unchecked` on the field-outcome counter, and
    // agentforge_document_ingestions_total{outcome="ingested"} minus this histogram's count is how many
    // documents arrived with nothing to check. Separate changes
    private void RecordExtractionGrounding(ClinicalDocumentType documentType, IReadOnlyList<DerivedFact> facts)
    {
        var checkable = 0;
        var located = 0;
        foreach (var fact in facts)
        {
            // null for a score the current rule cannot produce - a earlier row, never backfilled
            // . It cannot arrive here: these facts came back from _mapper.Map eleven lines above and
            // were scored by the current rule moments ago. The guard is defence against a future caller that
            // meters facts read back from the database, where such rows do exist. It has no outcome to
            // report, so it joins neither the rate nor the fraction.
            if (ExtractionConfidenceScore.MatchFor(fact.ExtractionConfidence) is not { } match)
            {
                continue;
            }

            _metrics.RecordExtractionFieldOutcome(DerivedFactType.ToFieldLabel(fact.FactType), match.ToWireName());

            // Exhaustive on purpose: a fourth outcome has to decide here whether it is checkable, rather
            // than falling into the denominator because nobody looked.
            switch (match)
            {
                case CitationQuoteMatch.Exact:
                    checkable++;
                    located++;
                    break;
                case CitationQuoteMatch.Unlocatable:
                    checkable++;
                    break;
                case CitationQuoteMatch.Unchecked:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(facts), match, "Unknown citation quote match.");
            }
        }

        if (checkable > 0)
        {
            _metrics.RecordExtractionConfidence(documentType.ToWireName(), (double)located / checkable);
        }
    }
}
