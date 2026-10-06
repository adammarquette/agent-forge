using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentForge.GenerateFixtureDocuments;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// What the scripted model says. The extraction replies are what a competent VLM would return for the
/// two fixtures - every quote copied from the fixture's own printed text (<see cref="SyntheticLabPanel"/>,
/// <see cref="SyntheticIntakeForm"/>), and every box a plausible estimate rather than the truth, so the
/// extractor's own geometry is what has to put the right box on the lab. The composer cites only tokens
/// it was actually handed, the way the composer prompt asks, and adds one fabricated uncited value the
/// critic has to remove.
/// </summary>
internal static class CannedReplies
{
    /// <summary>The line the composer invents; the critic must suppress it.</summary>
    public const string FabricatedClaim = "Creatinine is 2.4 mg/dL and rising.";

    /// <summary>
    /// A row the VLM reports that the PDF does not print. Its quote is searched for and not found, so the fact
    /// ships marked unlocatable with no box - the path a fabricating model takes.
    /// </summary>
    public static LabRow InventedLabRow { get; } = new("Magnesium", "1.4", "mg/dL", "1.7-2.2", "L");

    /// <summary>The lab row the answer is about.</summary>
    public static LabRow Potassium => SyntheticLabPanel.Rows[0];

    // A model estimate, deliberately not the glyph box, so an unchanged box would show the resolver did nothing.
    private static readonly double[] EstimatedLabBox = [0.1, 0.2, 0.6, 0.03];

    /// <summary>The lab extraction, quoting each row verbatim - or, with <paramref name="paraphrase"/>, as a model that rewords.</summary>
    public static string LabExtraction(bool paraphrase = false)
    {
        var tests = new JsonArray();
        foreach (var row in SyntheticLabPanel.Rows.Append(InventedLabRow))
        {
            var invented = ReferenceEquals(row, InventedLabRow);
            tests.Add(new JsonObject
            {
                ["test_name"] = row.Test,
                ["value"] = row.Result,
                ["unit"] = row.Units,
                ["reference_range"] = row.Reference,
                ["collection_date"] = SyntheticLabPanel.CollectionDate,
                ["abnormal_flag"] = row.Flag == "H",
                ["citation"] = Citation(paraphrase && !invented ? $"{row.Test} measured at {row.Result}" : row.Quote, EstimatedLabBox),
            });
        }

        return new JsonObject { ["tests"] = tests }.ToJsonString();
    }

    /// <summary>The intake extraction; boxes are where the form really prints each string.</summary>
    public static string IntakeExtraction() => new JsonObject
    {
        ["demographics"] = new JsonObject
        {
            ["full_name"] = "DEMO HAROLD WHITFIELD",
            ["date_of_birth"] = SyntheticCohort.BirthDate,
            ["sex"] = "male",
        },
        ["chief_concern"] = Item(SyntheticIntakeForm.ChiefConcern),
        ["current_medications"] = new JsonArray(
            Medication("SPIRONOLACTONE", "25 MG DAILY", SyntheticIntakeForm.Spironolactone),
            Medication("LISINOPRIL", "20 MG DAILY", SyntheticIntakeForm.Lisinopril)),
        ["allergies"] = new JsonArray(Item(SyntheticIntakeForm.Allergy)),
        ["family_history"] = new JsonArray(Item(SyntheticIntakeForm.FamilyHistory)),
        ["citation"] = Citation(SyntheticIntakeForm.NameLine, SyntheticIntakeForm.BoxOf(SyntheticIntakeForm.NameLine)),
    }.ToJsonString();

    /// <summary>
    /// The composer: one statement per source, each cited with the token the prompt carried for it - or left
    /// uncited when the prompt carried none, which is what a model does and what the critic exists to catch.
    /// </summary>
    public static string Compose(string prompt)
    {
        var lab = Token(prompt, $@"\[(Derived/[0-9a-f]{{8}})\] lab\.result: {Regex.Escape(Potassium.Test)} ");
        var medication = Token(prompt, $@"\[(Derived/[0-9a-f]{{8}})\] intake\.medication: {Regex.Escape(SyntheticIntakeForm.Spironolactone)}");
        var guideline = Token(prompt, @"\[(Guideline/[A-Za-z0-9\-\.]+)\][^\n]*potassium");

        return string.Join("\n",
            $"Potassium is {Potassium.Result} {Potassium.Units}, above the {Potassium.Reference} reference range{Cite(lab)}.",
            $"The intake form lists spironolactone 25 mg daily{Cite(medication)}.",
            $"Guidance is to reassess mineralocorticoid antagonist therapy when potassium is above 5.5 mmol/L{Cite(guideline)}.",
            FabricatedClaim);
    }

    private static string Cite(string? token) => token is null ? string.Empty : $" [{token}]";

    private static string? Token(string prompt, string pattern)
    {
        var match = Regex.Match(prompt, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static JsonObject Medication(string name, string dose, string quote) => new()
    {
        ["name"] = name,
        ["dose"] = dose,
        ["citation"] = Citation(quote, SyntheticIntakeForm.BoxOf(quote)),
    };

    private static JsonObject Item(string printed) => new()
    {
        ["text"] = printed,
        ["citation"] = Citation(printed, SyntheticIntakeForm.BoxOf(printed)),
    };

    private static JsonObject Citation(string quote, double[] box) => new()
    {
        ["page"] = 1,
        ["quote"] = quote,
        ["bounding_box"] = new JsonArray([.. box.Select(v => (JsonNode)v)]),
    };
}
