using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Refit;

namespace AgentForge.Llm.Anthropic;

/// <summary>
/// What of a failed Messages API call is safe to repeat in an exception message or a log line: Anthropic's
/// <c>error.type</c>, which is a fixed vocabulary, and the <c>request-id</c> response header, which is an opaque
/// token Anthropic support can look the call up by. Never the body's <c>message</c> - it can quote the request
/// back, and the request carries chart content (CONVENTIONS.md §7). A separate change
/// </summary>
internal sealed partial record AnthropicErrorReason(string ErrorType, string RequestId)
{
    /// <summary>Stands in for an absent or untrusted value.</summary>
    public const string Unrecognized = "unrecognized";

    /// <summary>Stands in for a value the failure never had, such as a timeout that got no response.</summary>
    public const string None = "none";

    // Anthropic's documented error types; anything else in that field is treated as free text.
    private static readonly FrozenSet<string> KnownErrorTypes = new[]
    {
        "invalid_request_error", "authentication_error", "permission_error", "not_found_error",
        "request_too_large", "rate_limit_error", "api_error", "overloaded_error", "billing_error", "timeout_error",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The reason for a failure that carried no provider response at all.</summary>
    public static AnthropicErrorReason NoResponse { get; } = new(None, None);

    /// <summary>Reads the error type from the body and the request id from the headers, keeping neither verbatim
    /// unless it passes its own allow-list.</summary>
    public static AnthropicErrorReason From(ApiException exception) =>
        new(ErrorTypeOf(exception.Content), RequestIdOf(exception));

    private static string ErrorTypeOf(string? body)
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
                   && error.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.GetString() is { } value
                   && KnownErrorTypes.Contains(value)
                ? value
                : Unrecognized;
        }
        catch (JsonException)
        {
            return Unrecognized;
        }
    }

    private static string RequestIdOf(ApiException exception)
    {
        if (exception.Headers is null || !exception.Headers.TryGetValues("request-id", out var values))
        {
            return None;
        }

        var value = values.FirstOrDefault();
        return value is not null && OpaqueToken().IsMatch(value) ? value : Unrecognized;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueToken();
}
