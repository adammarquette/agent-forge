using System.Text;
using AgentForge.Api.Evidence;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests.Api.Evidence;

/// <summary>
/// XSS at <c>GET /evidence/document/{id}</c>: the response echoed the <c>Binary</c>'s declared media type and
/// sent no <c>nosniff</c> or CSP, so an HTML or SVG file uploaded as a clinical document ran its script on the
/// sidecar's origin when opened. The type must come from the bytes, and the document must be inert whatever
/// they are. A separate change
/// </summary>
public sealed class EvidenceDocumentResponseHeadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private const string Document = "docref-own";
    private const string Patient = "patient-123";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%synthetic\n");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Gif = Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00");
    private static readonly byte[] Webp = [.. Encoding.ASCII.GetBytes("RIFF"), 0x24, 0x00, 0x00, 0x00, .. Encoding.ASCII.GetBytes("WEBPVP8 ")];
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><body><script>alert(document.cookie)</script></body></html>");
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(document.cookie)\"><script>alert(1)</script></svg>");

    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly IDerivedFactStore _store = A.Fake<IDerivedFactStore>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();

    public EvidenceDocumentResponseHeadersTests()
    {
        A.CallTo(() => _store.FindPatientIdByDocumentReferenceIdAsync(Document, A<CancellationToken>._))
            .Returns(Task.FromResult<string?>(Patient));
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-9");
    }

    public static TheoryData<string, string> ScriptPayloads => new()
    {
        { "html", "text/html" },
        { "svg", "image/svg+xml" },
        { "html", "application/pdf" },
        { "svg", "image/png" },
    };

    [Theory]
    [MemberData(nameof(ScriptPayloads))]
    public async Task GetDocumentAsync_ScriptBearingPayload_IsServedInertWhateverItsDeclaredType(string payload, string declared)
    {
        // Given an HTML or SVG file stored as a clinical document, under any declared type
        // When the click-to-source fetch serves it
        // Then no browser will render it as markup or run its script: an opaque download type, forced to a
        // download, with sniffing off and a sandboxing CSP behind both. Each of the four is load-bearing alone.
        var response = await Serve(payload == "html" ? Html : Svg, declared);

        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        response.ContentType.Should().Be("application/octet-stream");
        response.Headers.ContentDisposition.ToString().Should().StartWith("attachment");
        response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        AssertSandboxedCsp(response);
    }

    public static TheoryData<string, string> RenderableDocuments => new()
    {
        { "pdf", "application/pdf" },
        { "png", "image/png" },
        { "jpeg", "image/jpeg" },
        { "gif", "image/gif" },
        { "webp", "image/webp" },
    };

    [Theory]
    [MemberData(nameof(RenderableDocuments))]
    public async Task GetDocumentAsync_PdfOrImage_IsPinnedToTheTypeItsBytesProveEvenWhenDeclaredAsHtml(string kind, string expected)
    {
        // The declared type is upload metadata; only the file signature is trusted.
        var response = await Serve(BytesOf(kind), "text/html");

        response.ContentType.Should().Be(expected);
        response.Headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        AssertSandboxedCsp(response);
    }

    [Fact]
    public async Task GetDocumentAsync_Pdf_IsServedInlineWithItsBytesIntact()
    {
        // The overlay fetches the bytes and renders them with pdf.js; the headers must not change what it reads.
        var response = await Serve(Pdf, "application/pdf");

        response.Headers.ContentDisposition.ToString().Should().NotStartWith("attachment");
        ((MemoryStream)response.Body).ToArray().Should().Equal(Pdf);
    }

    private static void AssertSandboxedCsp(HttpResponse response)
    {
        var directives = response.Headers.ContentSecurityPolicy.ToString()
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        directives.Should().Contain("sandbox").And.Contain("default-src 'none'");
    }

    private static byte[] BytesOf(string kind) => kind switch
    {
        "pdf" => Pdf,
        "png" => Png,
        "jpeg" => Jpeg,
        "gif" => Gif,
        "webp" => Webp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private async Task<HttpResponse> Serve(byte[] content, string declaredType)
    {
        A.CallTo(() => _fhirClient.GetBinaryAsync(A<string>._, Document, A<CancellationToken>._))
            .Returns(Task.FromResult<BinaryDocument?>(new BinaryDocument(content, declaredType)));

        var httpContext = new DefaultHttpContext
        {
            Session = new InMemoryTestSession(),
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        httpContext.Response.Body = new MemoryStream();
        var session = new PatientSessionContext("session-token", "default", Patient, "dr-cardio", Now.AddHours(1));
        httpContext.Session.SavePatientSession(session);
        var expiredSession = new ExpiredSessionSignal(
            A.Fake<IAgentForgeMetrics>(), _correlationIdAccessor, new CapturingLogger<ExpiredSessionSignal>());

        var result = await EvidenceEndpoints.GetDocumentAsync(
            httpContext, Document, PatientContextBinding.KeyFor(httpContext.Session.Id, session), _fhirClient, _store, StubPatientRelationshipAuthorizer.Related,
            A.Fake<IScopedAccessTokenProvider>(), _correlationIdAccessor, expiredSession,
            new FixedTimeProvider(Now), new CapturingLogger<AccessAudit>());
        await result.ExecuteAsync(httpContext);
        return httpContext.Response;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
