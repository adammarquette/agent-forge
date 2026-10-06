using AgentForge.Evals;
using AgentForge.Verification;
using FluentAssertions;

namespace AgentForge.EvalTests;

/// <summary>
/// The answer-path tier's own guards — the tests that grade the grader. Everything here is about the
/// instrument rather than about any one golden case: that a case cannot land in a metric's denominator
/// unchecked, that a rubric cannot be claimed over an empty expectation, and that each population is
/// two-sided so a degenerate implementation cannot sweep it.
/// <para>
/// `M1` (groundedness), `M2` (constraint recall) and `M5` (transparent degradation) are stated over the
/// Week 1 <b>answer</b> path, which had no eval population at all until this suite
/// (<c>METRICS.md</c> §2). Separate changes
/// </para>
/// </summary>
public sealed class AnswerPathRubricTests
{
    private static readonly CaseOutcome ShippedOutcome = new(true, "Her INR is 4.6 [Observation/syn-a-obs-1].", null, []);

    /// <summary>A scenario every field of which is populated, so only the guard under test can throw.</summary>
    private static AnswerScenario ScorableScenario(string metric) => new()
    {
        Metric = metric,
        Intent = "synthetic scenario for the guard under test",
        Site = "default",
        PatientId = "syn-patient-alpha",
        ModelScript = [new ModelTurnFixture { Text = "Her INR is 4.6 [Observation/syn-a-obs-1]." }],
        ExpectedSuppressed = ["syn-suppressed-fragment"],
        ExpectedFlagRuleIds = ["inr-therapeutic-range"],
    };

    private static GoldenCase AnswerCase(string metric, IReadOnlyList<string> rubrics, AnswerScenario? scenario = null) => new()
    {
        Id = $"answer-guard-{metric.ToLowerInvariant()}",
        Guards = $"A synthetic {metric} case standing in for one whose rubrics do not cover the metric it declares.",
        Category = RubricEvaluator.AnswerCategory,
        ExpectSuccess = true,
        ExpectedValues = ["Her INR is 4.6"],
        Rubrics = rubrics,
        Answer = scenario ?? ScorableScenario(metric),
    };

    private static IReadOnlyList<GoldenCase> AnswerCases() =>
        [.. GoldenSet.CaseFileNames().Select(GoldenSet.Load).Where(c => c.Category == RubricEvaluator.AnswerCategory)];

    // FluentAssertions' Contain takes an expression tree, which may not carry `?.` - so the null-tolerant
    // count lives in a method the tree can call instead.
    private static int Size(IReadOnlyList<string>? items) => items?.Count ?? 0;

    /// <summary>
    /// Given an answer case that does not declare the rubric its own metric is scored by, when it is
    /// scored, then scoring fails loudly and names the rubric. Same defect as `M3`'s
    /// (<c>a separate change</c>): the metric's denominator is every case carrying that metric, but its numerator
    /// only counts cases that declare the rubric, so an undeclared case is reported as inspected and found
    /// clean. A separate change
    /// </summary>
    [Theory]
    [InlineData("M1", "grounded_answer")]
    [InlineData("M2", "constraint_flagged")]
    [InlineData("M5", "transparent_degradation")]
    public void Evaluate_WhenAnswerCaseOmitsItsMetricRubric_Throws(string metric, string missing)
    {
        var undercheckedCase = AnswerCase(metric, ["no_phi_in_logs"]);

        var score = () => RubricEvaluator.Evaluate(undercheckedCase, ShippedOutcome);

        score.Should().Throw<InvalidOperationException>(
                $"an answer case carrying {metric} still lands in that metric's denominator")
            .WithMessage($"*{undercheckedCase.Id}*")
            .WithMessage($"*{missing}*");
    }

    /// <summary>
    /// Given an `M1` case that pins neither a suppression nor a shipped fragment, when it is scored, then
    /// scoring throws — a groundedness check with nothing to check is the failure the rubric exists to
    /// prevent, exactly as a disclosure check over an empty forbidden set is for `M3`.
    /// </summary>
    [Fact]
    public void Evaluate_WhenGroundedAnswerClaimedWithNothingToCheck_Throws()
    {
        var emptyCase = AnswerCase("M1", ["grounded_answer"], ScorableScenario("M1") with { ExpectedSuppressed = null })
            with
        { ExpectedValues = null };

        var score = () => RubricEvaluator.Evaluate(emptyCase, ShippedOutcome);

        score.Should().Throw<InvalidOperationException>().WithMessage("*grounded_answer*");
    }

    /// <summary>
    /// Given an `M2` case that seeds no violation and does not declare itself a near-miss control, when it
    /// is scored, then scoring throws. A recall rate whose population includes cases with nothing seeded is
    /// the number-that-means-nothing set the target to avoid.
    /// </summary>
    [Fact]
    public void Evaluate_WhenConstraintFlaggedClaimedWithNothingSeeded_Throws()
    {
        var emptyCase = AnswerCase(
            "M2", ["constraint_flagged"], ScorableScenario("M2") with { ExpectedFlagRuleIds = [] });

        var score = () => RubricEvaluator.Evaluate(emptyCase, ShippedOutcome with { ConstraintRuleIds = [] });

        score.Should().Throw<InvalidOperationException>().WithMessage("*constraint_flagged*");
    }

    /// <summary>
    /// Given an `M5` case that names no fragment the shipped answer must contain, when it is scored, then
    /// scoring throws — "the answer stated the gap" is unfalsifiable without one.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTransparentDegradationClaimedWithNoExpectedValues_Throws()
    {
        var emptyCase = AnswerCase("M5", ["transparent_degradation"]) with { ExpectedValues = null };

        var score = () => RubricEvaluator.Evaluate(emptyCase, ShippedOutcome);

        score.Should().Throw<InvalidOperationException>().WithMessage("*transparent_degradation*");
    }

    /// <summary>
    /// Given a claim the case says must be suppressed, when it is still present in the answer that shipped,
    /// then `grounded_answer` fails. This is `M1`'s whole content: the suppressed-claims list recording a
    /// line is not the same as that line being absent from what reached the clinician.
    /// </summary>
    [Fact]
    public void Evaluate_WhenASuppressedClaimStillAppearsInTheShippedAnswer_FailsGroundedAnswer()
    {
        var testCase = AnswerCase(
            "M1", ["grounded_answer"], ScorableScenario("M1") with { ExpectedSuppressed = ["creatinine has risen"] });
        var leaked = new CaseOutcome(
            true,
            "Her INR is 4.6 [Observation/syn-a-obs-1].\nHer creatinine has risen to 2.1 mg/dL.",
            null,
            [],
            SuppressedLines: ["Her creatinine has risen to 2.1 mg/dL."]);

        var scores = RubricEvaluator.Evaluate(testCase, leaked);

        scores["grounded_answer"].Should().BeFalse(
            "a claim recorded as suppressed but still present in the shipped answer is an ungrounded claim that shipped");
    }

    /// <summary>
    /// Given an `M1` case whose grounded line must ship verbatim, when the verifier suppressed it anyway,
    /// then `grounded_answer` fails. Without this half a verifier that suppressed <em>everything</em> would
    /// score 100% groundedness over the whole population — `M3`'s deny-everything defect, one metric over.
    /// </summary>
    [Fact]
    public void Evaluate_WhenAGroundedLineWasSuppressedToo_FailsGroundedAnswer()
    {
        var testCase = AnswerCase("M1", ["grounded_answer"]);

        var scores = RubricEvaluator.Evaluate(testCase, ShippedOutcome with { ResultJson = string.Empty });

        scores["grounded_answer"].Should().BeFalse(
            "a gate that suppresses the grounded line as well is not groundedness, it is silence");
    }

    /// <summary>
    /// Given a seeded constraint violation, when the engine raised no flag for it, then
    /// `constraint_flagged` fails — `M2` is recall, and a missed seeded violation is what it counts.
    /// </summary>
    [Fact]
    public void Evaluate_WhenASeededViolationRaisedNoFlag_FailsConstraintFlagged()
    {
        var testCase = AnswerCase("M2", ["constraint_flagged"]);

        var scores = RubricEvaluator.Evaluate(testCase, ShippedOutcome with { ConstraintRuleIds = [] });

        scores["constraint_flagged"].Should().BeFalse("the seeded inr-therapeutic-range violation was not flagged");
    }

    /// <summary>
    /// Given a near-miss control — a chart deliberately just outside every rule — when any flag is raised,
    /// then `constraint_flagged` fails. This is the specificity half: without it an engine that flagged
    /// every chart would score 100% recall, which is a number that means nothing.
    /// </summary>
    [Fact]
    public void Evaluate_WhenANearMissControlRaisedAFlag_FailsConstraintFlagged()
    {
        var control = AnswerCase(
            "M2",
            ["constraint_flagged"],
            ScorableScenario("M2") with { ExpectedFlagRuleIds = [], ExpectNoConstraintFlags = true });

        var scores = RubricEvaluator.Evaluate(
            control, ShippedOutcome with { ConstraintRuleIds = ["inr-therapeutic-range"] });

        scores["constraint_flagged"].Should().BeFalse(
            "a control that trips a rule is not a control - recall measured without specificity is unfalsifiable");
    }

    /// <summary>
    /// Given the committed golden set, when the seeded violations are collected, then every rule in
    /// <see cref="CardiologyConstraintRules.Default"/> has at least one. The ruling is that no
    /// constraint class may be missed, so 100% recall over a population that skips a class would restate
    /// the gap rather than close it.
    /// </summary>
    [Fact]
    public void SeededConstraintViolations_OverTheGoldenSet_CoverEveryRuleInTheDefaultSet()
    {
        var seeded = AnswerCases()
            .SelectMany(c => c.Answer?.ExpectedFlagRuleIds ?? [])
            .ToHashSet(StringComparer.Ordinal);

        var unseeded = CardiologyConstraintRules.Default
            .Select(r => r.RuleId)
            .Where(id => !seeded.Contains(id))
            .ToArray();

        unseeded.Should().BeEmpty(
            "M2's target is 100% recall and no constraint class may be missed, so every rule in the default "
            + "set needs a seeded violation of its own");
    }

    /// <summary>
    /// Given the committed golden set, when `M2`'s population is counted, then it holds at least one
    /// near-miss control — the analogue of `M3`'s permit cases.
    /// </summary>
    [Fact]
    public void ConstraintPopulation_WhenCounted_HoldsAtLeastOneNearMissControl()
    {
        var constraintCases = AnswerCases().Where(c => c.Answer?.Metric == "M2").ToArray();

        constraintCases.Should().NotBeEmpty("M2 reads a recall rate, which over an empty population is arithmetic");
        constraintCases.Should().Contain(
            c => c.Answer!.ExpectNoConstraintFlags,
            "an engine that flagged every chart would score 100% recall without a control that must not flag");
        constraintCases.Should().Contain(
            c => Size(c.Answer!.ExpectedFlagRuleIds) > 0, "recall needs seeded violations to recall");
    }

    /// <summary>
    /// Given the committed golden set, when `M1`'s population is counted, then it holds both a case whose
    /// claim must be suppressed and a case whose answer must ship intact.
    /// </summary>
    [Fact]
    public void GroundednessPopulation_WhenCounted_HoldsBothSuppressionAndShippedCases()
    {
        var groundedness = AnswerCases().Where(c => c.Answer?.Metric == "M1").ToArray();

        groundedness.Should().NotBeEmpty("M1 has been 'not measured as stated' precisely because it had no population");
        groundedness.Should().Contain(
            c => Size(c.Answer!.ExpectedSuppressed) > 0, "a suppression must be shown to happen");
        groundedness.Should().Contain(
            c => Size(c.Answer!.ExpectedSuppressed) == 0 && Size(c.ExpectedValues) > 0,
            "a verifier that suppressed everything would pass every suppression case while shipping nothing");
    }

    /// <summary>
    /// Given the committed golden set, when its NG1 scope escapes are collected, then at least one exists, and
    /// each is an `M1` case whose pinned recommendation must ship on a line that carries a citation. The
    /// citation is what separates this limit from the keyword-boundary one: the line is grounded, so
    /// `ClinicalResponseVerifier` has nothing to object to, and only a scope check could. Without a case the
    /// "does not recommend treatment" limit is asserted in prose and nothing turns red when a lexical filter
    /// starts suppressing it. A separate change
    /// </summary>
    [Fact]
    public void ScopeEscapes_OverTheGoldenSet_PinACitedRecommendationThatMustShip()
    {
        var escapes = AnswerCases().Where(c => c.Answer!.KnownScopeEscape).ToArray();

        escapes.Should().NotBeEmpty(
            "NG1's 'does not recommend treatment' is prompt-only, and a limit no case pins is a limit nobody sees");
        foreach (var escape in escapes)
        {
            var scenario = escape.Answer!;
            scenario.Metric.Should().Be("M1", $"{escape.Id}: the escape is reported beside M1's line, so it must be in M1's population");
            escape.ExpectSuccess.Should().BeTrue($"{escape.Id}: the escape is that the recommendation ships");
            Size(scenario.ExpectedSuppressed).Should().Be(0, $"{escape.Id}: an escape that is suppressed is not an escape");
            scenario.KnownKeywordEscape.Should().BeFalse(
                $"{escape.Id}: the two escapes are different limits and are counted separately");
            Size(escape.ExpectedValues).Should().BeGreaterThan(0, $"{escape.Id}: the shipped recommendation must be named");

            var draftLines = scenario.ModelScript
                .Where(turn => turn.Text is not null)
                .SelectMany(turn => turn.Text!.Split('\n'))
                .ToArray();
            foreach (var fragment in escape.ExpectedValues!)
            {
                draftLines.Where(line => line.Contains(fragment, StringComparison.Ordinal))
                    .Should().ContainSingle($"{escape.Id}: '{fragment}' must be one line of the pinned draft")
                    .Which.Should().MatchRegex(
                        @"\[[A-Za-z]+/[A-Za-z0-9\-\.]+\]",
                        $"{escape.Id}: an uncited recommendation is the keyword-boundary escape, not this one");
            }
        }
    }

    /// <summary>
    /// Given each pinned NG1 scope escape, when it runs through the shipped orchestrator and verifier, then
    /// the recommendation reaches the clinician with nothing suppressed. This is the half that goes red on
    /// purpose: a lexical "recommend" filter added below the model suppresses the line, and the false-positive
    /// cost `PROMPTS.md` §7 records then has to be restated deliberately. A separate change
    /// </summary>
    [Fact]
    public async Task ScopeEscapes_WhenRunThroughTheShippedVerifier_ShipTheRecommendationIntact()
    {
        var escapes = AnswerCases().Where(c => c.Answer!.KnownScopeEscape).ToArray();
        escapes.Should().NotBeEmpty();

        foreach (var escape in escapes)
        {
            var outcome = await GoldenSet.RunAsync(escape);

            outcome.SuppressedLines.Should().BeNullOrEmpty($"{escape.Id}: a grounded recommendation passes FR-VERIF-1");
            RubricEvaluator.Evaluate(escape, outcome)[RubricEvaluator.GroundedAnswer].Should().BeTrue(
                $"{escape.Id}: the recommendation must ship verbatim");
        }
    }

    /// <summary>
    /// Given the committed golden set, when the answer population is counted, then it holds both turns that
    /// ship a synthesized answer and turns that degrade to the deterministic fallback — a suite in which
    /// everything degrades cannot show the difference between degrading and working.
    /// </summary>
    [Fact]
    public void AnswerPopulation_WhenCounted_HoldsBothSynthesizedAndDegradedTurns()
    {
        var answers = AnswerCases();

        answers.Should().NotBeEmpty();
        answers.Should().Contain(c => c.ExpectSuccess, "a turn that ships a verified answer must be shown to happen");
        answers.Should().Contain(c => !c.ExpectSuccess, "a turn that degrades transparently must be shown to happen");
    }
}
