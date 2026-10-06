namespace AgentForge.Verification.Rules;

/// <summary>
/// Flags an active ACEi/ARB alongside an elevated potassium (ARCHITECTURE.md §9.2) - both raise
/// serum potassium, and the combination with hyperkalemia already present warrants review.
/// Illustrative drug list and threshold, pending clinical validation before pilot (REQUIREMENTS.md FR-VERIF-2).
/// </summary>
public sealed class AceiArbHyperkalemiaRule : IDomainConstraintRule
{
    /// <summary>Illustrative hyperkalemia threshold in mEq/L.</summary>
    private const double HyperkalemiaThreshold = 5.0;

    private static readonly string[] AceiArbDrugs =
    [
        "lisinopril", "enalapril", "ramipril", "benazepril", "captopril",
        "losartan", "valsartan", "irbesartan", "candesartan", "olmesartan",
    ];

    /// <inheritdoc />
    public string RuleId => "acei-arb-hyperkalemia";

    /// <inheritdoc />
    public IReadOnlyList<DomainConstraintFlag> Evaluate(DomainConstraintInput input)
    {
        var aceiArb = input.ActiveMedications
            .Where(m => string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(m => AceiArbDrugs.Any(drug => m.MedicationDisplay.Contains(drug, StringComparison.OrdinalIgnoreCase)));

        if (aceiArb is null)
        {
            return [];
        }

        // CurrentLabs, not Labs: FirstOrDefault over the whole history picks whichever elevated
        // result happens to come first, so a resolved AKI outranks today's value.
        var elevatedPotassium = input.CurrentLabs.FirstOrDefault(
            l => l.CodeDisplay.Contains("potassium", StringComparison.OrdinalIgnoreCase) &&
                l.Value is { } value && value > HyperkalemiaThreshold);

        if (elevatedPotassium is null)
        {
            return [];
        }

        return
        [
            new DomainConstraintFlag(
                RuleId,
                $"{aceiArb.MedicationDisplay} (ACEi/ARB) with most recent potassium " +
                $"{elevatedPotassium.Value:0.0} mEq/L ({ObservationRecency.AsOf(elevatedPotassium)}) - " +
                "both raise serum potassium (illustrative check, pending clinical validation).",
                [aceiArb.Source, elevatedPotassium.Source]),
        ];
    }
}
