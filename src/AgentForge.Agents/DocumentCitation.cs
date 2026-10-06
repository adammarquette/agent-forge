using System.Text.Json.Serialization;
using AgentForge.Data.Entities;

namespace AgentForge.Agents;

// This record's XML doc comments are published verbatim as the DocumentCitation schema's description in
// the OpenAPI document (CONVENTIONS.md §13), so keep tracker citations and other notes an
// external reader cannot follow out of them, and put such notes here instead.

/// <summary>
/// A structured click-to-source citation for one document-derived fact (FR-CITE-2): the page and
/// normalized bounding box the fact was read from, so the client can highlight the exact region on the source
/// page, plus the five-field citation metadata every clinical claim carries (FR-CITE-1): source type, source
/// id, page or section, field or chunk id, and quote or value. Surfaced on the evidence answer alongside the
/// prose. <see cref="BoundingBox"/> is null when the extractor reported no region — the overlay degrades to
/// page-level (§7). The five metadata fields are additive; clients that only read the overlay fields are unchanged.
/// </summary>
/// <param name="FactId">Whitespace-free slug matching the answer's <c>[Lab/&lt;slug&gt;]</c> citation token, so a clicked token resolves to its region.</param>
/// <param name="Field">The extracted field / test name (e.g. "INR").</param>
/// <param name="Value">The extracted value, as printed.</param>
/// <param name="Page">1-based source page the fact was read from.</param>
/// <param name="BoundingBox">Normalized <c>[x, y, w, h]</c> region; null when absent (page-level fallback).</param>
/// <param name="Quote">Verbatim supporting text from the document.</param>
/// <param name="SourceDocumentId">OpenEMR <c>DocumentReference</c> id to fetch the source PDF from for the production overlay; null for a document attached in-turn, where the client already holds the bytes it uploaded.</param>
/// <param name="SourceType">Whether the fact came from a FHIR resource (<c>fhir</c>), a derived document (<c>derived</c>), or a guideline (<c>guideline</c>).</param>
/// <param name="SourceId">FHIR resource id, DocumentReference id, or guideline doc id; null for a document attached in-turn, before it has been ingested.</param>
/// <param name="PageOrSection">PDF page number or guideline section, as text.</param>
/// <param name="FieldOrChunkId">Extraction field name or retrieval chunk id.</param>
/// <param name="QuoteOrValue">The value asserted or the snippet quoted.</param>
public sealed record DocumentCitation(
    string FactId,
    string Field,
    string Value,
    int Page,
    double[]? BoundingBox,
    string? Quote,
    string? SourceDocumentId = null,
    [property: JsonConverter(typeof(CitationSourceTypeJsonConverter))] CitationSourceType SourceType = CitationSourceType.Derived,
    string? SourceId = null,
    string? PageOrSection = null,
    string? FieldOrChunkId = null,
    string? QuoteOrValue = null);
