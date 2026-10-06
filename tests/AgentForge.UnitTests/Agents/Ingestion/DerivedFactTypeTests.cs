using FluentAssertions;
using AgentForge.Agents.Ingestion;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Llm;

namespace AgentForge.UnitTests.Agents.Ingestion;

/// <summary>
/// Drives <see cref="DerivedFactType"/> — the bounded, code-owned set that supplies the <c>field</c> label on
/// <c>agentforge.extraction_field_outcomes</c>. The hazard it exists to close is a separate change: a label whose
/// values come from model output mints one permanent series per string the model invents. Every value here is
/// a literal in this repository, and anything else collapses to a single <c>other</c> bucket.
/// </summary>
public sealed class DerivedFactTypeTests
{
    private static readonly LlmUsage NoUsage = new(0, 0, 0m);

    [Fact]
    public void ToFieldLabel_AFactTypeTheMapperEmits_ReturnsItUnchanged() =>
        DerivedFactType.ToFieldLabel(DerivedFactType.LabResult).Should().Be(DerivedFactType.LabResult);

    [Theory]
    [InlineData("lab.result.potassium")]
    [InlineData("Potassium 5.8 (H)")]
    [InlineData("")]
    [InlineData(null)]
    public void ToFieldLabel_AnythingNotInTheSet_CollapsesToOther(string? factType) =>
        DerivedFactType.ToFieldLabel(factType).Should().Be(DerivedFactType.Other);

    [Fact]
    public void Labels_Always_IsTheFactTypesPlusOtherAndNothingElse() =>
        DerivedFactType.Labels.Should().BeEquivalentTo(DerivedFactType.All.Append(DerivedFactType.Other));

    [Fact]
    public void All_Always_ContainsEveryFactTypeTheMapperCanProduce()
    {
        // The guard that catches a *new* fact type rather than a hostile one: add a seventh kind of fact and
        // forget this set, and its whole population lands in `other` beside every unknown - the field-level
        // pass rate for it silently stops existing. Driving the real mapper is what makes that detectable
        // here instead of on the dashboard.
        var mapper = new DerivedFactMapper();
        var emitted = mapper.Map(Lab(), "dr-1").Concat(mapper.Map(Intake(), "dr-1"))
            .Select(fact => fact.FactType)
            .Distinct();

        emitted.Should().BeEquivalentTo(DerivedFactType.All);
    }

    private const string LabJson =
        """
        {"tests":[{"test_name":"Potassium","value":"5.8","unit":"mmol/L","reference_range":"3.5-5.1","collection_date":"2026-07-02","abnormal_flag":true,"citation":{"page":1,"quote":"K+ 5.8 (H)","bounding_box":[0.1,0.2,0.3,0.4],"match":"exact"}}]}
        """;

    private const string IntakeJson =
        """
        {"demographics":{"full_name":"Jane Synthetic","date_of_birth":"1970-01-01","sex":"F"},"chief_concern":{"text":"palpitations","citation":{"page":1,"quote":"Reason for visit: palpitations"}},"current_medications":[{"name":"Metoprolol","dose":"25mg","citation":{"page":2,"quote":"Metoprolol 25mg"}}],"allergies":[{"text":"penicillin","citation":{"page":2,"quote":"Penicillin - hives"}}],"family_history":[{"text":"father MI at 60","citation":{"page":2,"quote":"Father: MI at 60"}}],"citation":{"page":1,"quote":"Name: Jane Synthetic"}}
        """;

    private static DocumentExtractionResult Lab() =>
        DocumentExtractionResult.Ok(ClinicalDocumentType.LabPdf, LabJson, NoUsage);

    private static DocumentExtractionResult Intake() =>
        DocumentExtractionResult.Ok(ClinicalDocumentType.IntakeForm, IntakeJson, NoUsage);
}
