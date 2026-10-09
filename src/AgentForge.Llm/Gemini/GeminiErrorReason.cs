using System.Collections.Frozen;
using System.Text.Json;
using Refit;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// What of a failed Gemini call is safe to repeat in an exception message or a log line: the error body's
/// <c>error.status</c>, which is <c>google.rpc.Code</c>'s fixed vocabulary. Never <c>error.message</c> - it can quote
/// the request back, and the request carries chart content (CONVENTIONS.md §7). The Gemini counterpart of
/// <c>AnthropicErrorReason</c>.
/// </summary>
internal sealed record GeminiErrorReason(string ErrorStatus)
{
    /// <summary>Stands in for an absent or untrusted value.</summary>
    public const string Unrecognized = "unrecognized";

    /// <summary>Stands in for a value the failure never had, such as a timeout that got no response.</summary>
    public const string None = "none";

    // google.rpc.Code's canonical names; anything else in that field is treated as free text.
    private static readonly FrozenSet<string> KnownStatuses = new[]
    {
        "OK", "CANCELLED", "UNKNOWN", "INVALID_ARGUMENT", "DEADLINE_EXCEEDED", "NOT_FOUND", "ALREADY_EXISTS",
        "PERMISSION_DENIED", "RESOURCE_EXHAUSTED", "FAILED_PRECONDITION", "ABORTED", "OUT_OF_RANGE", "UNIMPLEMENTED",
        "INTERNAL", "UNAVAILABLE", "DATA_LOSS", "UNAUTHENTICATED",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The reason for a failure that carried no provider response at all.</summary>
    public static GeminiErrorReason NoResponse { get; } = new(None);

    /// <summary>Reads the status from the body, keeping it verbatim only if it passes the allow-list.</summary>
    public static GeminiErrorReason From(ApiException exception) => new(StatusOf(exception.Content));

    private static string StatusOf(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String
                   && status.GetString() is { } value
                   && KnownStatuses.Contains(value)
                ? value
                : Unrecognized;
        }
        catch (JsonException)
        {
            return Unrecognized;
        }
    }
}
