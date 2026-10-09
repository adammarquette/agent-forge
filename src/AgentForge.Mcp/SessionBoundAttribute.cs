namespace AgentForge.Mcp;

/// <summary>
/// Marks a request-contract property the <c>McpToolDispatcher</c> forces from the authenticated launch
/// context, so <see cref="McpToolInputSchema"/> never offers it to the model (FR-CHAT-3).
/// </summary>
/// <remarks>
/// The exclusion is declared on the contract rather than matched by name, so a record that calls its
/// patient id something other than <c>PatientId</c> is still excluded. A name list would have advertised
/// it - and any guard written against the same list would have agreed.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SessionBoundAttribute : Attribute;
