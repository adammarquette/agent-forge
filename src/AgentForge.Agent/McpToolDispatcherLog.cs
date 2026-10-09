using Microsoft.Extensions.Logging;

namespace AgentForge.Agent;

/// <summary>Source-generated log messages for <see cref="McpToolDispatcher"/> (CA1848).</summary>
internal static partial class McpToolDispatcherLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Suspicious tool call: {ToolName} arguments carried an unexpected '{FieldName}' field - " +
            "ignored and the session-bound site/patientId used instead (FR-AUTH-3)")]
    public static partial void SuspiciousArgumentOverrideAttempt(ILogger logger, string toolName, string fieldName);

    // The name is the whole content of this line: the counter records that an attempt happened, and
    // only this says what was asked for - which is what tells a model typo apart from record content
    // steering the copilot toward an action. No patient id: this is not an FR-AUTH-4 access-audit
    // event, and it is not routed to audit storage.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Out-of-scope tool call refused: {ToolName} is not offered by the catalog, so it was not " +
            "routed (REQUIREMENTS.md 12.4 NG1) correlation={CorrelationId}")]
    public static partial void OutOfScopeToolCallRefused(ILogger logger, string toolName, string correlationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "get_document_facts was called but no document-facts source is wired; returning no facts")]
    public static partial void DocumentFactsToolUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "retrieve_evidence was called but no evidence retriever is wired; returning no snippets")]
    public static partial void EvidenceToolUnavailable(ILogger logger);
}
