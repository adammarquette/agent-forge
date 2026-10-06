namespace AgentForge;

/// <summary>
/// The logger category of the access-audit trail (FR-AUTH-4): <c>ILogger&lt;AccessAudit&gt;</c> logs under
/// <see cref="CategoryName"/>, and every <see cref="Mcp.AccessAuditLog"/> method takes exactly that logger, so an audit
/// line can never be written under a caller's diagnostic category. The host keys its routing on this name - the
/// category is excluded from the OpenTelemetry log provider and so never reaches Loki or any OTLP sink; it is
/// written to the console only (CONVENTIONS.md §7). Declared in the root namespace so the category is
/// the short, stable <c>AgentForge.AccessAudit</c>.
/// </summary>
public sealed class AccessAudit
{
    /// <summary>The category name every access-audit line is written under.</summary>
    public const string CategoryName = "AgentForge.AccessAudit";

    private AccessAudit()
    {
    }
}
