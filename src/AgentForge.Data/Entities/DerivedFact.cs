namespace AgentForge.Data.Entities;

/// <summary>
/// A single fact extracted from an <see cref="IngestedDocument"/>. <b>Sidecar-authoritative</b> — there is no
/// supported FHIR/REST create path for these, so they live here, each citing the OpenEMR source document
/// (ARCHITECTURE-DOCUMENTS.md §4). This is PHI at rest; treat accordingly (§12).
/// <para>
/// Nothing here encrypts. At-rest protection is the deployment environment's under
/// <c>DEPLOYMENT.md</c> §7, and the sidecar's deferral of it is dated and reasoned in
/// <c>ARCHITECTURE-DOCUMENTS.md</c> §12 — read it before adding a column to this type.
/// reference: ARCHITECTURE-DOCUMENTS.md §12 (W2-D21), a separate change
/// </para>
/// </summary>
public sealed class DerivedFact
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Foreign key to the owning <see cref="IngestedDocument"/>.</summary>
    public Guid IngestedDocumentId { get; set; }

    /// <summary>The document this fact was derived from; navigation property.</summary>
    public IngestedDocument? Document { get; set; }

    /// <summary>Fact category (e.g. <c>lab.result</c>, <c>intake.medication</c>).</summary>
    public required string FactType { get; set; }

    /// <summary>The strict-schema fact payload as JSON (stored <c>jsonb</c>); the schema is the contract (NFR-CONTRACT-1).</summary>
    public required string PayloadJson { get; set; }

    /// <summary>Where this fact came from — resolves back to the source (owned value object).</summary>
    public required Citation Citation { get; set; }

    /// <summary>
    /// Grounding/locatability confidence. <b>Despite the name, this is not a model-reported extraction
    /// score</b> — it is what the extractor found when it looked for the citation's quote in the document's
    /// own text, and a reader who assumes otherwise inverts the meaning of <c>0.0</c>. Three values, defined
    /// in <c>ExtractionConfidenceScore</c>:
    /// <list type="bullet">
    /// <item><c>1.0</c> — the quote was located verbatim on the cited page. The only value evidencing grounding.</item>
    /// <item><c>0.5</c> — there was nothing to check against (a scan, an image page, a file that would not
    /// open), so the quote is neither corroborated nor impeached.</item>
    /// <item><c>0.0</c> — the page's text <i>was</i> searched and the quote is not in it, the cited page is
    /// not in the document at all, or the quote does not carry the fact it is cited for (an intake item's or
    /// medication's own text; a lab result's analyte, value and unit). This is the fabricated-fact signal, and it is the floor rather than a missing reading.</item>
    /// </list>
    /// <b>Populated on every fact this build persists</b> — <c>DerivedFactMapper</c> scores each one — but the
    /// column is nullable and the rule is <b>forward-only</b>: it was a two-value rule until <c>a separate change</c>
    /// (read off whether a bounding box was present, which scored a fabricated quote at <c>1.0</c>), and rows
    /// written before that keep their old value with no backfill (<c>a separate change</c>). So a stored value that none
    /// of the three above explains is a legacy row, not a new outcome.
    /// </summary>
    public double? ExtractionConfidence { get; set; }

    /// <summary>When the fact was persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
