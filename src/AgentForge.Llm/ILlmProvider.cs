namespace AgentForge.Llm;

/// <summary>
/// Seam over the model tier (ARCHITECTURE.md §12, D12). Two implementations, chosen per environment by
/// <see cref="LlmProviderOptions.Provider"/>: Anthropic and Gemini. Inference only - the
/// orchestrator (Epic 5) owns the multi-turn loop and tool-chaining decisions.
/// </summary>
public interface ILlmProvider
{
    /// <summary>Sends one request and returns the model's response.</summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
