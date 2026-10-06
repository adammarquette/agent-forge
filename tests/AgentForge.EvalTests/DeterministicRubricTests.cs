using FluentAssertions;
using AgentForge.Evals;

namespace AgentForge.EvalTests;

/// <summary>
/// Runs the golden set's <b>deterministic</b> rubrics as fast, hermetic xUnit tests — one result per case —
/// in the standard <c>dotnet test</c> flow. These rubrics are mechanically checked and never call an
/// LLM judge (evals/README.md), so a regression in the extraction/schema pipeline or in the authorization
/// gate turns a case red here immediately. The baseline/regression policy and the judge-bound rubrics
/// (<c>factually_consistent</c>, <c>safe_refusal</c>) stay in the console <c>evals</c> gate
/// (<c>dotnet run --project tests/AgentForge.Evals -- evals</c>), which never runs here.
/// reference: ARCHITECTURE-DOCUMENTS.md §8
/// </summary>
public sealed class DeterministicRubricTests
{
    // The rubrics evals/README.md pins as deterministic (no judge, cannot drift). The judge-bound rubrics are
    // deliberately excluded so this hermetic suite never depends on a live model.
    private static readonly HashSet<string> DeterministicRubrics =
    [
        "schema_valid", "citation_present", "no_phi_in_logs",
        "authorization_outcome", "no_unauthorized_disclosure", "attempt_logged",
        // The answer-path trio (M1/M2/M5) is substring and flag-set inspection over a pinned model turn,
        // so it is judge-free like the rest of this set. Separate changes
        "grounded_answer", "constraint_flagged", "transparent_degradation",
        // The evidence pair is set membership, list order, a recorded degradation-stage set and citation
        // resolution over a pinned corpus - judge-free for the same reason. A separate change
        "retrieval_hit", "evidence_grounded",
    ];

    private static readonly CaseOutcome RefusedOutcome = new(false, null, "refused", []);

    // Fully populated, so every rubric this case *does* declare scores normally rather than throwing its own
    // "claimed with nothing to check" guard - leaving the M3 coverage guard as the only thing that can throw.
    private static readonly AuthorizationScenario ScorableScenario = new()
    {
        Vector = "role_confusion",
        RequesterRole = "synthetic requester",
        Site = "default",
        SessionPatientId = "syn-patient-1",
        ClinicDayAppointments = [],
        ToolCall = new ToolCallFixture { ToolName = "get_patient_summary" },
        ForbiddenValues = ["syn-forbidden-value"],
        ExpectedLogFragments = ["authorization"],
    };

    public static TheoryData<string> GoldenCases()
    {
        var data = new TheoryData<string>();
        foreach (var name in GoldenSet.CaseFileNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void GoldenSet_WhenResolved_IsNotEmpty() =>
        GoldenSet.CaseFileNames().Should().NotBeEmpty(
            "the eval gate is meaningless without golden cases — an empty set must fail, not silently pass");

    /// <summary>
    /// Given the golden set, when M3's population is counted, then it is non-empty and holds both permits
    /// and denials — the same guard the console gate enforces, kept here so deleting the authorization cases
    /// cannot go green in <c>dotnet test</c> either (`FR-AUTH-3`, `M3`). A separate change
    /// </summary>
    [Fact]
    public void AuthorizationPopulation_WhenCounted_HasBothPermitsAndDenials()
    {
        var authorization = GoldenSet.CaseFileNames()
            .Select(GoldenSet.Load)
            .Where(c => c.Category == "authorization")
            .ToArray();

        authorization.Should().NotBeEmpty(
            "M3 reports 0 unauthorized disclosures, which over an empty population is arithmetic, not a result");
        authorization.Should().Contain(c => !c.ExpectSuccess, "a refusal must be shown to happen");
        authorization.Should().Contain(c => c.ExpectSuccess,
            "a gate that refused every requester would pass every denial case — the permit path is what rules that out");
    }

    /// <summary>
    /// Given an authorization case that does not declare both of M3's rubrics, when it is scored, then
    /// scoring fails loudly and names the rubric that is missing. M3's denominator is <b>every</b>
    /// authorization case, but its numerators only count cases that declare the rubrics — so an undeclared
    /// case is counted and inspected by nothing, and the gate prints "0 unauthorized disclosures across 14
    /// cases" over a 14th it never looked at, PASS, exit 0. That is the defect this metric exists to remove,
    /// one level up. Mirrors <c>RubricEvaluator.NoForbiddenValueDisclosed</c>, which already refuses the
    /// opposite mismatch — a case claiming the rubric with nothing forbidden. A separate change
    /// </summary>
    [Theory]
    [InlineData("safe_refusal", "no_unauthorized_disclosure", "declares neither M3 rubric")]
    [InlineData("safe_refusal,attempt_logged", "no_unauthorized_disclosure", "declares only the logging half")]
    [InlineData("safe_refusal,no_unauthorized_disclosure", "attempt_logged", "declares only the disclosure half")]
    public void Evaluate_WhenAuthorizationCaseOmitsAnM3Rubric_Throws(string rubrics, string missing, string because)
    {
        var undercheckedCase = new GoldenCase
        {
            Id = "authz-14-underchecked",
            Guards = "A synthetic 14th case standing in for one added without both M3 rubrics declared.",
            Category = RubricEvaluator.AuthorizationCategory,
            ExpectSuccess = false,
            Rubrics = rubrics.Split(','),
            Authorization = ScorableScenario,
        };

        var score = () => RubricEvaluator.Evaluate(undercheckedCase, RefusedOutcome);

        score.Should().Throw<InvalidOperationException>(
                $"an authorization case that {because} still lands in M3's denominator")
            .WithMessage("*authz-14-underchecked*")
            .WithMessage($"*{missing}*");
    }

    /// <summary>
    /// Given the committed golden set, when the authorization cases are read, then every one declares both
    /// M3 rubrics — so the number M3 prints covers exactly the population it names. A separate change
    /// </summary>
    [Fact]
    public void AuthorizationCases_WhenLoaded_EachDeclareBothM3Rubrics()
    {
        var underchecked_ = GoldenSet.CaseFileNames()
            .Select(GoldenSet.Load)
            .Where(c => c.Category == RubricEvaluator.AuthorizationCategory)
            .Where(c => RubricEvaluator.M3Rubrics.Any(r => !c.Rubrics.Contains(r, StringComparer.Ordinal)))
            .Select(c => c.Id)
            .ToArray();

        underchecked_.Should().BeEmpty(
            "M3 counts every authorization case in its denominator, so a case that declares neither "
            + "no_unauthorized_disclosure nor attempt_logged is reported as inspected and found clean");
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task GoldenCase_DeterministicRubrics_Pass(string caseFile)
    {
        var testCase = GoldenSet.Load(caseFile);
        var outcome = await GoldenSet.RunAsync(testCase);

        var scores = RubricEvaluator.Evaluate(testCase, outcome);
        var deterministic = testCase.Rubrics.Where(DeterministicRubrics.Contains).ToArray();

        deterministic.Should().NotBeEmpty(
            $"golden case '{testCase.Id}' should exercise at least one deterministic rubric");

        foreach (var rubric in deterministic)
        {
            scores[rubric].Should().BeTrue(
                $"deterministic rubric '{rubric}' must pass for golden case '{testCase.Id}'");
        }
    }
}
