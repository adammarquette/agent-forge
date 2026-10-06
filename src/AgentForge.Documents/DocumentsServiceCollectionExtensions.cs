using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Documents;

/// <summary>DI wiring for the Week 2 document-ingestion tier.</summary>
public static class DocumentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDocumentExtractor"/>. Assumes an <c>ILlmProvider</c> and an
    /// <c>IAgentForgeMetrics</c> are registered — the extractor meters its own model call (FR-OBS-2).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAgentForgeDocuments(this IServiceCollection services)
    {
        services.AddSingleton<IPdfWordReader, PdfPigWordReader>();
        services.AddScoped<IDocumentExtractor, DocumentExtractor>();
        return services;
    }
}
