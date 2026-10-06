using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using Microsoft.Extensions.Logging;

namespace AgentForge.Agent;

/// <summary>
/// Executes a tool call the model requested against the real <see cref="IMcpToolServer"/>,
/// forcing <c>site</c>/<c>patientId</c> from the session context on every dispatch regardless of
/// what the call's arguments contain - the enforcement half of
/// FR-CHAT-3's patient-scoping (<see cref="McpToolCatalog"/> is the schema half). <b>Every tool
/// outcome is a returned result, never a throw:</b> a provider requires exactly one tool_result per
/// tool_use before the conversation can continue, so an unknown tool, malformed arguments, or a
/// downstream contract failure all become an error result the model can see and react to, not a
/// crash (NFR-REL-1 - one tool failing degrades one step, not the whole turn). <b>What escapes is
/// not a tool outcome:</b> the FR-AUTH-2 lookup runs before that try and since a separate change lets
/// <see cref="AccessTokenExpiredException"/> through, so a dispatch on a dead SMART session ends
/// the turn instead of returning a tool_result - there is no chart left to degrade to and the
/// remedy is a re-launch, not a retry. <c>ChatHub</c> turns it into the session-expired message
/// the clinician can act on. A separate change
/// </summary>
/// <remarks>
/// <para>
/// This is also where FR-AUTH-2 is enforced, for the same reason FR-CHAT-3 is: it is the last
/// point below the model at which <c>site</c>/<c>patientId</c> are known to be the session's own
/// and not something the conversation supplied. Every dispatch is checked - not only the six FHIR
/// tools behind <see cref="IMcpToolServer"/> - because a session whose patient the requester is
/// not entitled to has no business running any tool at all. FR-AUTH-3 requires this to live here
/// rather than in prompt text, so no phrasing, injected document content or "ignore that" can
/// reach around it.
/// </para>
/// <para>
/// And it is where <c>REQUIREMENTS.md</c> §12.4's NG1 scope guardrail stops being prompt text. The copilot
/// "does not diagnose, recommend treatment, or place orders"; the first two verbs are properties of
/// prose and are asked for in <see cref="CardiologyProfile"/>, but the third is decidable here,
/// because placing an order is something the model would have to <i>call</i>. A tool call naming
/// anything <see cref="McpToolCatalog"/> does not offer is refused before it is routed, so what the
/// model is told it may do and what it may actually do are one list. The two questions are asked in
/// that order deliberately: entitlement is the outer one, so every dispatch still produces an
/// FR-AUTH-2 decision. A separate change.
/// </para>
/// </remarks>
public sealed class McpToolDispatcher(
    IMcpToolServer toolServer,
    IPatientRelationshipAuthorizer relationshipAuthorizer,
    IClinicianIdentityAccessor clinicianIdentityAccessor,
    ICorrelationIdAccessor correlationIdAccessor,
    IAgentForgeMetrics metrics,
    ILogger<McpToolDispatcher> logger,
    ILogger<AccessAudit> auditLogger,
    IDocumentFactsTool? documentFactsTool = null,
    IEvidenceTool? evidenceTool = null) : IMcpToolDispatcher
{
    private static readonly JsonSerializerOptions ArgumentsJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Field names McpToolCatalog's schemas never offer the model - if one shows up in real
    /// arguments anyway, it is ignored (enforced elsewhere in this class) but the attempt itself
    /// is worth recording (FR-AUTH-3: "confirmed... and get logged," not just confirmed harmless).
    /// </summary>
    private static readonly string[] UnexpectedArgumentFields = ["patientid", "patient_id", "site"];

    /// <inheritdoc />
    public async Task<LlmToolResultContent> DispatchAsync(
        string site, string patientId, LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        DetectSuspiciousArgumentOverride(toolCall);

        // FR-AUTH-2, before anything is fetched: the session's patient is settled by now, so this
        // asks the only question left - may the signed-in requester see that patient at all? A
        // refusal is deliberately not counted as a tool failure: it is the system working, and
        // routing it into the tool-failure rate would page an operator for correct behavior
        // (FR-OBS-4).
        var clinicianIdentity = clinicianIdentityAccessor.ClinicianIdentity;
        var decision = await relationshipAuthorizer
            .AuthorizeAsync(site, clinicianIdentity, patientId, cancellationToken).ConfigureAwait(false);

        // Both branches, above the stopwatch and in a series of its own, so M3's live story exists
        // without the refusal ever touching the tool-call rate. One call rather than two so the
        // permit and refuse legs cannot drift apart. The reason is a bounded label, not the audit
        // line's prose - it is exported. An expired session throws past this line and is counted as
        // neither, which is correct: it is not a decision. It is not uncounted, though -
        // it lands on agentforge.expired_session_refusals where ChatHub answers it, so re-wrapping
        // this await to "fix" the gap would only re-describe expiry as an entitlement refusal.
        metrics.RecordAuthorizationDecision(decision.IsRelated, AuthorizationDecisionReason.For(decision));

        if (!decision.IsRelated)
        {
            AccessAuditLog.RecordRefusal(
                auditLogger,
                clinicianIdentity ?? "unknown",
                patientId,
                toolCall.ToolName,
                PatientAccessRefusal.AuditReason,
                decision.AuditCount,
                correlationIdAccessor.CorrelationId);

            return new LlmToolResultContent(
                toolCall.Id, SerializeError(PatientAccessRefusal.UserFacingMessage), IsError: true);
        }

        // REQUIREMENTS.md §12.4's NG1 scope guardrail, below the model: the copilot may run what it offers and
        // nothing else. Taken from McpToolCatalog rather than from ExecuteAsync's router, so a tool
        // wired into the switch and advertised nowhere is inert instead of callable - and so a tool
        // that changes the record cannot become reachable without someone adding it to the advertised
        // surface. Deliberately after the FR-AUTH-2 gate: "may this requester see this patient" is the
        // outer question, and every dispatch still produces a decision. A separate change
        if (!McpToolCatalog.Offers(toolCall.ToolName))
        {
            metrics.RecordOutOfScopeToolCall();
            McpToolDispatcherLog.OutOfScopeToolCallRefused(
                logger, toolCall.ToolName, correlationIdAccessor.CorrelationId);

            return new LlmToolResultContent(
                toolCall.Id, SerializeError(McpToolCatalog.OutOfScopeRefusalMessage), IsError: true);
        }

        using var activity = AgentForgeActivitySource.Instance.StartActivity($"tool.{toolCall.ToolName}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resultJson = await ExecuteAsync(site, patientId, toolCall, cancellationToken).ConfigureAwait(false);
            metrics.RecordToolCall(toolCall.ToolName, succeeded: true, stopwatch.Elapsed);
            return new LlmToolResultContent(toolCall.Id, resultJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            metrics.RecordToolCall(toolCall.ToolName, succeeded: false, stopwatch.Elapsed);
            // An upstream FHIR/HTTP failure (e.g. a 403 for a resource the token can't read) reads as
            // a clean "unavailable", not raw "Response status code..." text in the brief - the agent
            // still sees IsError:true and won't fabricate (UC-5). A separate change
            var message = ex is HttpRequestException
                ? "This clinical data source is temporarily unavailable and could not be retrieved."
                : ex.Message;
            return new LlmToolResultContent(toolCall.Id, SerializeError(message), IsError: true);
        }
    }

    private void DetectSuspiciousArgumentOverride(LlmToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(toolCall.ArgumentsJson);
        }
        catch (JsonException)
        {
            // Malformed JSON is handled as a contract failure by the normal dispatch path - nothing to detect here.
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (UnexpectedArgumentFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    McpToolDispatcherLog.SuspiciousArgumentOverrideAttempt(logger, toolCall.ToolName, property.Name);
                }
            }
        }
    }

    private Task<string> ExecuteAsync(string site, string patientId, LlmToolCall call, CancellationToken cancellationToken) =>
        call.ToolName switch
        {
            "get_patient_summary" => ExecuteGetPatientSummaryAsync(site, patientId, cancellationToken),
            "get_interval_changes" => ExecuteGetIntervalChangesAsync(site, patientId, call.ArgumentsJson, cancellationToken),
            "get_labs" => ExecuteGetLabsAsync(site, patientId, call.ArgumentsJson, cancellationToken),
            "get_vitals" => ExecuteGetVitalsAsync(site, patientId, call.ArgumentsJson, cancellationToken),
            "get_recent_encounters" => ExecuteGetRecentEncountersAsync(site, patientId, call.ArgumentsJson, cancellationToken),
            "get_documents" => ExecuteGetDocumentsAsync(site, patientId, call.ArgumentsJson, cancellationToken),
            "get_document_facts" => ExecuteGetDocumentFactsAsync(patientId, cancellationToken),
            "retrieve_evidence" => ExecuteRetrieveEvidenceAsync(call.ArgumentsJson, cancellationToken),
            // Unreachable from DispatchAsync while this switch and McpToolCatalog agree, and kept as
            // the backstop for the case where they do not: a name the catalog offers and this router
            // cannot serve reaches here rather than silently returning nothing.
            _ => throw new McpToolContractException(call.ToolName, [$"Unknown tool '{call.ToolName}'."]),
        };

    private async Task<string> ExecuteGetPatientSummaryAsync(string site, string patientId, CancellationToken cancellationToken)
    {
        var result = await toolServer.GetPatientSummaryAsync(
            new GetPatientSummaryRequest { Site = site, PatientId = patientId }, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetIntervalChangesAsync(
        string site, string patientId, string argumentsJson, CancellationToken cancellationToken)
    {
        var args = ParseArguments<IntervalChangesArguments>(argumentsJson);
        var sinceDate = args?.SinceDate
            ?? throw new McpToolContractException("get_interval_changes", ["since_date is required."]);

        var result = await toolServer.GetIntervalChangesAsync(
            new GetIntervalChangesRequest { Site = site, PatientId = patientId, SinceDate = sinceDate }, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetLabsAsync(
        string site, string patientId, string argumentsJson, CancellationToken cancellationToken)
    {
        var args = ParseArguments<DateFilterArguments>(argumentsJson);
        var result = await toolServer.GetLabsAsync(
            new GetLabsRequest { Site = site, PatientId = patientId, SinceDate = args?.SinceDate }, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetVitalsAsync(
        string site, string patientId, string argumentsJson, CancellationToken cancellationToken)
    {
        var args = ParseArguments<DateFilterArguments>(argumentsJson);
        var result = await toolServer.GetVitalsAsync(
            new GetVitalsRequest { Site = site, PatientId = patientId, SinceDate = args?.SinceDate }, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetRecentEncountersAsync(
        string site, string patientId, string argumentsJson, CancellationToken cancellationToken)
    {
        var args = ParseArguments<RecentEncountersArguments>(argumentsJson);
        var request = new GetRecentEncountersRequest { Site = site, PatientId = patientId };
        if (args?.Count is { } count)
        {
            request = request with { Count = count };
        }

        var result = await toolServer.GetRecentEncountersAsync(request, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetDocumentsAsync(
        string site, string patientId, string argumentsJson, CancellationToken cancellationToken)
    {
        var args = ParseArguments<DocumentsArguments>(argumentsJson);
        var result = await toolServer.GetDocumentsAsync(
            new GetDocumentsRequest { Site = site, PatientId = patientId, DocumentType = args?.DocumentType }, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteGetDocumentFactsAsync(string patientId, CancellationToken cancellationToken)
    {
        if (documentFactsTool is null)
        {
            // No document store wired (e.g. a FHIR-only test host): honest empty, not a throw.
            McpToolDispatcherLog.DocumentFactsToolUnavailable(logger);
            return Serialize(new DocumentFactsResult([]));
        }

        var result = await documentFactsTool.GetAsync(patientId, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    private async Task<string> ExecuteRetrieveEvidenceAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var query = ParseArguments<EvidenceArguments>(argumentsJson)?.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpToolContractException("retrieve_evidence", ["query is required."]);
        }

        if (evidenceTool is null)
        {
            // No retriever wired (e.g. a FHIR-only test host): honest empty, not a throw.
            McpToolDispatcherLog.EvidenceToolUnavailable(logger);
            return Serialize(new EvidenceResult([]));
        }

        var result = await evidenceTool.GetAsync(query, cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    private static T? ParseArguments<T>(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Trim() == "{}")
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(argumentsJson, ArgumentsJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new McpToolContractException("(argument parsing)", [$"Arguments are not valid JSON: {ex.Message}"]);
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ResultJsonOptions);

    private static string SerializeError(string message) => JsonSerializer.Serialize(new { error = message });

    private sealed record IntervalChangesArguments([property: JsonPropertyName("since_date")] string? SinceDate);

    private sealed record DateFilterArguments([property: JsonPropertyName("since_date")] string? SinceDate);

    private sealed record RecentEncountersArguments([property: JsonPropertyName("count")] int? Count);

    private sealed record DocumentsArguments([property: JsonPropertyName("document_type")] string? DocumentType);

    private sealed record EvidenceArguments([property: JsonPropertyName("query")] string? Query);
}
