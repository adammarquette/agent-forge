using System.Diagnostics;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Documents.Extraction;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.UnitTests.Documents;

/// <summary>
/// NFR-TRACE-W2 one level below the worker spans: <see cref="DocumentExtractor"/> opens one span around
/// its VLM call, as a child of whatever span called it - the intake-extractor worker span in the graph - so the
/// extraction's latency reads as the model call's rather than the worker's. Guarded failure modes: the call
/// going untraced or losing its parent, and the document, the model's reply or an exception message reaching a
/// span attribute (ARCHITECTURE-DOCUMENTS.md §12).
/// </summary>
public sealed class DocumentExtractorTracingTests
{
    // Synthetic sentinels, each distinctive enough that a substring hit in a span can only be a leak.
    private const string DocumentText = "Zelda Quimby MRN 88410 Potassium 5.93";
    private const string ModelReply =
        """
        {"tests":[{"test_name":"PotassiumSentinel","value":"5.93","unit":"mmol/L","reference_range":null,
        "collection_date":null,"abnormal_flag":true,
        "citation":{"page":1,"quote":"Zelda Quimby Potassium 5.93","bounding_box":null}}]}
        """;

    private readonly ILlmProvider _llm = A.Fake<ILlmProvider>();
    private readonly IPdfWordReader _pdfReader = A.Fake<IPdfWordReader>();

    public DocumentExtractorTracingTests()
    {
        A.CallTo(() => _pdfReader.ReadTextLayer(A<ReadOnlyMemory<byte>>._)).Returns(PdfTextLayer.None);
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .Returns(new LlmResponse(ModelReply, [], LlmStopReason.EndTurn, new LlmUsage(1200, 340, 0.01m)));
    }

    private DocumentExtractor CreateSut() =>
        new(_llm, _pdfReader, A.Fake<IAgentForgeMetrics>(), NullLogger<DocumentExtractor>.Instance);

    private Task<DocumentExtractionResult> ExtractAsync() => CreateSut().ExtractAsync(
        ClinicalDocumentType.LabPdf, System.Text.Encoding.UTF8.GetBytes(DocumentText), "application/pdf",
        CancellationToken.None);

    [Fact]
    public async Task ExtractAsync_UnderAWorkerSpan_OpensOneVlmSpanAsItsChild()
    {
        using var recorder = SpanRecorder.Start();
        Activity? worker;

        using (worker = AgentForgeActivitySource.Instance.StartActivity("worker.intake-extractor"))
        {
            await ExtractAsync();
        }

        worker.Should().NotBeNull();
        var vlm = recorder.Spans.Should().ContainSingle(s => s.DisplayName == "extraction.vlm").Subject;
        vlm.ParentSpanId.Should().Be(worker!.SpanId, "the VLM call nests inside the worker that made it");
        recorder.Spans.Should().HaveCount(2, "the extractor opens one span, for the model call, and no other");
    }

    [Fact]
    public async Task ExtractAsync_ModelAnswers_VlmSpanCarriesDocumentTypeOutcomeAndTokenCounts()
    {
        using var recorder = SpanRecorder.Start();

        await ExtractAsync();

        var vlm = recorder.Spans.Should().ContainSingle(s => s.DisplayName == "extraction.vlm").Subject;
        vlm.GetTagItem("agentforge.document.type").Should().Be("lab_pdf");
        vlm.GetTagItem("agentforge.outcome").Should().Be("responded");
        vlm.GetTagItem("gen_ai.usage.input_tokens").Should().Be(1200);
        vlm.GetTagItem("gen_ai.usage.output_tokens").Should().Be(340);
        vlm.Status.Should().Be(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task ExtractAsync_ModelThrows_MarksTheVlmSpanFailedByExceptionTypeOnlyAndRethrows()
    {
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new TimeoutException($"VLM timed out reading {DocumentText}"));
        using var recorder = SpanRecorder.Start();

        var act = ExtractAsync;

        await act.Should().ThrowAsync<TimeoutException>();
        var vlm = recorder.Spans.Should().ContainSingle(s => s.DisplayName == "extraction.vlm").Subject;
        vlm.Status.Should().Be(ActivityStatusCode.Error);
        vlm.GetTagItem("error.type").Should().Be(typeof(TimeoutException).FullName);
        recorder.ExportedStrings().Should().NotContain(s => s.Contains("Zelda", StringComparison.Ordinal),
            "an exception message is free text and may carry the document, so only its type reaches the trace");
    }

    [Fact]
    public async Task ExtractAsync_ModelThrows_LeavesTheVlmSpanWithNoOutcomeAndNoTokenCounts()
    {
        // The failed-extraction shape ARCHITECTURE-DOCUMENTS.md §10 documents: `responded` is never set when the
        // model did not answer, so the absence of an outcome plus Error is the failure. A separate change
        A.CallTo(() => _llm.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("model unavailable"));
        using var recorder = SpanRecorder.Start();

        var act = ExtractAsync;

        await act.Should().ThrowAsync<HttpRequestException>();
        var vlm = recorder.Spans.Should().ContainSingle(s => s.DisplayName == "extraction.vlm").Subject;
        vlm.GetTagItem("agentforge.outcome").Should().BeNull();
        vlm.GetTagItem("gen_ai.usage.input_tokens").Should().BeNull();
        vlm.GetTagItem("gen_ai.usage.output_tokens").Should().BeNull();
        vlm.GetTagItem("agentforge.document.type").Should().NotBeNull("the type is set before the call");
    }

    [Fact]
    public async Task ExtractAsync_ModelAnswers_NoSpanCarriesDocumentTextFieldValuesOrIdentifiers()
    {
        using var recorder = SpanRecorder.Start();

        await ExtractAsync();

        string[] phi = [DocumentText, "Zelda", "Quimby", "88410", "5.93", "PotassiumSentinel", ModelReply];
        recorder.Spans.Should().NotBeEmpty();
        recorder.ExportedStrings().Should().NotContain(
            s => phi.Any(p => s.Contains(p, StringComparison.OrdinalIgnoreCase)),
            "ARCHITECTURE-DOCUMENTS.md §12: traces carry no patient identifiers, raw document text or extracted clinical values");
    }

    [Fact]
    public async Task ExtractAsync_EveryAttributeKeyIsOneOfTheBoundedPhiFreeSet()
    {
        using var recorder = SpanRecorder.Start();

        await ExtractAsync();

        recorder.Spans.Should().NotBeEmpty();
        recorder.TagKeys().Distinct().Should().BeSubsetOf(
        [
            "agentforge.document.type", "agentforge.outcome", "gen_ai.usage.input_tokens",
            "gen_ai.usage.output_tokens", "error.type",
        ]);
    }
}
