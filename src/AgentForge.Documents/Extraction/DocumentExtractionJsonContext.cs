using System.Text.Json.Serialization;

namespace AgentForge.Documents.Extraction;

/// <summary>
/// Source-generated JSON context for the extraction contracts (CONVENTIONS.md §3). Uses
/// snake_case property names — the wire shape the extraction prompt instructs the model to return — and
/// enforces required members on deserialization, which is the schema-is-the-gate mechanism (NFR-CONTRACT-1).
/// It also enforces nullable annotations: <c>required</c> alone accepts a present-but-null value, so a JSON
/// null in a non-nullable member (an item's text, a quote, a list) is rejected here rather than reaching the
/// matchers as a null they dereference. A separate change review N2
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(LabExtraction))]
[JsonSerializable(typeof(IntakeExtraction))]
public partial class DocumentExtractionJsonContext : JsonSerializerContext;
