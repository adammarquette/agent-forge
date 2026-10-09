namespace AgentForge.Llm;

/// <summary>
/// Which model API serves <see cref="ILlmProvider"/> (<c>Llm__Provider</c>, ARCHITECTURE.md section 12). Chosen per
/// environment at startup; the callers never see which.
/// </summary>
public enum LlmProviderKind
{
    /// <summary>The Anthropic Messages API - the default, and production's provider.</summary>
    Anthropic,

    /// <summary>
    /// The Google Gemini API (<c>generateContent</c>). Its free tier may use prompts and responses to improve
    /// Google's products, so it is for synthetic data only (DEPLOYMENT.md).
    /// </summary>
    Gemini,
}
