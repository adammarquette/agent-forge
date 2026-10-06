using System.Collections.Frozen;

namespace AgentForge.Agents.Ingestion;

/// <summary>
/// Every <c>DerivedFact.FactType</c> this sidecar produces, as constants, plus the closed set of values
/// admissible on the <c>field</c> label of <c>agentforge.extraction_field_outcomes</c>.
/// </summary>
/// <remarks>
/// <b>The set is code-owned, and that is the whole point.</b> The obvious "field" for a lab result is the
/// test name — but that is model output, and a label whose values come from model output mints one permanent
/// series per string the model happens to emit, and puts document text on an exported dimension while it is
/// at it (the same reasoning as <c>RecordOutOfScopeToolCall</c>'s deliberate untagging). So the label
/// is the fact's <i>kind</i>, every value of which is a literal in this file, and
/// <see cref="ToFieldLabel"/> collapses anything else into one <see cref="Other"/> bucket rather than
/// admitting it. A separate change
/// </remarks>
public static class DerivedFactType
{
    /// <summary>One result row from a laboratory-results document.</summary>
    public const string LabResult = "lab.result";

    /// <summary>The demographics block of an intake form.</summary>
    public const string IntakeDemographics = "intake.demographics";

    /// <summary>The intake form's free-text chief concern.</summary>
    public const string IntakeChiefConcern = "intake.chief_concern";

    /// <summary>One current medication listed on an intake form.</summary>
    public const string IntakeMedication = "intake.medication";

    /// <summary>One allergy listed on an intake form.</summary>
    public const string IntakeAllergy = "intake.allergy";

    /// <summary>One family-history item listed on an intake form.</summary>
    public const string IntakeFamilyHistory = "intake.family_history";

    /// <summary>
    /// The one bucket everything unrecognised shares. A value reaching this is a defect — a new fact type
    /// that was not registered in <see cref="All"/> — but it is a bounded defect: the series count cannot
    /// grow past this set however wrong the caller is.
    /// </summary>
    public const string Other = "other";

    /// <summary>Every fact type the mapper emits.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        LabResult,
        IntakeDemographics,
        IntakeChiefConcern,
        IntakeMedication,
        IntakeAllergy,
        IntakeFamilyHistory,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every value that can appear on the <c>field</c> metric label — <see cref="All"/> plus <see cref="Other"/>.</summary>
    public static readonly FrozenSet<string> Labels = All.Append(Other).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The metric label for a fact type: itself when it is one this repository owns, <see cref="Other"/>
    /// otherwise. Never returns its argument unchecked, so no caller can widen the label set by accident.
    /// </summary>
    public static string ToFieldLabel(string? factType) =>
        factType is not null && All.Contains(factType) ? factType : Other;
}
