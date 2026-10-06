using Microsoft.Extensions.Logging;

namespace AgentForge.Agents.Ingestion;

/// <summary>Structured log events for ingestion. Counts only — no patient ids, file names, or document
/// content (no PHI in logs, ARCHITECTURE-DOCUMENTS.md §12).</summary>
internal static partial class DocumentIngestionServiceLog
{
    [LoggerMessage(
        EventId = 4300,
        Level = LogLevel.Information,
        Message = "Document already ingested (content hash present); skipping extraction.")]
    public static partial void AlreadyIngested(ILogger logger);

    [LoggerMessage(
        EventId = 4301,
        Level = LogLevel.Warning,
        Message = "Ingestion stopped: extraction was rejected at the schema gate; nothing persisted.")]
    public static partial void ExtractionRejected(ILogger logger);

    [LoggerMessage(
        EventId = 4302,
        Level = LogLevel.Information,
        Message = "Document ingested: {FactCount} derived fact(s) persisted.")]
    public static partial void Ingested(ILogger logger, int factCount);

    [LoggerMessage(
        EventId = 4303,
        Level = LogLevel.Error,
        Message = "Ingestion stopped: extraction threw before returning; recording the \"error\" outcome and rethrowing: {ExceptionType}")]
    public static partial void ExtractionThrew(ILogger logger, Type exceptionType);

    [LoggerMessage(
        EventId = 4304,
        Level = LogLevel.Error,
        Message = "Ingestion stopped: persisting the derived facts threw after a successful extraction; recording the \"error\" outcome and rethrowing: {ExceptionType}")]
    public static partial void PersistThrew(ILogger logger, Type exceptionType);
}
