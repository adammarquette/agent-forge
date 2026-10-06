using AgentForge.Data.Entities;
using Microsoft.Extensions.Logging;

namespace AgentForge.Documents;

/// <summary>Source-generated log messages for <see cref="DocumentExtractor"/> (CA1848). PHI-free.</summary>
internal static partial class DocumentExtractorLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Extraction produced no JSON payload for {DocumentType}")]
    public static partial void NoJsonPayload(ILogger logger, ClinicalDocumentType documentType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Extraction failed schema validation for {DocumentType}")]
    public static partial void SchemaValidationFailed(ILogger logger, ClinicalDocumentType documentType, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "PDF word geometry unreadable (scan / encrypted / malformed); citation quotes cannot be checked and boxes fall back to the model estimate")]
    public static partial void PdfWordsUnreadable(ILogger logger, Exception exception);

    // Counts only. The quote is free text lifted off a clinical document, so the one thing this line must
    // not contain is the thing it is about. A separate change
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Unlocatable} of {Total} extraction citations for {DocumentType} quote text that is not in the document, or that does not carry the fact they cite; their model-estimated bounding boxes were discarded")]
    public static partial void UnlocatableQuotes(ILogger logger, int unlocatable, int total, ClinicalDocumentType documentType);
}
