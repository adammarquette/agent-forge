using System.Text.Json;
using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Mcp;
using AgentForge.Verification;
using AgentForge.Verification.Rules;

namespace AgentForge.UnitTests.Verification;

/// <summary>
/// Boundary coverage for the helper the three lab-reading rules now share. The clinical behaviour
/// itself is pinned by those rules' own regression tests; these pin the edges
/// none of them reach - grouping, tie order, and how a stamp renders.
/// </summary>
public sealed class ObservationRecencyTests
{
    private static ObservationRecord Lab(string id, string code, DateTimeOffset? effective, string category = "laboratory", string status = "final") => new(
        new ClinicalSourceRef("Observation", id), category, code, 1.0, null, null, null, effective, status);

    private static DateTimeOffset On(int year, int month, int day) => new(year, month, day, 9, 0, 0, TimeSpan.Zero);

    // The tool-result envelope the dispatcher actually writes (McpToolDispatcher.ResultJsonOptions).
    private static readonly JsonSerializerOptions ToolResultOptions = new() { PropertyNameCaseInsensitive = true };

    // A synthetic FHIR Observation drawn at 23:30 local on 2026-09-11, five hours behind UTC, so its
    // UTC instant falls on the 12th. FHIR R4 requires an offset on any dateTime that carries a time,
    // so this is the ordinary shape of an evening draw, not an exotic one.
    private const string LateEveningInrBundle = """
    {
      "resourceType": "Bundle",
      "entry": [
        {
          "resource": {
            "resourceType": "Observation",
            "id": "syn-evening-inr",
            "status": "final",
            "category": [ { "coding": [ { "code": "laboratory" } ] } ],
            "code": { "text": "INR" },
            "valueQuantity": { "value": 4.6 },
            "effectiveDateTime": "2026-09-11T23:30:00-05:00"
          }
        }
      ]
    }
    """;

    [Fact]
    public void MostRecentPerCode_SeveralResultsForOneCode_KeepsOnlyTheLatest()
    {
        IReadOnlyList<ObservationRecord> labs =
        [
            Lab("a", "INR", On(2019, 4, 2)),
            Lab("c", "INR", On(2026, 9, 11)),
            Lab("b", "INR", On(2021, 8, 14)),
        ];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/c");
    }

    [Fact]
    public void MostRecentPerCode_DifferentCodes_KeepsTheLatestOfEach()
    {
        IReadOnlyList<ObservationRecord> labs =
        [
            Lab("inr-old", "INR", On(2019, 4, 2)),
            Lab("k-old", "Potassium", On(2021, 6, 2)),
            Lab("inr-new", "INR", On(2026, 9, 11)),
            Lab("k-new", "Potassium", On(2026, 9, 11)),
        ];

        ObservationRecency.MostRecentPerCode(labs).Select(o => o.Source.Citation)
            .Should().BeEquivalentTo(["Observation/inr-new", "Observation/k-new"]);
    }

    [Fact]
    public void MostRecentPerCode_SameCodeDisplayInTwoCategories_KeepsOneOfEach()
    {
        // Labs and vitals share this list. A point-of-care reading and a lab result that happen to
        // carry the same display are two different measurements, not one superseding the other.
        IReadOnlyList<ObservationRecord> observations =
        [
            Lab("lab", "Glucose", On(2026, 9, 11)),
            Lab("vital", "Glucose", On(2026, 3, 2), category: "vital-signs"),
        ];

        ObservationRecency.MostRecentPerCode(observations).Should().HaveCount(2);
    }

    [Fact]
    public void MostRecentPerCode_CodeDisplayDifferingOnlyInCase_TreatsThemAsOneCode()
    {
        IReadOnlyList<ObservationRecord> labs = [Lab("a", "INR", On(2019, 4, 2)), Lab("b", "inr", On(2026, 9, 11))];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/b");
    }

    [Fact]
    public void MostRecentPerCode_EveryResultUndated_KeepsTheFirstOneTheToolReturned()
    {
        // Nothing orders undated results, so the selection must at least be deterministic rather
        // than depending on the grouping's internal order.
        IReadOnlyList<ObservationRecord> labs = [Lab("first", "INR", null), Lab("second", "INR", null)];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/first");
    }

    [Fact]
    public void MostRecentPerCode_UndatedAlongsideDated_KeepsTheDatedOne()
    {
        IReadOnlyList<ObservationRecord> labs = [Lab("undated", "INR", null), Lab("dated", "INR", On(2019, 4, 2))];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/dated");
    }

    [Fact]
    public void MostRecentPerCode_TwoResultsAtTheSameInstant_KeepsTheFirstOneTheToolReturned()
    {
        IReadOnlyList<ObservationRecord> labs =
        [
            Lab("first", "INR", On(2026, 9, 11)),
            Lab("second", "INR", On(2026, 9, 11)),
        ];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/first");
    }

    [Fact]
    public void MostRecentPerCode_NoObservations_ReturnsEmpty() =>
        ObservationRecency.MostRecentPerCode([]).Should().BeEmpty();

    [Fact]
    public void MostRecentPerCode_LaterResultEnteredInError_KeepsTheEarlierValidOne()
    {
        // Ranking on date alone lets FHIR's tombstone status outrank a real result and silence the
        // rule - the missing flag this change rejects windowing to avoid.
        IReadOnlyList<ObservationRecord> labs =
        [
            Lab("real", "INR", On(2026, 9, 11)),
            Lab("retracted", "INR", On(2026, 9, 12), status: "entered-in-error"),
        ];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/real");
    }

    [Fact]
    public void MostRecentPerCode_EveryResultForACodeEnteredInError_DropsTheCodeEntirely()
    {
        // Not a missed flag: a retracted record is not a value the patient has.
        IReadOnlyList<ObservationRecord> labs = [Lab("retracted", "INR", On(2026, 9, 12), status: "entered-in-error")];

        ObservationRecency.MostRecentPerCode(labs).Should().BeEmpty();
    }

    [Fact]
    public void MostRecentPerCode_LaterResultStillPreliminary_StillCounts()
    {
        // Only the tombstone is read. A preliminary result is real data and is often the newest
        // thing on file; a status whitelist would also drop every Observation whose FHIR status is
        // absent, which ObservationMapper maps to "unknown".
        IReadOnlyList<ObservationRecord> labs =
        [
            Lab("final", "INR", On(2026, 9, 11)),
            Lab("preliminary", "INR", On(2026, 9, 12), status: "preliminary"),
        ];

        ObservationRecency.MostRecentPerCode(labs)
            .Should().ContainSingle().Which.Source.Citation.Should().Be("Observation/preliminary");
    }

    [Fact]
    public void MostRecentPerCode_ResultWithNoFhirStatus_StillCounts()
    {
        IReadOnlyList<ObservationRecord> labs = [Lab("a", "INR", On(2026, 9, 11), status: "unknown")];

        ObservationRecency.MostRecentPerCode(labs).Should().ContainSingle();
    }

    [Fact]
    public void AsOf_ObservationCarryingAnEffectiveDate_RendersAnUnambiguousIsoDate()
    {
        ObservationRecency.AsOf(Lab("a", "INR", On(2026, 9, 11))).Should().Be("as of 2026-09-11");
    }

    [Fact]
    public void AsOf_ObservationWithNoEffectiveDate_SaysSoRatherThanGuessing()
    {
        ObservationRecency.AsOf(Lab("a", "INR", null)).Should().Be("date unknown");
    }

    [Fact]
    public void AsOf_LateEveningDrawCarryingASourceOffset_StampsTheDayItWasDrawn()
    {
        // Driven from FHIR text through the real mapper, because that is where the offset is at
        // risk. A hand-built DateTimeOffset asserts a shape the pipeline cannot produce and so
        // cannot fail on this defect - which is how it shipped.
        var record = ObservationMapper.MapBundle(LateEveningInrBundle)[0];

        ObservationRecency.AsOf(record).Should().Be("as of 2026-09-11");
    }

    [Fact]
    public void Stamp_LateEveningDrawThroughTheWholeChain_ReachesTheFlagAsTheDayItWasDrawn()
    {
        // The whole path a stamp travels: FHIR bundle -> ObservationMapper -> the tool-result JSON
        // the dispatcher writes -> ToolResultJsonScanner -> CurrentLabs -> the flag a cardiologist
        // reads. Any link that normalises the offset away moves the date a day and reddens here.
        var json = JsonSerializer.Serialize(
            new LabsResult(ObservationMapper.MapBundle(LateEveningInrBundle)), ToolResultOptions);

        var flags = new InrTherapeuticRangeRule().Evaluate(ToolResultJsonScanner.Scan([json]).Input);

        flags.Should().ContainSingle().Which.Description.Should().Contain("as of 2026-09-11");
    }
}
