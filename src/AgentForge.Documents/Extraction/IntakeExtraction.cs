namespace AgentForge.Documents.Extraction;

/// <summary>
/// Strict extraction schema for a patient intake form (Week 2 Core Req 2). Same gate as
/// <see cref="LabExtraction"/> — the schema is the contract: a payload missing a required field fails
/// deserialization and the extraction is rejected, nothing persisted.
/// </summary>
public sealed record IntakeExtraction
{
    /// <summary>Patient demographics captured on the form.</summary>
    public required IntakeDemographics Demographics { get; init; }

    /// <summary>The chief concern / reason for visit, when stated.</summary>
    public IntakeChiefConcern? ChiefConcern { get; init; }

    /// <summary>Current medications the patient reports.</summary>
    public required IReadOnlyList<IntakeMedication> CurrentMedications { get; init; }

    /// <summary>Reported allergies.</summary>
    public required IReadOnlyList<IntakeAllergy> Allergies { get; init; }

    /// <summary>Reported family history.</summary>
    public required IReadOnlyList<IntakeFamilyHistoryItem> FamilyHistory { get; init; }

    /// <summary>
    /// Where the demographics were read from (required — the grounding gate). It cites the demographics
    /// only: every other fact on the form carries its own citation.
    /// </summary>
    public required ExtractionCitation Citation { get; init; }
}

/// <summary>
/// A free-text intake item with its own citation, so click-to-source lands on the item's own text and page
/// and its grounding is checked on its own quote rather than borrowed from the form-level citation.
/// </summary>
/// <remarks>
/// One sealed type per field rather than one type used three times: the schema exporter turns a repeated
/// type into a relative <c>$ref</c>, and the OpenAPI generator drops a <c>$ref</c> reached through another,
/// publishing the third field's citation as an untyped node.
/// </remarks>
public abstract record IntakeTextItem
{
    /// <summary>The item as written.</summary>
    public required string Text { get; init; }

    /// <summary>Where this item was read from (required — the grounding gate).</summary>
    public required ExtractionCitation Citation { get; init; }
}

/// <summary>The chief concern / reason for visit as written, source-cited.</summary>
public sealed record IntakeChiefConcern : IntakeTextItem;

/// <summary>One reported allergy as written (free text, e.g. the agent and the reaction), source-cited.</summary>
public sealed record IntakeAllergy : IntakeTextItem;

/// <summary>One reported family-history item as written, source-cited.</summary>
public sealed record IntakeFamilyHistoryItem : IntakeTextItem;

/// <summary>Demographic fields from an intake form; individual fields are nullable when not present.</summary>
public sealed record IntakeDemographics
{
    /// <summary>Full name as written.</summary>
    public string? FullName { get; init; }

    /// <summary>Date of birth as written / ISO-8601 when unambiguous.</summary>
    public string? DateOfBirth { get; init; }

    /// <summary>Sex/gender as written.</summary>
    public string? Sex { get; init; }
}

/// <summary>One reported medication with an optional dose, source-cited.</summary>
public sealed record IntakeMedication
{
    /// <summary>Medication name as written.</summary>
    public required string Name { get; init; }

    /// <summary>Dose/frequency as written, when present.</summary>
    public string? Dose { get; init; }

    /// <summary>Where this medication was read from (required — the grounding gate).</summary>
    public required ExtractionCitation Citation { get; init; }
}
