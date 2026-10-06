using System.Text.Json.Nodes;
using AgentForge.Agents;
using AgentForge.Data.Entities;
using AgentForge.Documents.Extraction;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgentForge.Api.OpenApi;

/// <summary>
/// The one description of the sidecar's HTTP surface (Interface D, <c>INTERFACES.md</c> §D.1),
/// generated from the endpoints the app actually maps rather than written out beside them. The same
/// configuration serves <c>/openapi/v1.json</c> at run time and produces the exported
/// document (<c>--export-openapi</c>), so the served and the exported document are the same
/// artifact and cannot describe the surface differently. A separate change
/// </summary>
public static class AgentForgeOpenApi
{
    /// <summary>The document name — also the <c>/openapi/{name}.json</c> path segment.</summary>
    public const string DocumentName = "v1";

    /// <summary>
    /// The extraction contracts published alongside the endpoints. They reach the wire as the
    /// <c>extractedFacts</c> JSON <i>string</i> on the evidence answer, so no operation references them and
    /// the generator would otherwise leave them out of a document whose job is to publish the contracts
    /// (NFR-CONTRACT-1). Named one by one rather than swept from the assembly: a third one has to be a
    /// decision, not an accident.
    /// </summary>
    private static readonly Type[] ExtractionContracts = [typeof(LabExtraction), typeof(IntakeExtraction)];

    /// <summary>
    /// Registers the OpenAPI document generator for the sidecar's HTTP surface.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAgentForgeOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            // NFR-API-W2-1 asks for 3.0; the generator's own default is 3.1.
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;

            options.AddSchemaTransformer(CollapseNumberOrStringUnion);
            options.AddSchemaTransformer(DescribeConverterBackedEnum);
            options.AddDocumentTransformer(DescribeDocumentAsync);
        });

        return services;
    }

    /// <summary>
    /// Web-defaults JSON accepts a number written as a string (<c>NumberHandling.AllowReadingFromString</c>),
    /// so the generator types every numeric field as the union <c>["integer","string"]</c>. OpenAPI 3.0 has no
    /// union, and the serializer answers by emitting <b>no</b> <c>type</c> at all — which reads as "anything".
    /// Narrowing to the numeric member keeps 3.0 honest about the field it is describing.
    /// </summary>
    private static Task CollapseNumberOrStringUnion(
        IOpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema is OpenApiSchema concrete
            && concrete.Type is { } type
            && type.HasFlag(JsonSchemaType.String)
            && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)))
        {
            concrete.Type = type & ~JsonSchemaType.String;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> is opaque to the generator: it reads
    /// and writes the member itself, so nothing about the enum reaches the schema and the field is published
    /// with <b>no <c>type</c> and no <c>enum</c></b> — which reads as "anything", the same failure the
    /// numeric-union narrowing above exists to prevent, arriving by a different route.
    /// Two members of this surface have one, each a decision rather than a sweep:
    /// <see cref="CitationQuoteMatch"/> (FR-CITE-2's verbatim-grounding signal) and
    /// <see cref="CitationSourceType"/> (FR-CITE-1's <c>source_type</c>, whose wire spelling is lowercase).
    /// Publishing either as untyped would leave the citation contract silent about a closed set.
    /// <para>
    /// Named rather than swept, for the reason <see cref="ExtractionContracts"/> is. A check of the exported
    /// document fails on any node that publishes no type at all, which is what caught the first one. A separate change review, a separate change
    /// </para>
    /// </summary>
    private static Task DescribeConverterBackedEnum(
        IOpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema is not OpenApiSchema concrete)
        {
            return Task.CompletedTask;
        }

        if (context.JsonTypeInfo.Type == typeof(CitationQuoteMatch))
        {
            concrete.Type = JsonSchemaType.String;

            // Read off ToWireName rather than restated here, so the published enumeration cannot drift from
            // the tokens the converter actually writes - the fuzzy member arrives in the document free.
            concrete.Enum = [.. Enum.GetValues<CitationQuoteMatch>()
                .Select(match => (JsonNode)match.ToWireName())];
        }
        else if (context.JsonTypeInfo.Type == typeof(CitationSourceType))
        {
            concrete.Type = JsonSchemaType.String;
            concrete.Enum = [.. Enum.GetValues<CitationSourceType>()
                .Select(sourceType => (JsonNode)CitationSourceTypeJson.ToWireName(sourceType))];

            // The generator records the CLR default as the enum ordinal (1 = Derived). That is not a value
            // of the string enumeration the converter writes, so publishing it would describe a token the
            // wire never sends. The field stays optional; no default is declared.
            concrete.Default = null;
        }

        return Task.CompletedTask;
    }

    private static async Task DescribeDocumentAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "AgentForge Clinical Copilot — sidecar HTTP surface",
            Version = DocumentName,
            Description =
                "The .NET sidecar's browser/orchestrator-facing surface (Interface D), generated from the "
                + "implementation. INTERFACES.md §D.1 is the authoritative interface "
                + "contract and additionally covers the SignalR chat hub and the MCP tool catalog, neither of "
                + "which this document can express. The Week 2 routes appear only when the data tier is "
                + "configured, so a document generated without it describes a smaller surface.",
        };

        // The base URL is per-environment (ARCHITECTURE.md §13) and, left alone, is whatever host:port the
        // exporting process happened to bind - which would rewrite the committed file on every run.
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        foreach (var contract in ExtractionContracts)
        {
            document.Components.Schemas[contract.Name] =
                await context.GetOrCreateSchemaAsync(contract, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }
    }
}
