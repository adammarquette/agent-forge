using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Verification;
using AgentForge.Verification.Rules;

namespace AgentForge.UnitTests.Verification.Rules;

public sealed class InrTherapeuticRangeRuleTests
{
    private readonly InrTherapeuticRangeRule _sut = new();

    private static ObservationRecord InrLab(double value) => new(
        new ClinicalSourceRef("Observation", "1"), "laboratory", "INR", value, null, 0.8, 1.2, null, "final");

    private static ObservationRecord InrLab(double value, string id, DateTimeOffset effective) => new(
        new ClinicalSourceRef("Observation", id), "laboratory", "INR", value, null, 0.8, 1.2, effective, "final");

    private static ConditionRecord Problem(string display) => new(
        new ClinicalSourceRef("Condition", "1"), display, "active", null);

    [Fact]
    public void Evaluate_InrBelowAfibRangeWithAfibOnFile_FlagsViolation()
    {
        var input = new DomainConstraintInput([], [InrLab(1.5)], [Problem("Atrial fibrillation")]);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].RuleId.Should().Be("inr-therapeutic-range");
        flags[0].Sources.Should().ContainSingle().Which.Citation.Should().Be("Observation/1");
    }

    [Fact]
    public void Evaluate_InrWithinAfibRange_DoesNotFlag()
    {
        var input = new DomainConstraintInput([], [InrLab(2.5)], [Problem("Atrial fibrillation")]);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_InrBelowMechanicalValveRangeWithValveOnFile_FlagsUsingTheHigherValveRange()
    {
        // 2.2 is within the AFib range (2.0-3.0) but below the mechanical-valve range
        // (2.5-3.5) - proves indication detection actually switches which range applies,
        // not just a single hardcoded band.
        var input = new DomainConstraintInput([], [InrLab(2.2)], [Problem("Mechanical mitral valve")]);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("mechanical valve");
    }

    [Fact]
    public void Evaluate_InrWithinMechanicalValveRangeWithValveOnFile_DoesNotFlag()
    {
        var input = new DomainConstraintInput([], [InrLab(3.2)], [Problem("Mechanical aortic valve")]);

        // Sanity check paired with the test above: 3.2 is in-range for a valve (2.5-3.5).
        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NoInrLabPresent_ReturnsNoFlags()
    {
        var potassium = new ObservationRecord(new ClinicalSourceRef("Observation", "2"), "laboratory", "Potassium", 4.0, "mEq/L", 3.5, 5.0, null, "final");
        var input = new DomainConstraintInput([], [potassium], []);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_InrOutOfRangeWithNoIndicationOnFile_FlagsUsingTheDefaultAfibRange()
    {
        // Illustrative default (documented on the rule): AFib is the most common anticoagulation
        // indication for this product's user (REQUIREMENTS.md), used when no indication is on file -
        // not a substitute for a clinician confirming the actual indication.
        var input = new DomainConstraintInput([], [InrLab(4.0)], []);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("atrial fibrillation");
    }

    [Fact]
    public void Evaluate_StaleOutOfRangeInrSupersededByACurrentInRangeInr_DoesNotFlag()
    {
        // Synthetic: get_labs with no since_date returns the whole chart, so the rule sees years
        // of INRs rather than the patient's current state. A subtherapeutic
        // result from 2019 must not be stated as this patient's anticoagulation today when a
        // therapeutic result from last week is on file. Current value listed first, because
        // arrival order is not recency and must not be what rescues this.
        var current = InrLab(2.4, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var stale = InrLab(1.6, "stale", new DateTimeOffset(2019, 4, 2, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([], [current, stale], [Problem("Atrial fibrillation")]);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_MultipleStaleOutOfRangeInrs_FlagsNoneOfThem()
    {
        // The shape the brief actually produces: one flag per historical excursion, each cited to
        // a real resolving id so nothing downstream suppresses them.
        var current = InrLab(2.4, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        List<ObservationRecord> history =
        [
            InrLab(1.6, "stale-1", new DateTimeOffset(2019, 4, 2, 9, 0, 0, TimeSpan.Zero)),
            InrLab(3.9, "stale-2", new DateTimeOffset(2021, 8, 14, 9, 0, 0, TimeSpan.Zero)),
            InrLab(1.2, "stale-3", new DateTimeOffset(2024, 1, 30, 9, 0, 0, TimeSpan.Zero)),
        ];
        var input = new DomainConstraintInput([], [.. history, current], [Problem("Atrial fibrillation")]);

        _sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_CurrentInrOutOfRangeAfterAnEarlierInRangeInr_FlagsOnceAndDatesTheValue()
    {
        // The over-correction guard: recency must not turn the rule off. The most recent result
        // is the one that describes the patient, and the flag says when it was drawn
        // (ARCHITECTURE.md §9.3 "data as of").
        var earlier = InrLab(2.4, "earlier", new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));
        var current = InrLab(4.6, "current", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([], [earlier, current], [Problem("Atrial fibrillation")]);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("4.6").And.Contain("2026-09-11");
        flags[0].Sources.Should().ContainSingle().Which.Citation.Should().Be("Observation/current");
    }

    [Fact]
    public void Evaluate_UndatedInrIsTheOnlyOneOnFile_StillFlagsAndSaysTheDateIsUnknown()
    {
        // An Observation with no effectiveDateTime cannot be shown to be superseded, and dropping
        // it would trade a false flag for a missed one. It is flagged, and the stamp is honest.
        var input = new DomainConstraintInput([], [InrLab(4.0)], [Problem("Atrial fibrillation")]);

        var flags = _sut.Evaluate(input);

        flags.Should().ContainSingle();
        flags[0].Description.Should().Contain("date unknown");
    }

    [Fact]
    public void Evaluate_UndatedInrAlongsideADatedOne_UsesTheDatedOne()
    {
        // A dated result is the only one whose recency is knowable, so it wins over an undated
        // one rather than the list's arbitrary order deciding.
        var undated = InrLab(1.6);
        var dated = InrLab(2.4, "dated", new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));
        var input = new DomainConstraintInput([], [undated, dated], [Problem("Atrial fibrillation")]);

        _sut.Evaluate(input).Should().BeEmpty();
    }
}
