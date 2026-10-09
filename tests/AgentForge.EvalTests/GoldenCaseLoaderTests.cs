using FluentAssertions;
using AgentForge.Evals;

namespace AgentForge.EvalTests;

/// <summary>
/// Specifies the golden-case loader's contract around <c>guards</c> — the per-case statement of the failure
/// mode the case defends (`REQUIREMENTS.md` FR-EVAL-1: "each documents the failure mode it guards"). The slice-level
/// prose in <c>evals/README.md</c> and the self-describing case ids are both good and both survive; neither
/// is per-case documentation, and neither reaches someone reading a single file.
/// </summary>
public sealed class GoldenCaseLoaderTests
{
    private const string CaseWithoutGuards = """
        {
          "id": "intake-no-guards",
          "category": "extraction",
          "doc_type": "intake_form",
          "expect_success": false,
          "stub_model_response": "not json",
          "rubrics": ["schema_valid", "safe_refusal"]
        }
        """;

    private const string CaseWithBlankGuards = """
        {
          "id": "intake-blank-guards",
          "category": "extraction",
          "doc_type": "intake_form",
          "expect_success": false,
          "stub_model_response": "not json",
          "guards": "   ",
          "rubrics": ["schema_valid", "safe_refusal"]
        }
        """;

    /// <summary>
    /// Given a golden case with no <c>guards</c> member, when the loader parses it, then it refuses and names
    /// both the case and the missing field. Documentation nobody can skip is the only kind that stays true:
    /// a new case added without a stated failure mode must not reach the gate at all.
    /// </summary>
    [Fact]
    public void Parse_WhenCaseOmitsGuards_ThrowsNamingTheCase()
    {
        var parse = () => GoldenCaseLoader.Parse(CaseWithoutGuards, "intake-no-guards.json");

        parse.Should().Throw<InvalidOperationException>(
                "a case that states no failure mode documents nothing, and a field the loader tolerates is a "
                + "field the next case omits")
            .WithMessage("*intake-no-guards.json*")
            .WithMessage("*guards*");
    }

    /// <summary>
    /// Given a golden case whose <c>guards</c> is present but blank, when the loader parses it, then it
    /// refuses too — an empty string satisfies "the key exists" while documenting nothing, which is the
    /// cheapest way past a required field.
    /// </summary>
    [Fact]
    public void Parse_WhenGuardsIsBlank_ThrowsNamingTheCase()
    {
        var parse = () => GoldenCaseLoader.Parse(CaseWithBlankGuards, "intake-blank-guards.json");

        parse.Should().Throw<InvalidOperationException>("whitespace is not a stated failure mode")
            .WithMessage("*intake-blank-guards.json*")
            .WithMessage("*guards*");
    }

    /// <summary>
    /// Given a well-formed case, when the loader parses it, then <c>guards</c> is carried onto the case so the
    /// console gate can print it beside a failure.
    /// </summary>
    [Fact]
    public void Parse_WhenGuardsIsStated_CarriesItOntoTheCase()
    {
        var json = CaseWithoutGuards.Replace(
            "\"expect_success\": false,",
            "\"expect_success\": false,\n  \"guards\": \"Prose instead of JSON is refused, not parsed.\",",
            StringComparison.Ordinal);

        var parsed = GoldenCaseLoader.Parse(json, "intake-no-guards.json");

        parsed.Guards.Should().Be("Prose instead of JSON is refused, not parsed.");
    }

    // Throws when run (no doc_type) and declares no rubric, so a fault has nothing to fail.
    private const string ThrowingCaseWithNoRubrics = """
        {
          "id": "intake-throws-no-rubrics",
          "category": "extraction",
          "expect_success": false,
          "stub_model_response": "not json",
          "guards": "A case whose run throws must redden the gate, not score nothing and pass.",
          "rubrics": []
        }
        """;

    /// <summary>
    /// Given a case that declares no rubrics and whose run throws, when it bypasses the loader and is run
    /// and scored, then it faults and scores nothing — so no rubric fails, the case never reaches the gate's
    /// failing list, and the gate would print PASS over a run that crashed. This is the hole the loader
    /// check below closes.
    /// </summary>
    [Fact]
    public async Task ThrowingCaseWithNoRubrics_WhenRunPastTheLoader_FaultsAndScoresNothing()
    {
        var unguarded = new GoldenCase
        {
            Id = "intake-throws-no-rubrics",
            Category = "extraction",
            ExpectSuccess = false,
            StubModelResponse = "not json",
            Guards = "A case whose run throws must redden the gate, not score nothing and pass.",
            Rubrics = [],
        };

        var outcome = await EvalCaseRunner.RunAsync(unguarded);

        outcome.Fault.Should().NotBeNull("the fixture has no doc_type, so the extraction run throws");
        RubricEvaluator.Evaluate(unguarded, outcome).Should().BeEmpty(
            "a fault fails every declared rubric, and this case declares none");
    }

    /// <summary>
    /// Given a case that declares no rubrics, when the loader parses it, then it refuses and names the case
    /// and the field. A case checked by nothing cannot fail, so a fault in it cannot redden the gate —
    /// every case that loads must declare at least one rubric.
    /// </summary>
    [Fact]
    public void Parse_WhenCaseDeclaresNoRubrics_ThrowsNamingTheCase()
    {
        var parse = () => GoldenCaseLoader.Parse(ThrowingCaseWithNoRubrics, "intake-throws-no-rubrics.json");

        parse.Should().Throw<InvalidOperationException>(
                "a case with no rubrics passes whatever its run does, including throwing")
            .WithMessage("*intake-throws-no-rubrics.json*")
            .WithMessage("*rubrics*");
    }

    /// <summary>
    /// Given a case whose <c>rubrics</c> is an explicit <c>null</c>, when the loader parses it, then it
    /// refuses too: the serializer does not enforce the non-nullable annotation, so <c>null</c> satisfies
    /// "the key exists" while declaring nothing.
    /// </summary>
    [Fact]
    public void Parse_WhenRubricsIsNull_ThrowsNamingTheCase()
    {
        var json = ThrowingCaseWithNoRubrics.Replace("\"rubrics\": []", "\"rubrics\": null", StringComparison.Ordinal);

        var parse = () => GoldenCaseLoader.Parse(json, "intake-null-rubrics.json");

        parse.Should().Throw<InvalidOperationException>("null declares no rubric either")
            .WithMessage("*intake-null-rubrics.json*")
            .WithMessage("*rubrics*");
    }

    /// <summary>
    /// Given the committed golden set, when every case is loaded, then each states the failure mode it
    /// guards. The loader already refuses a case without one; this fails with the whole list rather than on
    /// whichever file the enumerator reached first.
    /// </summary>
    [Fact]
    public void GoldenSet_WhenLoaded_EveryCaseStatesWhatItGuards()
    {
        var silent = GoldenSet.CaseFileNames()
            .Where(name => string.IsNullOrWhiteSpace(ReadGuardsOrNull(name)))
            .ToArray();

        silent.Should().BeEmpty(
            "every golden case names the boundary, invariant or regression risk it defends (REQUIREMENTS.md FR-EVAL-1)");
    }

    private static string? ReadGuardsOrNull(string fileName)
    {
        try
        {
            return GoldenSet.Load(fileName).Guards;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
