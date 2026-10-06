using FluentAssertions;
using AgentForge.Agent;

namespace AgentForge.UnitTests.Agent;

public sealed class CardiologyProfileTests
{
    [Fact]
    public void SystemPrompt_Always_IsNotEmpty()
    {
        CardiologyProfile.SystemPrompt.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("cite", "grounding discipline (FR-VERIF-1) must survive any future prompt edit")]
    [InlineData("do not diagnose", "NG1 - the copilot surfaces and cites, it does not diagnose or recommend treatment")]
    [InlineData("do not recommend", "NG1 - no treatment recommendations")]
    // The third NG1 verb, unpinned while the other two were pinned from the start. Unlike
    // them it has a deterministic backstop (ClinicalScopeGuardrailTests), so this row guards the
    // *wording* the clinician reads in a refusal, not the rule - the rule survives the sentence.
    [InlineData("do not place orders", "NG1 - no orders; the clinician decides")]
    [InlineData("other patient", "FR-CHAT-3 - must refuse to pull a different patient's data into the conversation")]
    [InlineData("cannot verify", "UC-5 - must communicate uncertainty rather than guess when data is missing")]
    public void SystemPrompt_Always_ContainsRequiredSafetyInstruction(string requiredPhrase, string becauseReason)
    {
        // Whitespace-normalized so line-wrapping in the source raw string literal can't spuriously
        // split a required phrase across lines and fail a check that should only care about content.
        var normalized = string.Join(' ', CardiologyProfile.SystemPrompt.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        normalized.Should().Contain(requiredPhrase, becauseReason);
    }

    [Fact]
    public void SystemPrompt_Always_DirectsTheBriefToSurfaceIngestedDocumentFacts()
    {
        // The brief must surface findings that are only in an uploaded document, not just the structured
        // record - so the prompt must keep directing get_document_facts. A separate change.
        CardiologyProfile.SystemPrompt.Should().Contain("get_document_facts");
    }

    [Fact]
    public void SystemPrompt_Always_TellsTheModelNotToNarrateDocumentProvenance()
    {
        // The [Document/<id>] citation carries the source, so a document-derived value is stated and cited,
        // not described as "on the uploaded/outside report" in prose (demo feedback). A separate change.
        var normalized = string.Join(' ', CardiologyProfile.SystemPrompt.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        normalized.Should().Contain("do not narrate its provenance");
    }

    // Regression. 378786d narrowed the brief's batch to four tools and
    // nothing noticed for two months: get_labs, get_recent_encounters and get_documents carried no
    // live traffic, so a 401 on those three was invisible. A prompt-inventory check cannot catch it -
    // it asserts the doc matches the constant, so dropping a tool from both stays green. This pins
    // the batch LIST itself, which is the thing that regressed.
    [Fact]
    public void SystemPrompt_Always_DirectsTheBriefToCallEveryReadToolInOneBatch()
    {
        const string batchMarker = "ONE parallel batch -";
        var prompt = CardiologyProfile.SystemPrompt;
        var start = prompt.IndexOf(batchMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the brief's batching instruction is what this test pins");
        var end = prompt.IndexOf("rather than", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the batch list ends before the 'rather than' clause");

        var batchList = prompt[start..end];

        batchList.Should().ContainAll(
            "get_patient_summary",
            "get_interval_changes",
            "get_labs",
            "get_vitals",
            "get_recent_encounters",
            "get_documents",
            "get_document_facts");
    }
}
