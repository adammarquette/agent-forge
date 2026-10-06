using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AgentForge.Agents;
using AgentForge.Agents.Ingestion;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.GenerateFixtureDocuments;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.Retrieval;
using AgentForge.Verification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentForge.HermeticTests.Support;

/// <summary>One deliberately broken stage, for the red controls. Each is a regression a real change could make.</summary>
public enum Sabotage
{
    /// <summary>Nothing broken - the green run.</summary>
    None,

    /// <summary>An HTTP request leaves the pipeline, as a real provider left in the composition would.</summary>
    HttpCallFromThePipeline,

    /// <summary>A raw socket connect, as a database or cache client left in the composition would make.</summary>
    SocketConnectFromThePipeline,

    /// <summary>A DNS lookup, the first thing any client of a named host does.</summary>
    DnsLookupFromThePipeline,

    /// <summary>The VLM rewords its quotes instead of copying them.</summary>
    ParaphrasedQuotes,

    /// <summary>The fact store's patient read comes back empty (a wrong filter, a dropped Include).</summary>
    FactReadSideDropped,

    /// <summary>The guideline corpus is empty, so retrieval finds nothing.</summary>
    EmptyGuidelineCorpus,

    /// <summary>The critic ships the draft unchanged.</summary>
    PassThroughCritic,

    /// <summary>A model adapter logs the prompt it sends.</summary>
    PromptLogged,
}

/// <summary>Everything one run produced, for <see cref="PipelineChecks"/> to rule on.</summary>
internal sealed record PipelineOutcome
{
    public required byte[] LabBytes { get; init; }
    public required byte[] IntakeBytes { get; init; }
    public required DocumentIngestionResult LabIngest { get; init; }
    public required DocumentIngestionResult IntakeIngest { get; init; }
    public required DocumentIngestionResult LabReingest { get; init; }
    public required IngestedDocument? LabDocument { get; init; }
    public required IngestedDocument? IntakeDocument { get; init; }
    public required IReadOnlyList<DerivedFact> PatientFacts { get; init; }
    public required EvidenceAgentResult Result { get; init; }
    public required string? Draft { get; init; }
    public required IReadOnlyList<string> DocumentHashes { get; init; }
    public required IReadOnlyList<string> Logs { get; init; }
    public required IReadOnlyList<string> SpanText { get; init; }
    public required IReadOnlyList<string> NetworkActivity { get; init; }
    public required Type LlmProviderType { get; init; }
    public required Type EmbeddingProviderType { get; init; }
    public required Type RerankerType { get; init; }
}

/// <summary>
/// Composes the production ingestion-to-answer pipeline the way <c>Program.cs</c> does - the same
/// registration extensions for documents, retrieval and the evidence agent, the same verifier wiring, the
/// real <see cref="DerivedFactStore"/> over the real <see cref="AgentForgeDbContext"/> model - and replaces
/// exactly three things: the model (<see cref="ScriptedModelProvider"/>), the database process (EF Core's
/// in-memory provider) and the SQL half of retrieval (<see cref="InMemoryGuidelineCorpus"/>). Configuration
/// is an empty in-memory source, never the environment, so no key a developer has exported can reach it.
/// </summary>
internal static partial class HermeticPipeline
{
    public const string PatientId = "hermetic-patient-0701";
    public const string LabDocumentReferenceId = "hermetic-docref-lab-0701";
    public const string IntakeDocumentReferenceId = "hermetic-docref-intake-0701";
    public const string Question = "Should Demo Harold Whitfield stay on spironolactone given his latest potassium?";

    public static async Task<PipelineOutcome> RunAsync(Sabotage sabotage = Sabotage.None)
    {
        var labBytes = await ReadFixtureAsync(SyntheticLabPanel.FileName);
        var intakeBytes = await ReadFixtureAsync(SyntheticIntakeForm.FileName);

        var model = new ScriptedModelProvider(
            new Dictionary<string, string>
            {
                [Sha256(labBytes)] = CannedReplies.LabExtraction(paraphrase: sabotage == Sabotage.ParaphrasedQuotes),
                [Sha256(intakeBytes)] = CannedReplies.IntakeExtraction(),
            },
            prompt =>
            {
                TouchTheNetwork(sabotage);

                return CannedReplies.Compose(prompt);
            });

        using var telemetry = new TelemetryRecorder();
        using var logs = new CapturingLoggerProvider();
        await using var services = Compose(model, logs, sabotage);

        var labIngest = await IngestAsync(services, LabDocumentReferenceId, ClinicalDocumentType.LabPdf, labBytes, SyntheticLabPanel.MediaType);
        var intakeIngest = await IngestAsync(services, IntakeDocumentReferenceId, ClinicalDocumentType.IntakeForm, intakeBytes, SyntheticIntakeForm.MediaType);
        var labReingest = await IngestAsync(services, LabDocumentReferenceId, ClinicalDocumentType.LabPdf, labBytes, SyntheticLabPanel.MediaType);

        EvidenceAgentResult result;
        await using (var scope = services.CreateAsyncScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IEvidenceAgentSupervisor>().RunAsync(
                new EvidenceAgentRequest { PatientId = PatientId, Question = Question }, CancellationToken.None);
        }

        await using var readScope = services.CreateAsyncScope();
        var store = readScope.ServiceProvider.GetRequiredService<IDerivedFactStore>();
        return new PipelineOutcome
        {
            LabBytes = labBytes,
            IntakeBytes = intakeBytes,
            LabIngest = labIngest,
            IntakeIngest = intakeIngest,
            LabReingest = labReingest,
            LabDocument = await store.FindByContentHashAsync(ContentHash.Compute(labBytes)),
            IntakeDocument = await store.FindByContentHashAsync(ContentHash.Compute(intakeBytes)),
            PatientFacts = await store.GetByPatientAsync(PatientId),
            Result = result,
            Draft = model.LastDraft,
            DocumentHashes = model.DocumentHashes,
            Logs = logs.Entries,
            SpanText = telemetry.SpanText,
            NetworkActivity = telemetry.NetworkActivity,
            LlmProviderType = readScope.ServiceProvider.GetRequiredService<ILlmProvider>().GetType(),
            EmbeddingProviderType = readScope.ServiceProvider.GetRequiredService<IEmbeddingProvider>().GetType(),
            RerankerType = readScope.ServiceProvider.GetRequiredService<IReranker>().GetType(),
        };
    }

    public static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static Task<byte[]> ReadFixtureAsync(string fileName) => File.ReadAllBytesAsync(FixturePath(fileName));

    private static ServiceProvider Compose(ScriptedModelProvider model, CapturingLoggerProvider logs, Sabotage sabotage)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAgentForgeMetrics, AgentForgeMetrics>();
        services.AddSingleton<ILlmProvider>(model);

        // The data tier minus Npgsql: same context, same store, one database per run.
        var databaseName = $"hermetic-{Guid.NewGuid():N}";
        services.AddDbContext<AgentForgeDbContext>(o => o
            .UseInMemoryDatabase(databaseName)
            .ReplaceService<IModelCustomizer, HermeticModelCustomizer>());
        services.AddScoped<IDerivedFactStore, DerivedFactStore>();

        // From here down, Program.cs's own registrations.
        services.AddAgentForgeDocuments();
        services.AddAgentForgeRetrieval(configuration);
        services.AddAgentForgeEvidenceAgent();
        services.AddSingleton<ISourceAttributionEngine, SourceAttributionEngine>();
        services.AddSingleton(sp => new CardiologyConstraintEngine(
            CardiologyConstraintRules.Default, sp.GetRequiredService<ILogger<CardiologyConstraintEngine>>()));
        services.AddSingleton<IClinicalResponseVerifier, ClinicalResponseVerifier>();
        services.AddSingleton<IDerivedFactMapper, DerivedFactMapper>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();

        services.Replace(ServiceDescriptor.Scoped<ISparseRetriever>(_ =>
            new InMemoryGuidelineCorpus(sabotage == Sabotage.EmptyGuidelineCorpus ? [] : InMemoryGuidelineCorpus.Fixture)));

        switch (sabotage)
        {
            case Sabotage.FactReadSideDropped:
                services.Replace(ServiceDescriptor.Scoped<IDerivedFactStore>(sp =>
                    new PatientReadDroppingStore(ActivatorUtilities.CreateInstance<DerivedFactStore>(sp))));
                break;
            case Sabotage.PassThroughCritic:
                services.Replace(ServiceDescriptor.Singleton<IClinicalResponseVerifier, PassThroughVerifier>());
                break;
            case Sabotage.PromptLogged:
                services.Replace(ServiceDescriptor.Singleton<ILlmProvider>(sp =>
                    new PromptLoggingProvider(model, sp.GetRequiredService<ILogger<PromptLoggingProvider>>())));
                break;
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<DocumentIngestionResult> IngestAsync(
        ServiceProvider services, string documentReferenceId, ClinicalDocumentType type, byte[] content, string mediaType)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>().IngestAsync(
            new DocumentIngestionRequest
            {
                PatientId = PatientId,
                DocumentReferenceId = documentReferenceId,
                DocumentType = type,
                Content = content,
                MediaType = mediaType,
            });
    }

    // Loopback on the discard port, and a name in the reserved .invalid TLD: each fails at once, but the
    // attempt is what the recorder has to see.
    private static void TouchTheNetwork(Sabotage sabotage)
    {
        try
        {
            switch (sabotage)
            {
                case Sabotage.HttpCallFromThePipeline:
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        client.GetAsync(new Uri("http://127.0.0.1:9/")).GetAwaiter().GetResult().Dispose();
                    }

                    break;
                case Sabotage.SocketConnectFromThePipeline:
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        socket.Connect(new IPEndPoint(IPAddress.Loopback, 9));
                    }

                    break;
                case Sabotage.DnsLookupFromThePipeline:
                    Dns.GetHostAddresses("hermetic-probe.invalid");
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
        }
    }

    private sealed class PatientReadDroppingStore(IDerivedFactStore inner) : IDerivedFactStore
    {
        public Task<IngestedDocument?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) =>
            inner.FindByContentHashAsync(contentHash, cancellationToken);

        public Task<IReadOnlyList<DerivedFact>> GetByPatientAsync(string patientId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DerivedFact>>([]);

        public Task<string?> FindPatientIdByDocumentReferenceIdAsync(
            string documentReferenceId, CancellationToken cancellationToken = default) =>
            inner.FindPatientIdByDocumentReferenceIdAsync(documentReferenceId, cancellationToken);

        public Task AddAsync(IngestedDocument document, CancellationToken cancellationToken = default) =>
            inner.AddAsync(document, cancellationToken);
    }

    private sealed class PassThroughVerifier : IClinicalResponseVerifier
    {
        public VerificationResult Verify(string answer, IReadOnlyCollection<string> toolResultJson) =>
            new(true, answer, [], []);
    }

    private sealed partial class PromptLoggingProvider(ILlmProvider inner, ILogger<PromptLoggingProvider> logger) : ILlmProvider
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            foreach (var text in request.Messages.SelectMany(m => m.Content).OfType<LlmTextContent>())
            {
                LogPrompt(logger, text.Text);
            }

            return inner.CompleteAsync(request, cancellationToken);
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Sending prompt: {Prompt}")]
        private static partial void LogPrompt(ILogger logger, string prompt);
    }
}
