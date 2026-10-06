using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Verification;
using AgentForge.Verification.Rules;

namespace AgentForge.UnitTests.Verification.Rules;

public sealed class RenalDoacDosingRuleTests
{
    private readonly RenalDoacDosingRule _sut = new();

    private static MedicationRecord ActiveMed(string display) => new(
        new ClinicalSourceRef("MedicationRequest", "1"), display, null, "active", null);

    private static ObservationRecord CreatinineLab(double value) => new(
        new ClinicalSourceRef("Observation", "1"), "laboratory", "Creatinine", value, "mg/dL", 0.6, 1.2, null, "final");

    private static ObservationRecord CreatinineLab(double value, string id, DateTimeOffset effective) => new(
        new ClinicalSourceRef("Observation", id), "laboratory", "Creatinine", value, "mg/dL", 0.6, 1.2, effective, "final");

    [Fact]
    public void Evaluate_DoacWithElevatedCreatinine_FlagsForRenalDoseReview()
    {
        var input = new DomainConstraintInput([ActiveMed("Apixaban 5mg")], [CreatinineLab(1.8)], []);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].RuleId.Should().Be("renal-doac-dosing");
        flags[0].Sources.Should().HaveCount(2);
    }

    [Fact]
    public void Evaluate_DoacWithNormalCreatinine_DoesNotFlag()
    {
        var input = new DomainConstraintInput([ActiveMed("Apixaban 5mg")], [CreatinineLab(0.9)], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ElevatedCreatinineWithNoDoac_DoesNotFlag()
    {
        var input = new DomainConstraintInput([ActiveMed("Metoprolol 50mg")], [CreatinineLab(1.8)], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_WarfarinNotADoacWithElevatedCreatinine_DoesNotFlag()
    {
        // Warfarin dosing isn't renally cleared the same way DOACs are - the rule must not
        // over-fire on every anticoagulant.
        var input = new DomainConstraintInput([ActiveMed("Warfarin 5mg")], [CreatinineLab(1.8)], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_StaleElevatedCreatinineSupersededByACurrentNormalCreatinine_DoesNotFlag()
    {
        // Same FirstOrDefault-over-an-unordered-list shape as the hyperkalemia rule: a creatinine
        // from a resolved 2021 AKI must not prompt a dose review as though it were today's renal
        // function. Current value first, so order is not the rescue.
        var current = CreatinineLab(0.9, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var stale = CreatinineLab(1.8, "stale", new DateTimeOffset(2021, 6, 2, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([ActiveMed("Apixaban 5mg")], [current, stale], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_CurrentCreatinineElevatedAfterAnEarlierNormalOne_FlagsAndDatesTheValue()
    {
        // The over-correction guard: recency must not turn the rule off, and the flag says when
        // the value was drawn (ARCHITECTURE.md §9.3 "data as of").
        var earlier = CreatinineLab(0.9, "earlier", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var current = CreatinineLab(1.8, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([ActiveMed("Apixaban 5mg")], [earlier, current], []);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("1.8").And.Contain("2026-09-11");
        flags[0].Sources.Should().Contain(s => s.Citation == "Observation/current");
    }
}
