using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgentForge.Agents;
using AgentForge.Data.Entities;
using AgentForge.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// OpenEMR and the Anthropic Messages API, answered in process for <see cref="HostStdoutPhiScanTests"/>. Any other
/// host fails the call. Every patient-data answer carries the synthetic patient's id and name, so a sidecar line that
/// echoes an upstream answer is caught by the scan. One scripted transient failure per path makes the resilience
/// pipeline retry, so a rendered exception reaches stdout on each.
/// </summary>
internal sealed class FakeUpstream
{
    public const string PatientId = "0e7d2b54-9c1a-4f3e-b8a6-71c0d2e9f5a3";
    public const string ClinicianId = "phi-scan-clinician-7c41";
    public const string GivenName = "Synthetic";
    public const string FamilyName = "Scanpatient";
    public const string OpenEmrHost = "openemr.host-stdout-phi-scan.invalid";
    public const string AgendaClientId = "host-stdout-phi-scan-agenda";

    private int _allergyCalls;
    private int _extractionCalls;
    private int _binaryCalls;

    /// <summary>When set, each upstream call first logs one planted line per identifier under this category.</summary>
    public string? PlantUnder { get; set; }

    public async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, IServiceProvider services)
    {
        if (PlantUnder is { } category)
        {
            // Written on the calling request's own async flow, so it carries that request's logging scopes.
            var planted = services.GetRequiredService<ILoggerFactory>().CreateLogger(category);
            foreach (var line in (string[])[$"planted patient={PatientId}", $"planted clinician={ClinicianId}", $"planted name={GivenName} {FamilyName}"])
            {
                planted.Log(LogLevel.Information, default, line, null, static (state, _) => state);
            }

            // A clean message inside a scope that names the patient: only a scan that reads scopes sees it.
            using (planted.BeginScope(new Dictionary<string, object> { ["Planted"] = $"planted scope patient={PatientId}" }))
            {
                planted.Log(LogLevel.Information, default, "planted scope carrier", null, static (state, _) => state);
            }
        }

        var uri = request.RequestUri!;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
        if (uri.Host == OpenEmrHost)
        {
            return AnswerOpenEmr(uri, body);
        }

        if (uri.Host == "api.anthropic.com" && uri.AbsolutePath == "/v1/messages")
        {
            return AnswerModel(body);
        }

        throw new InvalidOperationException($"The host-stdout PHI scan reached an unfaked upstream: {uri.Host}");
    }

    private HttpResponseMessage AnswerOpenEmr(Uri uri, string body)
    {
        var path = uri.AbsolutePath;
        if (path == "/oauth2/default/token")
        {
            // The agenda client's launch carries no patient context; the single-patient launch does.
            var patient = body.Contains($"client_id={AgendaClientId}", StringComparison.Ordinal) ? string.Empty : $$""","patient":"{{PatientId}}" """;
            return Json($$"""{"access_token":"phi-scan-access-token","token_type":"Bearer","expires_in":3600,"scope":"launch"{{patient}}}""");
        }

        if (path == "/oauth2/default/introspect")
        {
            var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            return Json($$"""{"active":true,"client_id":"host-stdout-phi-scan","exp":{{exp}},"sub":"{{ClinicianId}}"}""");
        }

        if (path == "/apis/default/fhir/Appointment")
        {
            // Later today, so the roster keeps it; the relationship gate reads only the participants.
            var start = DateTimeOffset.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            return Json($$$"""
                {"resourceType":"Bundle","type":"searchset","entry":[{"resource":{"resourceType":"Appointment","id":"phi-scan-appointment","status":"booked","start":"{{{start}}}",
                "participant":[{"actor":{"reference":"Patient/{{{PatientId}}}"}},{"actor":{"reference":"Practitioner/{{{ClinicianId}}}"}}]}}]}
                """);
        }

        if (path == $"/apis/default/fhir/Patient/{PatientId}")
        {
            return Json($$"""{"resourceType":"Patient","id":"{{PatientId}}","name":[{"given":["{{GivenName}}"],"family":"{{FamilyName}}"}],"birthDate":"1950-01-01","gender":"female"}""");
        }

        if (path.StartsWith("/apis/default/fhir/Binary/", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _binaryCalls) == 1)
            {
                throw new HttpRequestException("Synthetic transient connection failure.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 synthetic")) { Headers = { ContentType = new("application/pdf") } },
            };
        }

        // One transient failure, retried by the resilience pipeline, so a rendered exception reaches stdout.
        if (path == "/apis/default/fhir/AllergyIntolerance" && Interlocked.Increment(ref _allergyCalls) == 1)
        {
            throw new HttpRequestException("Synthetic transient connection failure.");
        }

        return Json("""{"resourceType":"Bundle","type":"searchset","entry":[]}""");
    }

    // Routed by what the request is, not by call order, because the paths make different numbers of calls.
    private HttpResponseMessage AnswerModel(string body)
    {
        var request = JsonNode.Parse(body)!;
        var blocks = request["messages"]!.AsArray().SelectMany(m => m!["content"]!.AsArray()).Select(c => (string?)c!["type"]).ToList();

        if (blocks.Contains("image") || blocks.Contains("document"))
        {
            if (Interlocked.Increment(ref _extractionCalls) == 1)
            {
                throw new HttpRequestException("Synthetic transient connection failure.");
            }

            return Text(IntakeExtraction);
        }

        if (request["tools"] is not null && !blocks.Contains("tool_result"))
        {
            return Json("""{"id":"msg_phi_scan_tool","content":[{"type":"tool_use","id":"toolu_phi_scan","name":"get_patient_summary","input":{}}],"stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5}}""");
        }

        return Text($"{GivenName} {FamilyName} has no active problems, medications or allergies recorded.");
    }

    // What a vision model reads off an intake form: the patient's name and date of birth among the rest.
    private static readonly string IntakeExtraction = new JsonObject
    {
        ["demographics"] = new JsonObject { ["full_name"] = $"{GivenName} {FamilyName}".ToUpperInvariant(), ["date_of_birth"] = "1950-01-01", ["sex"] = "female" },
        ["chief_concern"] = new JsonObject
        {
            ["text"] = "Palpitations when climbing stairs",
            ["citation"] = new JsonObject { ["page"] = 1, ["quote"] = "PALPITATIONS WHEN CLIMBING STAIRS", ["bounding_box"] = new JsonArray(0.1, 0.3, 0.5, 0.03) },
        },
        ["current_medications"] = new JsonArray(new JsonObject
        {
            ["name"] = "METOPROLOL",
            ["dose"] = "25 MG DAILY",
            ["citation"] = new JsonObject { ["page"] = 1, ["quote"] = "METOPROLOL 25 MG DAILY", ["bounding_box"] = new JsonArray(0.1, 0.4, 0.5, 0.03) },
        }),
        ["allergies"] = new JsonArray(),
        ["family_history"] = new JsonArray(),
        ["citation"] = new JsonObject { ["page"] = 1, ["quote"] = $"NAME: {GivenName} {FamilyName}".ToUpperInvariant(), ["bounding_box"] = new JsonArray(0.1, 0.1, 0.5, 0.03) },
    }.ToJsonString();

    private static HttpResponseMessage Text(string text) =>
        Json(new JsonObject
        {
            ["id"] = "msg_phi_scan_text",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["stop_reason"] = "end_turn",
            ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
        }.ToJsonString());

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FakeUpstreamHandler(FakeUpstream upstream, IServiceProvider services) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        upstream.AnswerAsync(request, services);
}

/// <summary>
/// The real <c>AgentForgeDbContext</c> model on EF Core's in-memory provider. The guideline chunk's two Postgres-only
/// columns have no in-memory mapping: the generated <c>tsvector</c> is dropped (retrieval's SQL half is
/// <see cref="OneGuidelineSparseRetriever"/>), and the pgvector embedding is stored as text so the startup seeder's
/// backfill query still runs. The ingested-document and derived-fact tables keep their production configuration.
/// </summary>
internal sealed class InMemoryModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var chunk = modelBuilder.Entity<GuidelineChunk>();
        chunk.Ignore(c => c.SearchVector);
        chunk.Property(c => c.Embedding).HasConversion(
            vector => string.Join(',', vector!.ToArray()),
            text => new Vector(text.Split(',', StringSplitOptions.None).Select(float.Parse).ToArray()));
    }
}

/// <summary>Startup applies migrations; the in-memory provider has none to apply, so its migrator does nothing.</summary>
internal sealed class NoMigrationsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) => services.AddScoped<IMigrator, NoMigrator>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => string.Empty;

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
        }
    }

    private sealed class NoMigrator : IMigrator
    {
        public void Migrate(string? targetMigration)
        {
        }

        public Task MigrateAsync(string? targetMigration, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string GenerateScript(string? fromMigration = null, string? toMigration = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) =>
            string.Empty;

        public bool HasPendingModelChanges() => false;
    }
}

/// <summary>The SQL half of retrieval (Postgres full-text search), answered with one synthetic guideline chunk.</summary>
internal sealed class OneGuidelineSparseRetriever : ISparseRetriever
{
    public Task<IReadOnlyList<EvidenceSnippet>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceSnippet>>(
        [
            new EvidenceSnippet
            {
                DocumentId = "synthetic-beta-blocker-guideline",
                Section = "Beta-blocker monitoring",
                ChunkId = "phi-scan-beta-blocker-1",
                Text = "Review heart rate and blood pressure when continuing beta-blocker therapy.",
                Score = 1,
            },
        ]);
}
