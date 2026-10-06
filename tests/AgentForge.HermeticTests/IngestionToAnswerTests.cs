using AgentForge.HermeticTests.Support;
using FluentAssertions;
using Xunit.Sdk;

namespace AgentForge.HermeticTests;

/// <summary>
/// The Week 2 Engineering Requirement end to end: fixture documents in, a cited and verified answer out,
/// with no live API. Two committed fixtures - a digital lab PDF and a scanned-style intake form image - go
/// through the real <c>DocumentIngestionService</c> (content-hash idempotency, the real
/// <c>DocumentExtractor</c> with its schema gate and PdfPig quote matcher, the real
/// <c>DerivedFactMapper</c> and <c>DerivedFactStore</c>), then a clinician's question goes through the real
/// <c>EvidenceAgentSupervisor</c> (stored facts, the real hybrid retriever, composition, and the real
/// <c>ClinicalResponseVerifier</c> as critic). Only the model is scripted.
/// <para>
/// <b>Failure mode guarded (invariant):</b> a clinical claim in the shipped answer must cite a source that
/// resolves back to the document region it came from, uncited values must not ship, and nothing the patient
/// or the clinician wrote may reach a log or a span. Each stage has unit tests of its own; what only this
/// test sees is the seams between them - a fact persisted without its box, a store read that loses the
/// document, a citation token the result cannot resolve - each of which leaves every unit test green.
/// </para>
/// REQUIREMENTS.md NFR-TEST-W2-1, FR-CITE-2
/// </summary>
public sealed class IngestionToAnswerTests
{
    [Fact]
    public async Task IngestThenAsk_FixtureDocumentsAndScriptedModel_AnswerIsGroundedCitedToTheFixturesAndPhiFree()
    {
        var outcome = await HermeticPipeline.RunAsync();

        PipelineChecks.AssertGroundedAndCited(outcome);
    }

    /// <summary>
    /// Red controls: break one stage and the checks above must fail, at that stage. Without these a check
    /// that can never fail - a box compared with itself, a PHI scan over an empty capture, a network
    /// recorder that listens to nothing - would read exactly like a passing one.
    /// </summary>
    [Theory]
    [InlineData(Sabotage.HttpCallFromThePipeline, PipelineChecks.Hermetic, "System.Net.Http.HttpRequestOut")]
    [InlineData(Sabotage.SocketConnectFromThePipeline, PipelineChecks.Hermetic, "Experimental.System.Net.Sockets.Connect")]
    [InlineData(Sabotage.DnsLookupFromThePipeline, PipelineChecks.Hermetic, "Experimental.System.Net.NameResolution.DnsLookup")]
    [InlineData(Sabotage.ParaphrasedQuotes, PipelineChecks.Extract, PipelineChecks.Extract)]
    [InlineData(Sabotage.FactReadSideDropped, PipelineChecks.Persist, PipelineChecks.Persist)]
    [InlineData(Sabotage.EmptyGuidelineCorpus, PipelineChecks.Retrieve, PipelineChecks.Retrieve)]
    [InlineData(Sabotage.PassThroughCritic, PipelineChecks.Critic, PipelineChecks.Critic)]
    [InlineData(Sabotage.PromptLogged, PipelineChecks.Phi, PipelineChecks.Phi)]
    public async Task IngestThenAsk_OneStageBroken_FailsAtThatStage(Sabotage sabotage, string expectedStage, string expectedEvidence)
    {
        var outcome = await HermeticPipeline.RunAsync(sabotage);

        var act = () => PipelineChecks.AssertGroundedAndCited(outcome);

        // The evidence pins WHICH recorder heard it, so a network control cannot pass on another source's activity.
        act.Should().Throw<XunitException>().Which.Message.Should()
            .Contain(expectedStage,
                $"breaking the pipeline with {sabotage} must be caught by the {expectedStage} checks, not an earlier one")
            .And.Contain(expectedEvidence, $"and for the reason {sabotage} introduces");
    }
}
