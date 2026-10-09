using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Agents;
using AgentForge.Observability;

namespace AgentForge.UnitTests.Agent;

public sealed class EvidenceToolTests
{
    private readonly IEvidenceRetriever _retriever = A.Fake<IEvidenceRetriever>();
    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly EvidenceTool _sut;

    public EvidenceToolTests() => _sut = new EvidenceTool(_retriever, _metrics);

    [Fact]
    public async Task GetAsync_SnippetsFound_ProjectsThemToCitableGuidelineRecords()
    {
        var snippets = new List<EvidenceSnippet>
        {
            new() { DocumentId = "anticoag-2026", Section = "Warfarin INR target", ChunkId = "chunk-1", Text = "Target INR 2.0 to 3.0.", Score = 0.9 },
        };
        A.CallTo(() => _retriever.RetrieveAsync("inr target", A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<EvidenceSnippet>>(snippets));

        var result = await _sut.GetAsync("inr target", CancellationToken.None);

        result.Snippets.Should().ContainSingle();
        result.Snippets[0].ResourceType.Should().Be("Guideline");
        result.Snippets[0].Id.Should().Be("chunk-1");
        result.Snippets[0].DocumentId.Should().Be("anticoag-2026");
        result.Snippets[0].Section.Should().Be("Warfarin INR target");
        result.Snippets[0].Text.Should().Be("Target INR 2.0 to 3.0.");
    }

    [Fact]
    public async Task GetAsync_NoSnippets_ReturnsEmptyResult()
    {
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]));

        var result = await _sut.GetAsync("nothing on file", CancellationToken.None);

        result.Snippets.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_SnippetsFound_RecordsTheRetrievalAsAChatToolHit()
    {
        // The chat path's retrieve_evidence tool reaches the same retriever as POST /evidence/ask, so it must
        // reach the same series - otherwise NFR-SLO-W2-1's p95 covers one of two call sites.
        var snippets = new List<EvidenceSnippet>
        {
            new() { DocumentId = "d", Section = "s", ChunkId = "c1", Text = "t", Score = 0.9 },
            new() { DocumentId = "d", Section = "s", ChunkId = "c2", Text = "t", Score = 0.8 },
        };
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<EvidenceSnippet>>(snippets));

        await _sut.GetAsync("inr target", CancellationToken.None);

        A.CallTo(() => _metrics.RecordEvidenceRetrieval(
                true, 2, A<TimeSpan>.That.Matches(d => d >= TimeSpan.Zero), EvidenceRetrievalEntryPoint.ChatTool))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetAsync_NoSnippets_RecordsAChatToolMiss()
    {
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<EvidenceSnippet>>([]));

        await _sut.GetAsync("nothing on file", CancellationToken.None);

        A.CallTo(() => _metrics.RecordEvidenceRetrieval(false, 0, A<TimeSpan>._, EvidenceRetrievalEntryPoint.ChatTool))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task GetAsync_Always_MeasuresTheRetrievalItself()
    {
        // The recorded duration is the retriever's own wall-clock, the same span the supervisor times.
        A.CallTo(() => _retriever.RetrieveAsync(A<string>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60));
                return (IReadOnlyList<EvidenceSnippet>)[];
            });

        await _sut.GetAsync("slow", CancellationToken.None);

        A.CallTo(() => _metrics.RecordEvidenceRetrieval(
                A<bool>._, A<int>._, A<TimeSpan>.That.Matches(d => d >= TimeSpan.FromMilliseconds(50)), A<EvidenceRetrievalEntryPoint>._))
            .MustHaveHappenedOnceExactly();
    }
}
