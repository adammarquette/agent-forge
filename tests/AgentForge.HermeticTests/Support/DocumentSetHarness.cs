using AgentForge.Agents.Ingestion;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using AgentForge.GenerateFixtureDocuments;
using AgentForge.Llm;
using AgentForge.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// The ingestion half of <see cref="HermeticPipeline"/>, over the whole generated document set: the real
/// <c>DocumentIngestionService</c>, <c>DocumentExtractor</c> (schema gate, PdfPig text layer,
/// <c>CitationBoundingBoxResolver</c>), <c>DerivedFactMapper</c> and <c>DerivedFactStore</c> on EF Core's
/// in-memory provider. Only the model is scripted - by <see cref="ManifestReplies"/>, keyed on each committed
/// file's own bytes.
/// </summary>
internal sealed class DocumentSetHarness : IAsyncDisposable
{
    public const string PatientId = "hermetic-patient-0692";

    private readonly ServiceProvider _services;

    private DocumentSetHarness(ServiceProvider services, ScriptedModelProvider model, CapturingLoggerProvider logs)
    {
        _services = services;
        Model = model;
        Logs = logs;
    }

    public ScriptedModelProvider Model { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <param name="rewrite">Replaces the faithful reply for one document - a model that invents - when given.</param>
    public static async Task<DocumentSetHarness> CreateAsync(Func<FixtureDocument, string, string>? rewrite = null)
    {
        var replies = new Dictionary<string, string>();
        foreach (var document in FixtureDocuments.All)
        {
            var reply = ManifestReplies.ExtractionFor(document.Manifest);
            replies[HermeticPipeline.Sha256(await ReadAsync(document))] = rewrite?.Invoke(document, reply) ?? reply;
        }

        var model = new ScriptedModelProvider(replies, _ => string.Empty);
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAgentForgeMetrics, AgentForgeMetrics>();
        services.AddSingleton<ILlmProvider>(model);
        var databaseName = $"hermetic-set-{Guid.NewGuid():N}";
        services.AddDbContext<AgentForgeDbContext>(o => o
            .UseInMemoryDatabase(databaseName)
            .ReplaceService<IModelCustomizer, HermeticModelCustomizer>());
        services.AddScoped<IDerivedFactStore, DerivedFactStore>();
        services.AddAgentForgeDocuments();
        services.AddSingleton<IDerivedFactMapper, DerivedFactMapper>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();

        return new DocumentSetHarness(
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }),
            model,
            logs);
    }

    /// <summary>The committed file's bytes - what an upload would carry.</summary>
    public static Task<byte[]> ReadAsync(FixtureDocument document) =>
        File.ReadAllBytesAsync(HermeticPipeline.FixturePath(document.FileName));

    public static ClinicalDocumentType TypeOf(FixtureDocument document) => document.Manifest.DocumentType switch
    {
        DocumentManifest.LabPdf => ClinicalDocumentType.LabPdf,
        DocumentManifest.IntakeForm => ClinicalDocumentType.IntakeForm,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document.Manifest.DocumentType, "Unknown document type."),
    };

    /// <param name="patientId">Files the document under another patient than <see cref="PatientId"/>, when given.</param>
    /// <param name="documentReferenceId">Files it under another OpenEMR id than its own, when given.</param>
    public async Task<DocumentIngestionResult> IngestAsync(
        FixtureDocument document, ClinicalDocumentType? asType = null, string? patientId = null, string? documentReferenceId = null)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>().IngestAsync(
            new DocumentIngestionRequest
            {
                PatientId = patientId ?? PatientId,
                DocumentReferenceId = documentReferenceId ?? DocumentReferenceIdOf(document),
                DocumentType = asType ?? TypeOf(document),
                Content = await ReadAsync(document),
                MediaType = document.MediaType,
            });
    }

    public static string DocumentReferenceIdOf(FixtureDocument document) => $"hermetic-docref-{document.FileName}";

    /// <summary>The stored document for <paramref name="document"/>'s bytes, with its facts, or null.</summary>
    public async Task<IngestedDocument?> StoredAsync(FixtureDocument document)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDerivedFactStore>()
            .FindByContentHashAsync(ContentHash.Compute(await ReadAsync(document)));
    }

    /// <summary>The patient the store says owns the OpenEMR document reference <paramref name="documentReferenceId"/>.</summary>
    public async Task<string?> OwnerOfAsync(string documentReferenceId)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDerivedFactStore>()
            .FindPatientIdByDocumentReferenceIdAsync(documentReferenceId);
    }

    /// <summary>Every stored document and derived fact row, counted straight off the context.</summary>
    public async Task<(int Documents, int Facts)> CountRowsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentForgeDbContext>();
        return (await context.IngestedDocuments.CountAsync(), await context.DerivedFacts.CountAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Logs.Dispose();
    }
}
