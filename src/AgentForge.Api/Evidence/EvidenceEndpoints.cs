using System.ComponentModel;
using AgentForge.Agents;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentForge.Api.Evidence;


/// <summary>
/// The Week 2 multimodal-evidence endpoint: upload a clinical document + ask a question, run the
/// supervisor/worker graph, and return a grounded, verified answer with its handoff trace and evidence
/// (ARCHITECTURE-DOCUMENTS.md §6). It extracts from the uploaded document and retrieves from the guideline corpus,
/// and it is gated by the BFF session like the rest of the launched app (401 without a launch), not left
/// open. This is what removes its earlier outlier status (a public, unauthenticated endpoint).
/// <para>
/// It also answers over the patient's <em>ingested document facts</em>, so it is an FR-AUTH-2 choke point in
/// its own right (<c>ARCHITECTURE.md</c> §5.7) and makes one user-scoped FHIR call of its own to decide that.
/// The patient comes from the session, never from the request.
/// </para>
/// </summary>
public static class EvidenceEndpoints
{
    /// <summary>The access-audit trail's tool name for <c>POST /evidence/ask</c> (FR-AUTH-4).</summary>
    public const string AskAuditToolName = "evidence_ask";

    /// <summary>The access-audit trail's tool name for <c>GET /evidence/document/{id}</c> (FR-AUTH-4).</summary>
    public const string DocumentAuditToolName = "evidence_document";

    /// <summary>The <c>429</c> body of <c>POST /evidence/ask</c> once the session's LLM turn budget is spent.</summary>
    public const string BudgetExhaustedMessage =
        "This session has reached its turn limit - no further AI answers until its budget resets.";

    // The patient field of a refusal whose document the ingest index cannot attribute to anyone.
    private const string UnresolvedDocumentOwner = "unresolved";

    /// <summary>Maps the Week 2 evidence endpoints: <c>POST /evidence/ask</c> and the source-document fetch.</summary>
    public static IEndpointRouteBuilder MapEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/evidence/ask", HandleAskAsync)
            .DisableAntiforgery()
            .WithName("EvidenceAsk")
            // No .WithSummary here, deliberately. HandleAskAsync is internal, so the XML-doc source generator
            // sees it and its <summary> is what the document publishes; a .WithSummary beside it would be a
            // second spelling of the same string that never reaches the document, for someone to edit later
            // and wonder why nothing changed. Accessibility is what decides which source wins - see the
            // private handler below. A separate change review
            .Accepts<EvidenceAskForm>("multipart/form-data")
            .Produces<EvidenceResponsePayload>(StatusCodes.Status200OK)
            .Produces<string>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<string>(StatusCodes.Status409Conflict)
            .Produces<string>(StatusCodes.Status429TooManyRequests);

        endpoints.MapGet("/evidence/document/{documentId}", HandleGetDocumentAsync)
            .WithName("EvidenceSourceDocument")
            // Load-bearing, unlike the one removed above: HandleGetDocumentAsync is private, so the XML-doc
            // source generator does not see it and its <summary> reaches nothing. Verified by removing this
            // line and regenerating - the operation came out with no summary at all. A separate change review
            .WithSummary("Stream a cited source document's bytes for the click-to-source overlay.")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<string>(StatusCodes.Status409Conflict);

        return endpoints;
    }

    /// <summary>
    /// <c>GET /evidence/document/{documentId}</c> — streams a source document's bytes for the click-to-source
    /// overlay's production path (FR-CITE-2), fetched as the launched clinician. <c>context</c> is the
    /// <c>contextKey</c> <c>GET /patient</c> gave the page; a key for another patient is answered 409.
    /// </summary>
    private static Task<IResult> HandleGetDocumentAsync(
        HttpContext httpContext, string documentId,
        [Description("The contextKey GET /patient returned when the page was rendered. A key for another patient than the session's current one is answered 409.")]
        string? context,
        IOpenEmrFhirClient fhirClient, IDerivedFactStore store, IPatientRelationshipAuthorizer relationshipAuthorizer,
        IScopedAccessTokenProvider tokenProvider, ICorrelationIdAccessor correlationIdAccessor,
        ExpiredSessionSignal expiredSession, TimeProvider timeProvider, ILogger<AccessAudit> auditLogger) =>
        GetDocumentAsync(
            httpContext, documentId, context, fhirClient, store, relationshipAuthorizer, tokenProvider, correlationIdAccessor,
            expiredSession, timeProvider, auditLogger);

    /// <summary>
    /// The source-document fetch behind <see cref="HandleGetDocumentAsync"/>. The document's patient is resolved
    /// from the sidecar's own ingest index, never from the token: a <c>user/Binary.read</c> token is not confined
    /// to one patient. A document that is not the session patient's, or whose owner the index cannot name, is
    /// refused with 403 before any <c>Binary</c> read; so is a requester whose FR-AUTH-2 relationship to the
    /// session patient no longer holds. Every decision writes one access-audit record (FR-AUTH-4), granted or
    /// refused, naming the clinician, the patient and the correlation id and never the document id. A page rendered
    /// for another patient than the session's current one is answered 409 before any of that, and audited as
    /// nothing: it asked about no patient's data. A separate change
    /// </summary>
    // Separate from the private handler so the unit tests can drive it without publishing its summary in the
    // OpenAPI document (see the .WithSummary note in MapEvidenceEndpoints). A separate change
    internal static async Task<IResult> GetDocumentAsync(
        HttpContext httpContext, string documentId, string? contextKey,
        IOpenEmrFhirClient fhirClient, IDerivedFactStore store, IPatientRelationshipAuthorizer relationshipAuthorizer,
        IScopedAccessTokenProvider tokenProvider, ICorrelationIdAccessor correlationIdAccessor,
        ExpiredSessionSignal expiredSession, TimeProvider timeProvider, ILogger<AccessAudit> auditLogger)
    {
        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        // An aged-out session is refused here, by the read itself - see SessionExtensions.
        if (httpContext.Session.TryGetPatientSession(timeProvider) is not { } session)
        {
            return Results.Unauthorized();
        }

        if (!PatientContextBinding.Matches(contextKey, httpContext.Session.Id, session))
        {
            return PatientChanged();
        }

        var correlationId = correlationIdAccessor.CorrelationId;
        var owner = await store.FindPatientIdByDocumentReferenceIdAsync(documentId, httpContext.RequestAborted).ConfigureAwait(false);
        if (!string.Equals(owner, session.PatientId, StringComparison.Ordinal))
        {
            // No relationship lookup is made, so the clinic-day count is the unresolved one.
            AccessAuditLog.RecordRefusal(
                auditLogger,
                session.ClinicianIdentity,
                owner ?? UnresolvedDocumentOwner,
                DocumentAuditToolName,
                PatientAccessRefusal.DocumentOutsideSessionPatientAuditReason,
                PatientRelationshipDecision.Unresolved.AuditCount,
                correlationId);
            return Refused();
        }

        // Set the AsyncLocal token once; the FHIR client's auth handler reads it back for both calls below.
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);
        BinaryDocument? document;
        try
        {
            var decision = await relationshipAuthorizer
                .AuthorizeAsync(session.Site, session.ClinicianIdentity, session.PatientId, httpContext.RequestAborted)
                .ConfigureAwait(false);
            if (!decision.IsRelated)
            {
                AccessAuditLog.RecordRefusal(
                    auditLogger,
                    session.ClinicianIdentity,
                    session.PatientId,
                    DocumentAuditToolName,
                    PatientAccessRefusal.AuditReason,
                    decision.AuditCount,
                    correlationId);
                return Refused();
            }

            AccessAuditLog.RecordAccess(
                auditLogger, session.ClinicianIdentity, session.PatientId, DocumentAuditToolName, correlationId);
            document = await fhirClient.GetBinaryAsync(session.Site, documentId, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (AccessTokenExpiredException)
        {
            // Live when the session was read, dead by the time a FHIR call went out. Same answer as an
            // already-expired session, because that is what this is.
            expiredSession.Record(ExpiredSessionSurface.EvidenceDocument, httpContext.Session.Id);
            return Results.Unauthorized();
        }

        // Never document.ContentType: that is the upload's declared type, and echoing it served HTML/SVG as
        // live markup on this origin. A separate change
        return document is null
            ? Results.NotFound()
            : SourceDocumentResponse.Create(httpContext.Response, document.Content);
    }

    private static IResult Refused() =>
        Results.Json(new { error = PatientAccessRefusal.UserFacingMessage }, statusCode: StatusCodes.Status403Forbidden);

    // The page matches on 409 and reloads, so its banner and its answers show the session's patient again.
    private static IResult PatientChanged() =>
        Results.Json(ChatHub.PatientChangedMessage, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Runs the supervisor/worker graph over the session's patient and returns a verified, cited answer
    /// with its handoff trace, the guideline evidence it retrieved, and the click-to-source citations for
    /// any document-derived fact.
    /// </summary>
    /// <remarks>
    /// The patient is the session's and the form carries no patient field; a clinician with no clinical
    /// relationship to that patient is refused with 403 (FR-AUTH-2). The form's <c>context</c> says which patient
    /// the asking page shows: a request from a page rendered for another patient than the session's current one
    /// is refused with 409 and costs nothing, so a stale page is never answered about a patient it does not show.
    /// </remarks>
    // Internal, not private, so EvidenceEndpointsAuthorizationTests can drive FR-AUTH-2's third choke point
    // directly - the gate below is the only thing between a launched session and another patient's document
    // facts. Said here rather than in the doc comment, which is published in the OpenAPI document.
    internal static async Task<IResult> HandleAskAsync(
        HttpContext httpContext,
        IEvidenceAgentSupervisor supervisor,
        IPatientRelationshipAuthorizer relationshipAuthorizer,
        IScopedAccessTokenProvider tokenProvider,
        ICorrelationIdAccessor correlationIdAccessor,
        ExpiredSessionSignal expiredSession,
        TimeProvider timeProvider,
        ILogger<AccessAudit> auditLogger,
        IConversationTurnBudget turnBudget)
    {
        // The request's application root span (NFR-TRACE-W2): this route bypasses AgentOrchestrator, so nothing
        // else opens one. It nests under the host's HTTP server span and carries the correlation id every log
        // line of the request is scoped to - never the patient, the requester or the question. A separate change
        using var span = AgentForgeActivitySource.Instance.StartActivity(EvidenceTracing.AskSpan);
        span?.SetTag(EvidenceTracing.CorrelationId, correlationIdAccessor.CorrelationId);
        try
        {
            var result = await AskAsync(
                httpContext, supervisor, relationshipAuthorizer, tokenProvider, correlationIdAccessor,
                expiredSession, timeProvider, auditLogger, turnBudget).ConfigureAwait(false);
            span?.SetTag(EvidenceTracing.Outcome, OutcomeOf(result));
            return result;
        }
        catch (Exception ex)
        {
            EvidenceTracing.RecordFailure(span, ex);
            throw;
        }
    }

    private static string OutcomeOf(IResult result) =>
        (result as IStatusCodeHttpResult)?.StatusCode switch
        {
            StatusCodes.Status200OK => "answered",
            StatusCodes.Status400BadRequest => "bad_request",
            StatusCodes.Status401Unauthorized => "unauthorized",
            StatusCodes.Status403Forbidden => "forbidden",
            StatusCodes.Status409Conflict => "patient_changed",
            StatusCodes.Status429TooManyRequests => "budget_exhausted",
            _ => "other",
        };

    private static async Task<IResult> AskAsync(
        HttpContext httpContext,
        IEvidenceAgentSupervisor supervisor,
        IPatientRelationshipAuthorizer relationshipAuthorizer,
        IScopedAccessTokenProvider tokenProvider,
        ICorrelationIdAccessor correlationIdAccessor,
        ExpiredSessionSignal expiredSession,
        TimeProvider timeProvider,
        ILogger<AccessAudit> auditLogger,
        IConversationTurnBudget turnBudget)
    {
        // Gate on the BFF session, same as /agenda and /patient: the endpoint burns LLM + retrieval quota, so
        // access required a launch even before the flow made a user-scoped FHIR call of its own (#96, #105).
        // The FR-AUTH-2 lookup below is that call, so the session is the token source now as well as the
        // paywall. The read also refuses an aged-out session, so an expired token normally never reaches
        // that lookup; one that dies in between is caught there and answered 401.
        await httpContext.Session.LoadAsync(httpContext.RequestAborted).ConfigureAwait(false);
        if (httpContext.Session.TryGetPatientSession(timeProvider) is not { } session)
        {
            return Results.Unauthorized();
        }

        var request = httpContext.Request;
        if (!request.HasFormContentType)
        {
            return Results.BadRequest("Expected multipart/form-data with a 'question' and an optional 'file'.");
        }

        var form = await request.ReadFormAsync(httpContext.RequestAborted).ConfigureAwait(false);

        var question = form["question"].ToString();
        if (string.IsNullOrWhiteSpace(question))
        {
            return Results.BadRequest("'question' is required.");
        }

        // The session id outlives a patient switch in another tab, so the session alone does not say which patient
        // this page shows. Refused before the relationship lookup, the audit and the budget. A separate change
        if (!PatientContextBinding.Matches(form[PatientContextBinding.QueryParameter].ToString(), httpContext.Session.Id, session))
        {
            return PatientChanged();
        }

        // FR-AUTH-2, third choke point (ARCHITECTURE.md §5.7). Two separate things are wrong with
        // trusting the form's `patientId`, and both are fixed by not reading it: it let a launched
        // session name *another* patient and receive their ingested document facts, quotes and page
        // citations, and even the session's own patient is only as current as
        // the launch, so the relationship is re-checked here the way McpToolDispatcher re-checks it
        // on every dispatch. The patient is the session's, full stop - there is no request field
        // left to point somewhere else.
        var patientId = session.PatientId;
        // The relationship lookup is a FHIR read, so it runs as the launched clinician - the same
        // AsyncLocal hand-off HandleGetDocumentAsync makes. Without it the call is unauthenticated,
        // the lookup throws, and the gate fails closed on everyone.
        tokenProvider.Adopt(session.AccessToken, session.ExpiresAt);
        PatientRelationshipDecision decision;
        try
        {
            decision = await relationshipAuthorizer
                .AuthorizeAsync(session.Site, session.ClinicianIdentity, patientId, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (AccessTokenExpiredException)
        {
            // Expired between the session read and this lookup. Not an entitlement decision, so it is
            // neither a refusal nor an audit event - the session is simply no longer authenticated.
            // It is counted, on its own series, because otherwise nothing here says "expiry" at all.
            expiredSession.Record(ExpiredSessionSurface.EvidenceAsk, httpContext.Session.Id);
            return Results.Unauthorized();
        }

        if (!decision.IsRelated)
        {
            AccessAuditLog.RecordRefusal(
                auditLogger,
                session.ClinicianIdentity,
                patientId,
                AskAuditToolName,
                PatientAccessRefusal.AuditReason,
                decision.AuditCount,
                correlationIdAccessor.CorrelationId);

            return Refused();
        }

        PendingDocument? document = null;
        var file = form.Files["file"];
        if (file is { Length: > 0 })
        {
            if (!TryParseDocType(form["docType"].ToString(), out var documentType))
            {
                return Results.BadRequest("'docType' must be 'lab_pdf' or 'intake_form' when a file is attached.");
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, httpContext.RequestAborted).ConfigureAwait(false);
            document = new PendingDocument(documentType, buffer.ToArray(), file.ContentType);
        }

        // Charged last, once nothing else can refuse it, so a 400 or 403 spends no budget. The edge limits this
        // route per minute, never in total; this is the total. A separate change
        if (!turnBudget.TryConsume(httpContext.Session.Id))
        {
            return Results.Json(BudgetExhaustedMessage, statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Recorded once the request is known to run, so a 400 on the attachment is not audited as a read.
        AccessAuditLog.RecordAccess(
            auditLogger, session.ClinicianIdentity, patientId, AskAuditToolName, correlationIdAccessor.CorrelationId);
        var agentRequest = new EvidenceAgentRequest { PatientId = patientId, Question = question, Document = document };
        var result = await supervisor.RunAsync(agentRequest, httpContext.RequestAborted).ConfigureAwait(false);
        return Results.Ok(ToPayload(result));
    }

    private static bool TryParseDocType(string value, out ClinicalDocumentType documentType)
    {
        switch (value)
        {
            case "lab_pdf":
                documentType = ClinicalDocumentType.LabPdf;
                return true;
            case "intake_form":
                documentType = ClinicalDocumentType.IntakeForm;
                return true;
            default:
                documentType = default;
                return false;
        }
    }

    private static EvidenceResponsePayload ToPayload(EvidenceAgentResult result) => new(
        result.Answer,
        [.. result.Handoffs.Select(h => new HandoffPayload(h.From, h.To, h.Reason))],
        [.. result.Evidence.Select(e => new EvidencePayload(e.DocumentId, e.Section, e.ChunkId, e.Text, e.Score))],
        result.SafetyFlags.Count,
        result.SuppressedClaims.Count,
        result.ExtractedFactsJson,
        result.DocumentCitations);
}
