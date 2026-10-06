using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Verification;
using AgentForge.Verification.Rules;

namespace AgentForge.UnitTests.Verification.Rules;

public sealed class AceiArbHyperkalemiaRuleTests
{
    private readonly AceiArbHyperkalemiaRule _sut = new();

    private static MedicationRecord ActiveMed(string display) => new(
        new ClinicalSourceRef("MedicationRequest", "1"), display, null, "active", null);

    private static ObservationRecord PotassiumLab(double value) => new(
        new ClinicalSourceRef("Observation", "1"), "laboratory", "Potassium", value, "mEq/L", 3.5, 5.0, null, "final");

    private static ObservationRecord PotassiumLab(double value, string id, DateTimeOffset effective) => new(
        new ClinicalSourceRef("Observation", id), "laboratory", "Potassium", value, "mEq/L", 3.5, 5.0, effective, "final");

    [Fact]
    public void Evaluate_AceiWithElevatedPotassium_FlagsViolation()
    {
        var input = new DomainConstraintInput([ActiveMed("Lisinopril 10mg")], [PotassiumLab(5.8)], []);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].RuleId.Should().Be("acei-arb-hyperkalemia");
        flags[0].Sources.Should().HaveCount(2);
    }

    [Fact]
    public void Evaluate_ArbWithElevatedPotassium_FlagsViolation()
    {
        var input = new DomainConstraintInput([ActiveMed("Losartan 50mg")], [PotassiumLab(5.6)], []);

        _sut.Evaluate(input).Should().ContainSingle();
    }

    [Fact]
    public void Evaluate_AceiWithNormalPotassium_DoesNotFlag()
    {
        var input = new DomainConstraintInput([ActiveMed("Lisinopril 10mg")], [PotassiumLab(4.2)], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ElevatedPotassiumWithNoAceiOrArb_DoesNotFlag()
    {
        var input = new DomainConstraintInput([ActiveMed("Metoprolol 50mg")], [PotassiumLab(5.8)], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_StaleElevatedPotassiumSupersededByACurrentNormalPotassium_DoesNotFlag()
    {
        // Synthetic: get_labs with no since_date returns the whole chart, and FirstOrDefault over
        // an unordered list picks whichever elevated result happens to appear first - here a 2021
        // AKI admission - while today's normal potassium is never considered.
        // The current value is listed first so arrival order is not what rescues this.
        var current = PotassiumLab(4.1, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var stale = PotassiumLab(5.9, "stale", new DateTimeOffset(2021, 6, 2, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([ActiveMed("Lisinopril 10mg")], [current, stale], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_CurrentPotassiumElevatedAfterAnEarlierNormalOne_FlagsAndDatesTheValue()
    {
        // The over-correction guard: recency must not turn the rule off, and the flag says when
        // the value was drawn (ARCHITECTURE.md §9.3 "data as of").
        var earlier = PotassiumLab(4.1, "earlier", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var current = PotassiumLab(5.8, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([ActiveMed("Lisinopril 10mg")], [earlier, current], []);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("5.8").And.Contain("2026-09-11");
        flags[0].Sources.Should().Contain(s => s.Citation == "Observation/current");
    }
}
