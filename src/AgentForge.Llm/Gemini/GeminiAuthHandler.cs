using Microsoft.Extensions.Options;

namespace AgentForge.Llm.Gemini;

/// <summary>
/// Attaches the Gemini API key as the <c>x-goog-api-key</c> header on every outbound call
/// (CONVENTIONS.md §4). Never the <c>?key=</c> query parameter Google also accepts: a URL reaches access
/// logs, traces and exception messages that a header does not.
/// </summary>
public sealed class GeminiAuthHandler(IOptions<LlmProviderOptions> options) : DelegatingHandler
{
    /// <summary>The header Google's REST examples authenticate with.</summary>
    public const string ApiKeyHeader = "x-goog-api-key";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ApiKeyHeader);
        request.Headers.Add(ApiKeyHeader, options.Value.ApiKey);

        return base.SendAsync(request, cancellationToken);
    }
}
