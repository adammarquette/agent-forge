using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace AgentForge.Api.Observability;

/// <summary>
/// Where the access-audit trail may be written. It is the one stream that names the patient (FR-AUTH-4), so it is
/// kept out of every OpenTelemetry log exporter - Loki and any other OTLP sink - and written by the built-in
/// console provider only (CONVENTIONS.md §7; maintainer ruling on a separate change item 1, option A).
/// </summary>
public static class AccessAuditLogRouting
{
    private const char Wildcard = '*';

    private static readonly string OpenTelemetryProviderName = typeof(OpenTelemetryLoggerProvider).FullName!;

    private static readonly string? OpenTelemetryProviderAlias =
        typeof(OpenTelemetryLoggerProvider).GetCustomAttribute<ProviderAliasAttribute>()?.Alias;

    /// <summary>
    /// Turns the <see cref="AccessAudit.CategoryName"/> category off for the <see cref="OpenTelemetryLoggerProvider"/>
    /// and for no other provider. A code rule rather than an <c>appsettings</c> entry, so it holds in every
    /// environment rather than depending on which file one loads. Applied after every configuration source, and it
    /// removes any OpenTelemetry rule that would otherwise outrank it, so no <c>Logging</c> key - in a file or an
    /// environment variable - can put the audit trail back on an exporter.
    /// </summary>
    public static ILoggingBuilder KeepAccessAuditOffOpenTelemetry(this ILoggingBuilder logging)
    {
        logging.Services.PostConfigure<LoggerFilterOptions>(options => EnforceAccessAuditRule(options.Rules));
        return logging;
    }

    // The framework's selector picks the provider rule with the longest matching category, and the later one on a
    // tie. Ours is appended last, so only a strictly longer matching rule could beat it - and those are removed.
    private static void EnforceAccessAuditRule(IList<LoggerFilterRule> rules)
    {
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (CouldOutrankTheAuditRule(rules[i]))
            {
                rules.RemoveAt(i);
            }
        }

        rules.Add(new LoggerFilterRule(OpenTelemetryProviderName, AccessAudit.CategoryName, LogLevel.None, filter: null));
    }

    private static bool CouldOutrankTheAuditRule(LoggerFilterRule rule) =>
        IsOpenTelemetryProvider(rule.ProviderName)
        && rule.CategoryName is { } pattern
        && pattern.Length > AccessAudit.CategoryName.Length
        && MatchesAuditCategory(pattern);

    // Case-insensitive, which is wider than the selector's ordinal provider comparison and so errs towards removal.
    private static bool IsOpenTelemetryProvider(string? providerName) =>
        string.Equals(providerName, OpenTelemetryProviderName, StringComparison.OrdinalIgnoreCase)
        || (OpenTelemetryProviderAlias is not null
            && string.Equals(providerName, OpenTelemetryProviderAlias, StringComparison.OrdinalIgnoreCase));

    // The selector's own category match: one optional wildcard, prefix and suffix compared case-insensitively.
    private static bool MatchesAuditCategory(string pattern)
    {
        var wildcard = pattern.IndexOf(Wildcard, StringComparison.Ordinal);
        var prefix = wildcard < 0 ? pattern : pattern[..wildcard];
        var suffix = wildcard < 0 ? string.Empty : pattern[(wildcard + 1)..];
        return AccessAudit.CategoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && AccessAudit.CategoryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
