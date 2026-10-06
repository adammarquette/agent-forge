using System.Text.Json.Nodes;
using AgentForge.GenerateFixtureDocuments;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// What a VLM that reads perfectly would return for a document in the set: every fact the manifest lists,
/// each quoting its manifest span verbatim. So a fact that fails to resolve to a box fails because of the
/// document's layout and the resolver, never because the model paraphrased - which is what makes the count
/// a baseline for the resolver.
/// </summary>
internal static class ManifestReplies
{
    // A model estimate, deliberately not the glyph box, so a kept box shows the resolver did not replace it.
    private static readonly double[] EstimatedBox = [0.1, 0.2, 0.6, 0.03];

    /// <summary>The extraction JSON for <paramref name="manifest"/>.</summary>
    public static string ExtractionFor(DocumentManifest manifest) => manifest.DocumentType switch
    {
        DocumentManifest.LabPdf => Lab(manifest),
        DocumentManifest.IntakeForm => Intake(manifest),
        _ => throw new ArgumentOutOfRangeException(nameof(manifest), manifest.DocumentType, "Unknown document type."),
    };

    private static string Lab(DocumentManifest manifest)
    {
        var tests = new JsonArray();
        foreach (var fact in manifest.Facts)
        {
            tests.Add(new JsonObject
            {
                ["test_name"] = fact.Label,
                ["value"] = fact.Value,
                ["unit"] = fact.Unit,
                ["reference_range"] = fact.ReferenceRange,
                ["collection_date"] = fact.CollectionDate,
                ["abnormal_flag"] = fact.AbnormalFlag,
                ["citation"] = Citation(fact),
            });
        }

        return new JsonObject { ["tests"] = tests }.ToJsonString();
    }

    private static string Intake(DocumentManifest manifest)
    {
        var demographics = manifest.Facts.Single(f => f.FactType == ExpectedFact.IntakeDemographics);
        var chiefConcern = manifest.Facts.SingleOrDefault(f => f.FactType == ExpectedFact.IntakeChiefConcern);
        var medications = new JsonArray();
        foreach (var fact in manifest.Facts.Where(f => f.FactType == ExpectedFact.IntakeMedication))
        {
            medications.Add(new JsonObject { ["name"] = fact.Label, ["dose"] = fact.Value, ["citation"] = Citation(fact) });
        }

        return new JsonObject
        {
            ["demographics"] = new JsonObject
            {
                ["full_name"] = demographics.Value,
                ["date_of_birth"] = manifest.Patient.BirthDate,
                ["sex"] = manifest.Patient.Sex,
            },
            ["chief_concern"] = chiefConcern is null ? null : Item(chiefConcern),
            ["current_medications"] = medications,
            ["allergies"] = Items(manifest, ExpectedFact.IntakeAllergy),
            ["family_history"] = Items(manifest, ExpectedFact.IntakeFamilyHistory),

            // The form-level citation is the demographics block's; every other item carries its own.
            ["citation"] = Citation(demographics),
        }.ToJsonString();
    }

    private static JsonArray Items(DocumentManifest manifest, string factType) =>
        [.. manifest.Facts.Where(f => f.FactType == factType).Select(f => (JsonNode)Item(f))];

    private static JsonObject Item(ExpectedFact fact) => new() { ["text"] = fact.Value, ["citation"] = Citation(fact) };

    // On a scan the model's estimate is all there is; give it the true region, as a competent VLM would.
    private static JsonObject Citation(ExpectedFact fact) => new()
    {
        ["page"] = fact.Page,
        ["quote"] = fact.Quote,
        ["bounding_box"] = new JsonArray([.. (fact.Region ?? EstimatedBox).Select(v => (JsonNode)v)]),
    };
}
