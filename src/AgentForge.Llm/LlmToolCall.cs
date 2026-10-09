namespace AgentForge.Llm;

/// <summary>One tool the model asked to invoke this turn.</summary>
/// <param name="Id">Provider-assigned call id - echo it back with the tool's result.</param>
/// <param name="ToolName">Which tool, matching an <see cref="LlmToolDefinition.Name"/> offered on the request.</param>
/// <param name="ArgumentsJson">The tool's input arguments, as a raw JSON string.</param>
/// <param name="ReplayToken">
/// Opaque provider data the call must be replayed with when it goes back as history - Gemini's
/// <c>thoughtSignature</c>; null for a provider that has none. Never logged.
/// </param>
public sealed record LlmToolCall(string Id, string ToolName, string ArgumentsJson, string? ReplayToken = null);
