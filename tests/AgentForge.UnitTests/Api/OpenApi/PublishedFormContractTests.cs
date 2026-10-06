using System.Text;
using System.Text.Json;
using AgentForge.Agents;
using AgentForge.Agents.Ingestion;
using AgentForge.Api.Evidence;
using AgentForge.Api.Ingestion;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data.Entities;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp.Authorization;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AgentForge.UnitTests.Api.OpenApi;

/// <summary>
/// The two Week 2 endpoints parse their multipart body by hand, so the request contract the OpenAPI
/// document publishes (<see cref="EvidenceAskForm"/>, <see cref="DocumentIngestForm"/>) is a second
/// statement of the same thing rather than the one the handler binds. NFR-CONTRACT-1 does not allow two
/// statements to drift, and nothing else here would notice: renaming a property on either record changes
/// what the committed spec tells a caller to send and changes nothing about what the handler reads, so the
/// spec would go on validating while every request built from it silently lost a field.
/// </summary>
/// <remarks>
/// Written against the <i>published</i> name - the record's property put through the same camel-case policy
/// the schema generator applies - and then driving the real handler with a form keyed by it. A test that
/// asserted the literal string <c>"question"</c> on both sides would be a third statement, not a guard.
/// </remarks>
public sealed class PublishedFormContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private const string Site = "default";
    private const string SessionPatient = "patient-123";
    private const string ProviderOfRecord = "dr-cardio";

    /// <summary>The wire name the OpenAPI schema advertises for a property of a published form contract.</summary>
    private static string PublishedName(Type contract, string propertyName)
    {
        var property = contract.GetProperty(propertyName);
        property.Should().NotBeNull(
            "{0}.{1} is what the published schema names - a rename here is the drift this fixture exists for",
            contract.Name,
            propertyName);

        return JsonNamingPolicy.CamelCase.ConvertName(property!.Name);
    }

    [Fact]
    public async Task EvidenceAsk_FormKeyedByThePublishedFieldNames_ReachesTheAgentWithEveryValue()
    {
        var supervisor = A.Fake<IEvidenceAgentSupervisor>();
        A.CallTo(() => supervisor.RunAsync(A<EvidenceAgentRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new EvidenceAgentResult
            {
                Answer = "answer",
                SafetyFlags = [],
                SuppressedClaims = [],
                Handoffs = [],
                Evidence = [],
            }));

        var httpContext = AskContext(
            new Dictionary<string, StringValues>(StringComparer.Ordinal)
            {
                [PublishedName(typeof(EvidenceAskForm), nameof(EvidenceAskForm.Question))] = "what changed?",
                [PublishedName(typeof(EvidenceAskForm), nameof(EvidenceAskForm.DocType))] = "lab_pdf",
                [PublishedName(typeof(EvidenceAskForm), nameof(EvidenceAskForm.Context))] = PatientContextBinding.KeyFor(
                    "test-session-id", AskSession()),
            },
            fileFieldName: PublishedName(typeof(EvidenceAskForm), nameof(EvidenceAskForm.File)));

        var result = await EvidenceEndpoints.HandleAskAsync(
            httpContext,
            supervisor,
            StubPatientRelationshipAuthorizer.Related,
            A.Fake<IScopedAccessTokenProvider>(),
            A.Fake<ICorrelationIdAccessor>(),
            NewExpiredSessionSignal(),
            new FixedTimeProvider(Now),
            new CapturingLogger<AccessAudit>(),
            AlwaysGrantingBudget());

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => supervisor.RunAsync(
                A<EvidenceAgentRequest>.That.Matches(r =>
                    r.Question == "what changed?"
                    && r.Document != null
                    && r.Document.DocumentType == ClinicalDocumentType.LabPdf),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DocumentIngest_FormKeyedByThePublishedFieldNames_ReachesTheIngestionServiceWithEveryValue()
    {
        var ingestion = A.Fake<IDocumentIngestionService>();
        A.CallTo(() => ingestion.IngestAsync(A<DocumentIngestionRequest>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new DocumentIngestionResult
            {
                Status = DocumentIngestionStatus.Ingested,
                DocumentReferenceId = "doc-7",
                FactCount = 1,
            }));

        var httpContext = IngestContext(
            new Dictionary<string, StringValues>(StringComparer.Ordinal)
            {
                [PublishedName(typeof(DocumentIngestForm), nameof(DocumentIngestForm.PatientId))] = "patient-9",
                [PublishedName(typeof(DocumentIngestForm), nameof(DocumentIngestForm.DocumentReferenceId))] = "doc-7",
                [PublishedName(typeof(DocumentIngestForm), nameof(DocumentIngestForm.DocType))] = "intake_form",
            },
            fileFieldName: PublishedName(typeof(DocumentIngestForm), nameof(DocumentIngestForm.File)));

        var result = await IngestionEndpoints.HandleIngestAsync(httpContext, ingestion);

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        A.CallTo(() => ingestion.IngestAsync(
                A<DocumentIngestionRequest>.That.Matches(r =>
                    r.PatientId == "patient-9"
                    && r.DocumentReferenceId == "doc-7"
                    && r.DocumentType == ClinicalDocumentType.IntakeForm
                    && r.Content.Length > 0),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    private static DefaultHttpContext AskContext(Dictionary<string, StringValues> form, string fileFieldName)
    {
        var httpContext = new DefaultHttpContext { Session = new InMemoryTestSession() };
        httpContext.Session.SavePatientSession(AskSession());
        return WithMultipartForm(httpContext, form, fileFieldName);
    }

    private static PatientSessionContext AskSession() =>
        new("session-token", Site, SessionPatient, ProviderOfRecord, Now.AddHours(1));

    private static DefaultHttpContext IngestContext(Dictionary<string, StringValues> form, string fileFieldName) =>
        WithMultipartForm(new DefaultHttpContext(), form, fileFieldName);

    private static DefaultHttpContext WithMultipartForm(
        DefaultHttpContext httpContext, Dictionary<string, StringValues> form, string fileFieldName)
    {
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 synthetic");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, fileFieldName, "synthetic.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

        httpContext.Request.ContentType = "multipart/form-data; boundary=----test";
        httpContext.Request.Form = new FormCollection(form, new FormFileCollection { file });
        return httpContext;
    }

    private static ExpiredSessionSignal NewExpiredSessionSignal() =>
        new(A.Fake<IAgentForgeMetrics>(), A.Fake<ICorrelationIdAccessor>(), new CapturingLogger<ExpiredSessionSignal>());

    private static int StatusOf(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: { } status } ? status : StatusCodes.Status200OK;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AgentForge.Api.Chat.IConversationTurnBudget AlwaysGrantingBudget()
    {
        var budget = A.Fake<AgentForge.Api.Chat.IConversationTurnBudget>();
        A.CallTo(() => budget.TryConsume(A<string>._)).Returns(true);
        return budget;
    }
}
