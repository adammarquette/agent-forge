using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentForge.Data.Entities;
using AgentForge.Documents.Extraction;
using AgentForge.Llm;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Documents;

/// <summary>
/// Default <see cref="IDocumentExtractor"/>: sends the document to the vision model behind
/// <see cref="ILlmProvider"/>, then validates the model's output against the strict schema. Output that
/// fails the schema is rejected at the boundary and nothing is persisted (ARCHITECTURE-DOCUMENTS.md §3, W2-D6).
/// </summary>
public sealed class DocumentExtractor : IDocumentExtractor
{
    private const string PdfMediaType = "application/pdf";

    private readonly ILlmProvider _llm;
    private readonly IPdfWordReader _pdfReader;
    private readonly IAgentForgeMetrics _metrics;
    private readonly ILogger<DocumentExtractor> _logger;

    /// <summary>Creates the extractor.</summary>
    /// <param name="llm">The model provider (must support image/document content).</param>
    /// <param name="pdfReader">Reads PDF word geometry to make citation boxes exact.</param>
    /// <param name="metrics">Token/cost accounting for the extraction call (FR-OBS-2).</param>
    /// <param name="logger">Logger (PHI-free).</param>
    public DocumentExtractor(
        ILlmProvider llm,
        IPdfWordReader pdfReader,
        IAgentForgeMetrics metrics,
        ILogger<DocumentExtractor> logger)
    {
        _llm = llm;
        _pdfReader = pdfReader;
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DocumentExtractionResult> ExtractAsync(
        ClinicalDocumentType documentType,
        ReadOnlyMemory<byte> content,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var base64 = Convert.ToBase64String(content.Span);
        LlmContent documentBlock = mediaType.Equals(PdfMediaType, StringComparison.OrdinalIgnoreCase)
            ? new LlmDocumentContent(mediaType, base64)
            : new LlmImageContent(mediaType, base64);

        var request = new LlmRequest(
            SystemPrompt: ExtractionPrompts.SystemFor(documentType),
            Messages: [new LlmMessage(LlmRole.User, [documentBlock, new LlmTextContent(ExtractionPrompts.UserInstruction)])],
            Tools: null,
            MaxOutputTokens: 8192);

        var response = await CompleteTracedAsync(documentType, request, cancellationToken);

        // Meter the extractor's multimodal call the way the chat and evidence turns meter theirs. Recorded
        // before the schema gate on purpose: the provider has already charged for this call, so a rejected
        // extraction costs the same as an accepted one and must not vanish from the cost counter.
        _metrics.RecordLlmUsage(response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.EstimatedCostUsd);

        var json = ExtractJsonObject(response.Content);
        if (json is null)
        {
            DocumentExtractorLog.NoJsonPayload(_logger, documentType);
            return DocumentExtractionResult.Rejected(documentType, "Model returned no JSON object.", response.Usage);
        }

        // The document's own text layer: glyph rectangles, and the page count a cited page is
        // range-checked against. Two jobs: exact citation geometry for the click-to-source overlay, and the
        // only deterministic check that a quote was copied rather than composed. None for
        // non-PDF or scanned/unreadable input, which is a third outcome - "not checked" - and not "not found".
        var text = mediaType.Equals(PdfMediaType, StringComparison.OrdinalIgnoreCase)
            ? _pdfReader.ReadTextLayer(content)
            : PdfTextLayer.None;

        var matches = new List<CitationQuoteMatch>();
        try
        {
            // Deserialize against the strict, source-generated schema. Missing required members throw
            // (the schema-is-the-gate mechanism); locate every quote, then re-serialize the canonical payload.
            var canonicalJson = documentType switch
            {
                ClinicalDocumentType.LabPdf =>
                    Serialize(ResolveLabBoxes(Deserialize(json, DocumentExtractionJsonContext.Default.LabExtraction), text, matches),
                        DocumentExtractionJsonContext.Default.LabExtraction),
                ClinicalDocumentType.IntakeForm =>
                    Serialize(ResolveIntakeBoxes(Deserialize(json, DocumentExtractionJsonContext.Default.IntakeExtraction), text, matches),
                        DocumentExtractionJsonContext.Default.IntakeExtraction),
                _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "Unknown document type."),
            };

            RecordQuoteMatches(documentType, matches);
            return DocumentExtractionResult.Ok(documentType, canonicalJson, response.Usage);
        }
        catch (JsonException ex)
        {
            DocumentExtractorLog.SchemaValidationFailed(_logger, documentType, ex);
            return DocumentExtractionResult.Rejected(
                documentType, $"Extracted JSON failed schema validation: {ex.Message}", response.Usage);
        }
    }

    // The VLM call as its own span, a child of the caller's (the intake-extractor worker span in the graph).
    // Type, outcome and token counts only - never the document, the prompt or the reply (ARCHITECTURE-DOCUMENTS.md §12).
    private async Task<LlmResponse> CompleteTracedAsync(
        ClinicalDocumentType documentType, LlmRequest request, CancellationToken cancellationToken)
    {
        using var span = AgentForgeActivitySource.Instance.StartActivity(EvidenceTracing.ExtractionVlmSpan);
        span?.SetTag(EvidenceTracing.DocumentType, documentType.ToWireName());
        try
        {
            var response = await _llm.CompleteAsync(request, cancellationToken);
            span?.SetTag(EvidenceTracing.Outcome, "responded");
            span?.SetTag(EvidenceTracing.InputTokens, response.Usage.InputTokens);
            span?.SetTag(EvidenceTracing.OutputTokens, response.Usage.OutputTokens);
            return response;
        }
        catch (Exception ex)
        {
            EvidenceTracing.RecordFailure(span, ex);
            throw;
        }
    }

    private static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new JsonException("Payload deserialized to null.");

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);

    // No short-circuit on empty geometry: every citation is stamped on every path, because an unstamped
    // citation keeps whatever `match` the MODEL sent, and the one thing this field must never be is a model
    // claim. Empty geometry is an outcome (Unchecked), not a reason to skip.
    private static LabExtraction ResolveLabBoxes(
        LabExtraction extraction, PdfTextLayer text, List<CitationQuoteMatch> matches) =>
        extraction with
        {
            Tests =
            [
                .. extraction.Tests.Select(t => t with
                {
                    Citation = ResolveCitation(
                        t.Citation, text, matches,
                        FactQuoteSupport.IsLabResultSupported(t.TestName, t.Value, t.Unit, t.Citation.Quote)),
                }),
            ],
        };

    private static IntakeExtraction ResolveIntakeBoxes(
        IntakeExtraction extraction, PdfTextLayer text, List<CitationQuoteMatch> matches) =>
        extraction with
        {
            ChiefConcern = extraction.ChiefConcern is { } concern
                ? concern with { Citation = ResolveCitation(concern.Citation, text, matches, Carries(concern.Citation, concern.Text)) }
                : null,
            CurrentMedications =
            [
                // A blank dose is an absent one; a blank name still fails, because it claims a drug.
                .. extraction.CurrentMedications.Select(m => m with
                {
                    Citation = ResolveCitation(
                        m.Citation, text, matches, Carries(m.Citation, m.Name, string.IsNullOrWhiteSpace(m.Dose) ? null : m.Dose)),
                }),
            ],
            Allergies =
                [.. extraction.Allergies.Select(a => a with { Citation = ResolveCitation(a.Citation, text, matches, Carries(a.Citation, a.Text)) })],
            FamilyHistory =
                [.. extraction.FamilyHistory.Select(f => f with { Citation = ResolveCitation(f.Citation, text, matches, Carries(f.Citation, f.Text)) })],

            // The form-level citation cites the demographics block, which claims no text of its own to check.
            Citation = ResolveCitation(extraction.Citation, text, matches, supported: true),
        };

    // Whether an intake item's quote carries each of its claimed texts. A null entry is an optional field
    // left absent, and claims nothing.
    private static bool Carries(ExtractionCitation citation, params string?[] claimed) =>
        claimed.All(c => c is null || FactQuoteSupport.IsSupported(c, citation.Quote));

    /// <summary>
    /// Stamps one citation with what the resolver found and reconciles its bounding box with that finding.
    /// This is the VERBATIM rule's deterministic backstop: the prompt asks the model to
    /// copy its quotes, and this is the only thing that can tell whether it did. <paramref name="supported"/>
    /// is whether the quote carries the fact it is cited for (<see cref="FactQuoteSupport"/>); a quote that
    /// does not is unlocatable wherever it is printed.
    /// </summary>
    private static ExtractionCitation ResolveCitation(
        ExtractionCitation citation, PdfTextLayer text, List<CitationQuoteMatch> matches, bool supported)
    {
        var located = CitationBoundingBoxResolver.Resolve(text, citation.Page, citation.Quote);

        // Checked on scans too: the mismatch is between two strings the model sent, so no text layer is
        // needed to see it, and "unchecked" would pass an invented fact off as merely unread.
        var match = supported ? located.Match : CitationQuoteMatch.Unlocatable;
        matches.Add(match);

        return match switch
        {
            // Located: the union of the quote's own glyphs replaces whatever the model estimated.
            CitationQuoteMatch.Exact => citation with { Match = match, BoundingBox = located.BoundingBox },

            // Searched and not there, or there but saying something else. A box asserts "this fact is HERE",
            // and that assertion is false - drop it so click-to-source opens at page level instead of
            // highlighting text that says something else. The fact still ships, marked; see PROMPTS.md
            // section 7 for why it is not suppressed.
            CitationQuoteMatch.Unlocatable => citation with { Match = match, BoundingBox = null },

            // Nothing to check against, so the model's estimate is the only overlay available and is kept.
            CitationQuoteMatch.Unchecked => citation with { Match = match },

            _ => throw new ArgumentOutOfRangeException(
                nameof(citation), match, "Unknown citation quote match."),
        };
    }

    // Counted, so a fabricating model or a regressed matcher shows up as a trend rather than as one
    // clinician noticing a highlight in the wrong place; logged with counts only, because the quote itself
    // is free text off a clinical document (CONVENTIONS.md section 7).
    private void RecordQuoteMatches(ClinicalDocumentType documentType, List<CitationQuoteMatch> matches)
    {
        var unlocatable = 0;
        foreach (var match in matches)
        {
            _metrics.RecordCitationQuoteMatch(match.ToWireName());
            if (match == CitationQuoteMatch.Unlocatable)
            {
                unlocatable++;
            }
        }

        if (unlocatable > 0)
        {
            DocumentExtractorLog.UnlocatableQuotes(_logger, unlocatable, matches.Count, documentType);
        }
    }

    /// <summary>Isolates the outermost JSON object from model text, tolerating stray prose or code fences.</summary>
    private static string? ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : null;
    }
}
