using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentForge.Api.Chat;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Retrieval;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>A patient-data path the host-level scan drives end to end.</summary>
public enum ScanPath
{
    /// <summary>SMART launch, then a pre-visit brief over the chat hub (UC-1).</summary>
    Brief,

    /// <summary>Agenda launch, the roster with its per-patient summaries, then the drill-down (UC-6).</summary>
    Agenda,

    /// <summary><c>POST /documents/ingest</c>, the front desk's upload handed on by OpenEMR (UC-9).</summary>
    Ingestion,

    /// <summary>SMART launch, a prior ingestion, then <c>POST /evidence/ask</c> with a document attached (UC-10).</summary>
    EvidenceAsk,

    /// <summary>SMART launch, a prior ingestion, then <c>GET /evidence/document/{documentId}</c>, the click-to-source fetch (FR-CITE-2).</summary>
    EvidenceDocument,
}

/// <summary>
/// The host-level half of the no-PHI-in-logs gate (NFR-SEC-1, CONVENTIONS.md §7). The eval rubric
/// builds its pipeline by hand, so it never sees what production stdout actually carries: the shipped
/// <c>appsettings.json</c> levels, logging scopes, the OpenTelemetry console exporter's formatting or a rendered
/// exception. This boots the real <c>Program</c> under Production with that file and Week 2 wired, drives one
/// patient-data path for a synthetic patient, and scans everything both console sinks wrote for the patient id,
/// the clinician id, the patient's name and the document ids the run touches (<see cref="StdoutPhiScan"/>). Only OpenEMR and the LLM provider are
/// faked, at the HTTP transport, plus the two things no in-process host can have: the database process (EF Core's
/// in-memory provider, migrations skipped) and the SQL half of retrieval. Every sidecar logger, handler and
/// resilience pipeline between them is the production one.
/// <para>
/// Unit tier (in-memory host, no network, no database process, Coding Agent), because it needs no deployed
/// dependency and must run on every change; <c>CONVENTIONS.md</c> §8.1 records the choice.
/// </para>
/// </summary>
[Collection(nameof(HostStdoutPhiScanTests))]
public sealed class HostStdoutPhiScanTests : IDisposable
{
    // Synthetic ids, unique to this test so no other line in the process can carry them by accident.
    private const string PatientId = FakeUpstream.PatientId;
    private const string ClinicianId = FakeUpstream.ClinicianId;
    private const string GivenName = FakeUpstream.GivenName;
    private const string FamilyName = FakeUpstream.FamilyName;
    private const string OpenEmrHost = FakeUpstream.OpenEmrHost;
    private const string AgendaClientId = FakeUpstream.AgendaClientId;
    private const string DocumentReferenceId = "phi-scan-docref-9d3a";

    // The request path carried the fetched document's id onto every record of a fetch until it was scrubbed.
    // The fetch names an ingested DocumentReference id, since the endpoint refuses any other. Separate changes
    private static readonly ScanIdentifiers Ids = new(PatientId, ClinicianId, FamilyName, [DocumentReferenceId]);

    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StringWriter _stdout = new(CultureInfo.InvariantCulture);
    private readonly FakeUpstream _upstream = new();
    private readonly WebApplicationFactory<Program> _factory;
    private bool _hostStopped;

    public HostStdoutPhiScanTests()
    {
        // Before the host exists: the console logger provider captures Console.Out when it is constructed.
        // One synchronized writer for both streams, so the two sinks' threads cannot interleave inside a write.
        var console = TextWriter.Synchronized(_stdout);
        Console.SetOut(console);
        Console.SetError(console);

        var databaseName = $"host-stdout-phi-scan-{Guid.NewGuid():N}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = $"https://{OpenEmrHost}",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "host-stdout-phi-scan",
                ["OpenEmr:Scopes:0"] = "launch",
                ["OpenEmrAgenda:ClientId"] = AgendaClientId,
                ["OpenEmrAgenda:Scopes:0"] = "user/Appointment.read",
                ["Bff:PublicBaseUrl"] = "https://bff.host-stdout-phi-scan.invalid",
                ["Llm:ApiKey"] = "host-stdout-phi-scan",
                ["Llm:Model"] = "host-stdout-phi-scan",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                // Wires Week 2 as production does; the connection is replaced below and never opened.
                ["AgentForgeData:ConnectionString"] = "Host=agentforge-db.host-stdout-phi-scan.invalid;Database=phi_scan",
                ["Cohere:ApiKey"] = string.Empty,
            })
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlers =>
                        handlers.PrimaryHandler = new FakeUpstreamHandler(_upstream, handlers.Services)));

                services.RemoveAll<DbContextOptions<AgentForgeDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AgentForgeDbContext>>();
                services.AddDbContext<AgentForgeDbContext>(options => ((IDbContextOptionsBuilderInfrastructure)options
                    .UseInMemoryDatabase(databaseName)
                    .ReplaceService<IModelCustomizer, InMemoryModelCustomizer>())
                    .AddOrUpdateExtension(new NoMigrationsExtension()));
                services.Replace(ServiceDescriptor.Scoped<ISparseRetriever, OneGuidelineSparseRetriever>());
            });
        });
    }

    [Theory]
    [InlineData(ScanPath.Brief)]
    [InlineData(ScanPath.Agenda)]
    [InlineData(ScanPath.Ingestion)]
    [InlineData(ScanPath.EvidenceAsk)]
    [InlineData(ScanPath.EvidenceDocument)]
    public async Task ProductionStdout_WhenAPatientDataPathRuns_NamesThePatientAndClinicianOnlyInTheirAuditFields(ScanPath path)
    {
        await DriveAsync(path);

        var stdout = StopHostAndReadStdout();

        if (path is ScanPath.Brief or ScanPath.Agenda or ScanPath.EvidenceAsk or ScanPath.EvidenceDocument)
        {
            StdoutPhiScan.AuditLinesNaming(stdout, PatientId).Should().NotBeEmpty(
                "the path read the chart, so FR-AUTH-4 must have audited it - without that line the exemption is untested");
        }

        stdout.Should().Contain("LogRecord.ScopeValues",
            "the OpenTelemetry console exporter's scoped output must be in the capture, or scopes went unscanned");
        stdout.Should().Contain(nameof(HttpRequestException),
            "the path's scripted transient failure must be rendered in the capture, or exception text went unscanned");
        StdoutPhiScan.LinesLeaking(stdout, Ids).Should().BeEmpty(
            "no line may name the patient or the clinician outside their audit fields, or the patient's name or a document id at all (NFR-SEC-1, CONVENTIONS.md §7)");

        if (path is ScanPath.EvidenceDocument)
        {
            stdout.Should().Contain($"]:RequestPath: {PathOf(path)}",
                "the request path scope must still be exported, with the document id scrubbed out of it rather than the scope dropped");
        }
    }

    [Theory]
    [InlineData(ScanPath.Brief, "AgentForge.Mcp.McpToolServer")]
    [InlineData(ScanPath.Agenda, "AgentForge.Api.Agenda.AgendaRosterService")]
    [InlineData(ScanPath.Ingestion, "AgentForge.Agents.Ingestion.DocumentIngestionService")]
    [InlineData(ScanPath.EvidenceAsk, "AgentForge.Agents.EvidenceAgentSupervisor")]
    [InlineData(ScanPath.EvidenceDocument, "AgentForge.Integration.OpenEmr.Fhir.OpenEmrFhirClient")]
    public async Task PlantedIdentifiers_LoggedInsideAPathsOwnRequest_AreEachReportedByTheScan(ScanPath path, string category)
    {
        // The scan's own red control, one per path: a diagnostic category the path really uses, writing inside
        // the path's own request (so under its scopes), must fail the scan for each identifier - even beside an
        // otherwise clean run. Guards against a scan that passes because it cannot see a path's output.
        await DriveAsync(path, plantUnder: category);

        var stdout = StopHostAndReadStdout();

        var leaking = StdoutPhiScan.LinesLeaking(stdout, Ids);
        leaking.Should().OnlyContain(line => line.Contains("planted ", StringComparison.Ordinal),
            "only the planted lines may be reported, since the unplanted run is clean");
        // Listed here, not read off Ids, so a scanner that stopped looking for one cannot also stop this check.
        foreach (var identifier in (string[])[PatientId, ClinicianId, FamilyName])
        {
            leaking.Should().Contain(
                line => line.StartsWith("LogRecord.FormattedMessage:", StringComparison.Ordinal) && line.Contains(identifier, StringComparison.OrdinalIgnoreCase),
                $"the planted {identifier} must be reported off the OpenTelemetry exporter's record, not only the console provider's");
        }

        leaking.Should().Contain(line => line.Contains($"]:Planted: planted scope patient={PatientId}", StringComparison.Ordinal),
            "a scope naming the patient must be reported, or scopes went unscanned");

        ExporterRecords(stdout)
            .Where(record => record.Contains("LogRecord.FormattedMessage:        planted ", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(record => record.Split('\n').Any(line => line.TrimEnd('\r').EndsWith($"]:RequestPath: {PathOf(path)}", StringComparison.Ordinal)),
                "each plant must have been written inside the path's own request, under its scopes");
    }

    public void Dispose()
    {
        StopHost();
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _stdout.Dispose();
    }

    private static string PathOf(ScanPath path) => path switch
    {
        ScanPath.Brief => ChatHub.Route,
        ScanPath.Agenda => "/agenda",
        ScanPath.Ingestion => "/documents/ingest",
        ScanPath.EvidenceAsk => "/evidence/ask",
        ScanPath.EvidenceDocument => "/evidence/document/{id}",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    // One OpenTelemetry console-exporter record per element: its message, attributes and scopes.
    private static IEnumerable<string> ExporterRecords(string stdout) =>
        stdout.Split("LogRecord.Timestamp:", StringSplitOptions.None).Skip(1);

    private async Task DriveAsync(ScanPath path, string? plantUnder = null)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        switch (path)
        {
            case ScanPath.Brief:
                await RunBriefAsync(client, plantUnder);
                break;
            case ScanPath.Agenda:
                await RunAgendaAsync(client, plantUnder);
                break;
            case ScanPath.Ingestion:
                _upstream.PlantUnder = plantUnder;
                await IngestAsync(client);
                break;
            case ScanPath.EvidenceAsk:
                var askCookie = await LaunchAsync(client);
                var askPageKey = await PageContextKeyAsync(client, askCookie);
                await IngestAsync(client);
                _upstream.PlantUnder = plantUnder;
                await AskAsync(client, askCookie, askPageKey);
                break;
            case ScanPath.EvidenceDocument:
                var documentCookie = await LaunchAsync(client);
                var documentPageKey = await PageContextKeyAsync(client, documentCookie);
                await IngestAsync(client);
                _upstream.PlantUnder = plantUnder;
                await FetchDocumentAsync(client, documentCookie, documentPageKey);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }

        _upstream.PlantUnder = null;
    }

    private async Task RunBriefAsync(HttpClient client, string? plantUnder)
    {
        var sessionCookie = await LaunchAsync(client);
        var contextKey = await PageContextKeyAsync(client, sessionCookie);

        var brief = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = BuildHubConnection(_factory.Server, sessionCookie, contextKey);
        hub.On<JsonElement>("ChatMessage", message => brief.TrySetResult(message));
        await hub.StartAsync();

        _upstream.PlantUnder = plantUnder;
        await hub.InvokeAsync(nameof(ChatHub.RequestBrief));
        var delivered = await brief.Task.WaitAsync(TimeSpan.FromSeconds(30));

        delivered.GetProperty("kind").GetString().Should().Be("brief", "the turn must complete through the real hub");
    }

    // The roster read names the patient on the wire, which is where it belongs; the drill-down is audited.
    private async Task RunAgendaAsync(HttpClient client, string? plantUnder)
    {
        using var launch = await client.GetAsync(new Uri($"/agenda/launch?iss=https://{OpenEmrHost}/apis/default/fhir&launch=phi-scan-agenda-launch", UriKind.Relative));
        launch.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = System.Web.HttpUtility.ParseQueryString(launch.Headers.Location!.Query)["state"];

        using var callback = await SendAsync(client, HttpMethod.Get, $"/agenda/callback?code=phi-scan-agenda-code&state={state}", CookieFrom(launch));
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect, "the agenda launch must establish a session");
        var cookie = CookieFrom(callback);

        _upstream.PlantUnder = plantUnder;
        using var roster = await SendAsync(client, HttpMethod.Get, "/agenda", cookie);
        roster.StatusCode.Should().Be(HttpStatusCode.OK);
        (await roster.Content.ReadAsStringAsync()).Should().Contain(FamilyName, "the roster must have resolved the patient's name");
        _upstream.PlantUnder = null;

        using var select = await SendAsync(client, HttpMethod.Post, $"/agenda/select-patient?patientId={PatientId}", cookie);
        select.StatusCode.Should().Be(HttpStatusCode.Redirect, "the rostered patient must pass the FR-AUTH-3 gate");
    }

    // OpenEMR's cron hands over the front desk's upload; no session, and the extraction reads the name off the form.
    private static async Task IngestAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent
        {
            { IntakeImage(), "file", "intake.png" },
            { new StringContent(PatientId), "patientId" },
            { new StringContent(DocumentReferenceId), "documentReferenceId" },
            { new StringContent("intake_form"), "docType" },
        };
        using var response = await client.PostAsync(new Uri("/documents/ingest", UriKind.Relative), form);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // Each page renders from GET /patient before it asks or connects, and presents that response's key.
    private static async Task<string> PageContextKeyAsync(HttpClient client, string cookie)
    {
        using var patient = await SendAsync(client, HttpMethod.Get, "/patient", cookie);
        patient.StatusCode.Should().Be(HttpStatusCode.OK);
        using var patientBody = JsonDocument.Parse(await patient.Content.ReadAsStringAsync());
        return patientBody.RootElement.GetProperty("contextKey").GetString()!;
    }

    private static async Task AskAsync(HttpClient client, string cookie, string contextKey)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent($"Should {GivenName} {FamilyName} stay on metoprolol?"), "question" },
            { IntakeImage(), "file", "intake.png" },
            { new StringContent("intake_form"), "docType" },
            { new StringContent(contextKey), PatientContextBinding.QueryParameter },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/evidence/ask", UriKind.Relative)) { Content = form };
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task FetchDocumentAsync(HttpClient client, string cookie, string contextKey)
    {
        using var response = await SendAsync(
            client, HttpMethod.Get,
            $"/evidence/document/{DocumentReferenceId}?{PatientContextBinding.QueryParameter}={Uri.EscapeDataString(contextKey)}", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the click-to-source fetch must reach OpenEMR's Binary");
    }

    private static ByteArrayContent IntakeImage()
    {
        // The model is faked, so the bytes only need to be an image to the extractor (no PDF text layer).
        var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return image;
    }

    // The real SMART launch: /launch sets the pending-launch cookie, /callback exchanges the code, introspects and
    // runs FR-AUTH-2 before any session exists, then sets the session cookie. The cookie is carried by hand because it is Secure and the
    // TestServer speaks http, which a CookieContainer would (rightly) refuse to send it over.
    private static async Task<string> LaunchAsync(HttpClient client)
    {
        using var launch = await client.GetAsync(new Uri($"/launch?iss=https://{OpenEmrHost}/apis/default/fhir&launch=phi-scan-launch", UriKind.Relative));
        launch.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = System.Web.HttpUtility.ParseQueryString(launch.Headers.Location!.Query)["state"];

        using var completed = await SendAsync(client, HttpMethod.Get, $"/callback?code=phi-scan-code&state={state}", CookieFrom(launch));
        completed.StatusCode.Should().Be(HttpStatusCode.Redirect, "the launch must pass the FR-AUTH-2 gate");

        return CookieFrom(completed);
    }

    private static string CookieFrom(HttpResponseMessage response) =>
        string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(header => header.Split(';')[0]));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string cookie)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    // WebSockets, not long-polling: only a single long-lived request keeps the session feature.
    private static HubConnection BuildHubConnection(TestServer server, string cookie, string contextKey) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, $"{ChatHub.Route}?{PatientContextBinding.QueryParameter}={Uri.EscapeDataString(contextKey)}"),
                HttpTransportType.WebSockets,
                options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socketClient = server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
                    var uri = new UriBuilder(context.Uri) { Scheme = Uri.UriSchemeHttp }.Uri;
                    return await socketClient.ConnectAsync(uri, cancellationToken);
                };
            })
            .Build();

    // Disposing the host drains the console logger's background queue, so everything it accepted is written.
    private string StopHostAndReadStdout()
    {
        StopHost();
        return _stdout.ToString();
    }

    private void StopHost()
    {
        if (!_hostStopped)
        {
            _hostStopped = true;
            _factory.Dispose();
        }
    }
}

/// <summary>Runs <see cref="HostStdoutPhiScanTests"/> alone: it redirects the process-wide console.</summary>
[CollectionDefinition(nameof(HostStdoutPhiScanTests), DisableParallelization = true)]
public sealed class HostStdoutPhiScanRunsAlone;
