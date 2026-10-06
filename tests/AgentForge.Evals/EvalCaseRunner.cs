using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.Evals.Answer;
using AgentForge.Evals.Authorization;
using AgentForge.Evals.Evidence;

namespace AgentForge.Evals;

/// <summary>
/// The one place a golden case is turned into a <see cref="CaseOutcome"/>. The console gate and the xUnit
/// theories both call it, so the two tiers cannot drift into running the same case differently
/// (evals/README.md).
/// </summary>
internal static class EvalCaseRunner
{
    /// <summary>
    /// Runs the case. A pipeline that throws instead of returning comes back as a faulted outcome, which
    /// fails every rubric the case declares: a stack trace names neither the case nor what it guards, so
    /// the gate reddened without saying which finding it was. A separate change
    /// </summary>
    public static async Task<CaseOutcome> RunAsync(GoldenCase testCase, CancellationToken cancellationToken = default)
    {
        try
        {
            return await DispatchAsync(testCase, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new CaseOutcome(
                Succeeded: false,
                ResultJson: null,
                RejectionReason: null,
                Logs: [],
                Fault: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Task<CaseOutcome> DispatchAsync(GoldenCase testCase, CancellationToken cancellationToken) =>
        testCase.Category switch
        {
            "extraction" => RunExtractionAsync(testCase, cancellationToken),
            "authorization" => AuthorizationCaseHarness.RunAsync(testCase, cancellationToken),
            "answer" => AnswerCaseHarness.RunAsync(testCase, cancellationToken),
            "evidence" => EvidenceCaseHarness.RunAsync(testCase, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unknown category '{testCase.Category}' in case '{testCase.Id}'."),
        };

    private static async Task<CaseOutcome> RunExtractionAsync(GoldenCase testCase, CancellationToken cancellationToken)
    {
        var logs = new List<CapturedLog>();
        var extractor = new DocumentExtractor(
            new StubLlmProvider(Required(testCase.StubModelResponse, testCase.Id, "stub_model_response")),
            // Same sink as the extractor: a word reader that logged document text was scanned by nothing.
            new PdfPigWordReader(new CapturingLogger<PdfPigWordReader>(logs)),
            new NoOpMetrics(),
            new CapturingLogger<DocumentExtractor>(logs));

        byte[] syntheticBytes = [0];
        var result = await extractor.ExtractAsync(
            ParseDocType(Required(testCase.DocType, testCase.Id, "doc_type")),
            syntheticBytes,
            testCase.MediaType,
            cancellationToken);

        return new CaseOutcome(result.Succeeded, result.CanonicalJson, result.RejectionReason, logs);
    }

    private static string Required(string? value, string caseId, string field) =>
        value ?? throw new InvalidOperationException($"Extraction case '{caseId}' is missing '{field}'.");

    private static ClinicalDocumentType ParseDocType(string docType) => docType switch
    {
        "lab_pdf" => ClinicalDocumentType.LabPdf,
        "intake_form" => ClinicalDocumentType.IntakeForm,
        _ => throw new InvalidOperationException($"Unknown doc_type '{docType}'."),
    };
}
