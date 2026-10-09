using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Ingestion;

// PublishedFormContractTests is what holds this record and the hand-parsed handler to the same field names.
// Said here rather than in the doc comment below, which is published verbatim as this schema's description
// in the OpenAPI document: which fixture drives the code is true of the code only, and
// CONVENTIONS.md §13 keeps that out of external API copy.

/// <summary>
/// The multipart request contract for <c>POST /documents/ingest</c> — the published shape of the form the
/// handler reads field by field. Description, not binding: the handler parses the form itself so it can
/// answer each missing field with its own 400.
/// </summary>
public sealed record DocumentIngestForm
{
    /// <summary>The document's bytes (PDF or image).</summary>
    public required IFormFile File { get; init; }

    /// <summary>OpenEMR patient id the document belongs to.</summary>
    public required string PatientId { get; init; }

    /// <summary>OpenEMR <c>DocumentReference</c> id, cited back on every derived fact.</summary>
    public required string DocumentReferenceId { get; init; }

    /// <summary>Which strict extraction schema to apply: <c>lab_pdf</c> or <c>intake_form</c>.</summary>
    public required string DocType { get; init; }
}
