using Refit;

namespace AgentForge.Llm.Gemini;

/// <summary>The Gemini API's <c>generateContent</c> (ai.google.dev/api/generate-content, read 2026-10-08).</summary>
public interface IGeminiGenerativeLanguageApi
{
    /// <summary>Generates one response from <paramref name="model"/>. The key travels in a header, never the URL.</summary>
    [Post("/v1beta/models/{model}:generateContent")]
    Task<GeminiGenerateContentResponse> GenerateContentAsync(
        string model, [Body] GeminiGenerateContentRequest request, CancellationToken cancellationToken = default);
}
