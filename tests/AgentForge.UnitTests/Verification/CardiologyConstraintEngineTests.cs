using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Mcp;
using AgentForge.Verification;

namespace AgentForge.UnitTests.Verification;

public sealed class CardiologyConstraintEngineTests
{
    private static readonly JsonSerializerOptions SerializeOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Evaluate_MultipleRulesEachFlag_ReturnsFlagsFromEveryRule()
    {
        var ruleA = A.Fake<IDomainConstraintRule>();
        var ruleB = A.Fake<IDomainConstraintRule>();
        var flagA = new DomainConstraintFlag("rule-a", "flag from a", [new ClinicalSourceRef("Observation", "1")]);
        var flagB = new DomainConstraintFlag("rule-b", "flag from b", [new ClinicalSourceRef("MedicationRequest", "2")]);
        var input = DomainConstraintInput.Empty;
        A.CallTo(() => ruleA.Evaluate(input)).Returns([flagA]);
        A.CallTo(() => ruleB.Evaluate(input)).Returns([flagB]);
        var sut = new CardiologyConstraintEngine([ruleA, ruleB]);

        var flags = sut.Evaluate(input);

        flags.Should().BeEquivalentTo([flagA, flagB]);
    }

    [Fact]
    public void Evaluate_NoRuleFlags_ReturnsEmpty()
    {
        var rule = A.Fake<IDomainConstraintRule>();
        var input = DomainConstraintInput.Empty;
        A.CallTo(() => rule.Evaluate(input)).Returns([]);
        var sut = new CardiologyConstraintEngine([rule]);

        sut.Evaluate(input).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_OneRuleThrows_TheOtherRulesStillRunAndTheThrowingRuleIsSkipped()
    {
        // One rule's bug (e.g. a null-ref on unexpected data) must degrade one check, not take
        // down verification for the whole response (NFR-REL-1's "one failure degrades one
        // section" applied to the rule engine itself).
        var badRule = A.Fake<IDomainConstraintRule>();
        var goodRule = A.Fake<IDomainConstraintRule>();
        var flag = new DomainConstraintFlag("good-rule", "flag", []);
        var input = DomainConstraintInput.Empty;
        A.CallTo(() => badRule.Evaluate(input)).Throws<InvalidOperationException>();
        A.CallTo(() => goodRule.Evaluate(input)).Returns([flag]);
        var sut = new CardiologyConstraintEngine([badRule, goodRule]);

        var flags = sut.Evaluate(input);

        flags.Should().ContainSingle().Which.Should().Be(flag);
    }

    // Regression, and the clinical symptom the scanner fix exists to
    // prevent: the brief now calls get_labs AND get_interval_changes, which issue the identical
    // FHIR query, so one INR can arrive twice in a turn. InrTherapeuticRangeRule emits one flag per
    // matching lab and ChatSessionCoordinator maps flags to the wire without Distinct, so a
    // duplicated lab reaches the clinician as the SAME safety flag printed twice. The prompt's
    // de-duplication rule cannot prevent this - flags are computed from tool JSON and never pass
    // through the model - so the guarantee has to live below it, in the scanner.
    [Fact]
    public void Evaluate_SameOutOfRangeInrReturnedByTwoTools_FlagsItOnce()
    {
        var inr = new ObservationRecord(
            new ClinicalSourceRef("Observation", "inr-1"), "laboratory", "INR", 3.8, null, 2.0, 3.0, null, "final");
        var afib = new ConditionRecord(new ClinicalSourceRef("Condition", "1"), "Atrial fibrillation", "active", null);
        var fromGetLabs = JsonSerializer.Serialize(new LabsResult([inr]), SerializeOptions);
        var fromIntervalChanges = JsonSerializer.Serialize(new IntervalChangesResult([], [inr], []), SerializeOptions);
        var fromSummary = JsonSerializer.Serialize(new PatientSummaryResult(null, [afib], [], []), SerializeOptions);
        var sut = new CardiologyConstraintEngine(CardiologyConstraintRules.Default);

        var scan = ToolResultJsonScanner.Scan([fromSummary, fromGetLabs, fromIntervalChanges]);
        var flags = sut.Evaluate(scan.Input);

        flags.Should().ContainSingle("one out-of-range INR is one finding, however many tools returned it");
    }
}
