using Microsoft.AspNetCore.Http;

namespace AgentForge.Api.Evidence;

// PublishedFormContractTests is what holds this record and the hand-parsed handler to the same field names.
// Said here rather than in the doc comment below, which is published verbatim as this schema's description
// in the OpenAPI document: which fixture drives the code is true of the code only, and
// CONVENTIONS.md §13 keeps that out of external API copy.
// The omission of a patient field is the same rule seen from the other side - the patient is the session's,
// and a form that offered one is exactly the disclosure a separate change closed, so it must not reappear here as
// documentation either. A separate change

/// <summary>
/// The multipart request contract for <c>POST /evidence/ask</c> — the published shape of the form the
/// handler reads field by field. Description, not binding: the handler parses the form itself so it can
/// answer each missing field with its own 400 - all but <c>context</c>, whose absence is answered 409. The patient is taken from the launched session and is
/// deliberately not a field here; sending one has no effect.
/// </summary>
public sealed record EvidenceAskForm
{
    /// <summary>The clinician's question. Required.</summary>
    public required string Question { get; init; }

    /// <summary>An optional document to extract from in-turn.</summary>
    public IFormFile? File { get; init; }

    /// <summary>Required when a file is attached: <c>lab_pdf</c> or <c>intake_form</c>.</summary>
    public string? DocType { get; init; }

    /// <summary>
    /// Required: the <c>contextKey</c> <c>GET /patient</c> returned when the asking page was rendered. It names
    /// neither the patient nor the session. A key for another patient than the session's current one - the
    /// patient was switched in another tab - is refused with 409, and the page should reload.
    /// </summary>
    public required string Context { get; init; }
}
