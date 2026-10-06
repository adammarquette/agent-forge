using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Data.Entities;

namespace AgentForge.Agents;

/// <summary>
/// Reads and writes <see cref="CitationSourceType"/> as the lowercase token
/// <see cref="CitationSourceTypeJson.ToWireName"/> produces, so the API's <c>sourceType</c> matches the
/// brief's <c>fhir</c> / <c>derived</c> / <c>guideline</c> spelling rather than the enum's ordinal or its
/// PascalCase name.
/// </summary>
public sealed class CitationSourceTypeJsonConverter : JsonConverter<CitationSourceType>
{
    /// <inheritdoc />
    public override CitationSourceType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Citation source_type must be a string.");
        }

        return reader.GetString() switch
        {
            "fhir" => CitationSourceType.Fhir,
            "derived" => CitationSourceType.Derived,
            "guideline" => CitationSourceType.Guideline,
            _ => throw new JsonException("Unknown citation source_type."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CitationSourceType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(CitationSourceTypeJson.ToWireName(value));
}

/// <summary>The wire spelling of <see cref="CitationSourceType"/>, shared by the serializer and the published schemas.</summary>
public static class CitationSourceTypeJson
{
    /// <summary>The token the brief names for <paramref name="sourceType"/>.</summary>
    public static string ToWireName(CitationSourceType sourceType) => sourceType switch
    {
        CitationSourceType.Fhir => "fhir",
        CitationSourceType.Derived => "derived",
        CitationSourceType.Guideline => "guideline",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown citation source type."),
    };
}
