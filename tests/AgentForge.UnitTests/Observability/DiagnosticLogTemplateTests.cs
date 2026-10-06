using System.Reflection;
using System.Text.RegularExpressions;
using AgentForge.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Observability;

/// <summary>
/// Pins the two-stream split of CONVENTIONS.md §7 at the one place every log line in this codebase is
/// declared: its source-generated <see cref="LoggerMessageAttribute"/> template. <see cref="AccessAuditLog"/> is the
/// only logger allowed to name a patient (FR-AUTH-4); every other one is diagnostic and must not (NFR-SEC-1).
/// A template is where a raw id enters a line, so a new logger that templates one fails here before it ships.
/// </summary>
public sealed partial class DiagnosticLogTemplateTests
{
    private static readonly IReadOnlyList<LogTemplate> Templates = DiscoverTemplates();

    [Fact]
    public void Discovery_FindsTheLoggersThatUsedToCarryAPatientId_SoTheScanIsNotVacuous()
    {
        // The four loggers a separate change named. If discovery stops reaching an assembly, the scan below passes on nothing.
        Templates.Select(t => t.DeclaringType).Should().Contain(
            ["AgendaRosterServiceLog", "PatientContextServiceLog", "PatientRelationshipAuthorizerLog", "AgendaEndpointsLog", nameof(AccessAuditLog)]);
    }

    [Fact]
    public void DiagnosticLoggers_TemplateNoPatientIdentifier()
    {
        var offenders = Templates
            .Where(t => t.DeclaringType != nameof(AccessAuditLog))
            .SelectMany(t => t.Names.Where(IsPatientIdentifier).Select(name => $"{t.DeclaringType}.{t.Method}: {name}"))
            .ToList();

        offenders.Should().BeEmpty(
            "only AccessAuditLog may name a patient (CONVENTIONS.md §7); log the correlation id, which joins to the audit line");
    }

    [Fact]
    public void AccessAuditLog_StillNamesThePatientOnGrantedAndRefusedAccess()
    {
        // FR-AUTH-4: the audit trail must say which patient. The fix for the diagnostic stream must not reach here.
        Templates.Where(t => t.DeclaringType == nameof(AccessAuditLog))
            .Select(t => (t.Method, HasPatient: t.Names.Contains("PatientId")))
            .Should().BeEquivalentTo(
                [(nameof(AccessAuditLog.RecordAccess), true), (nameof(AccessAuditLog.RecordRefusal), true)]);
    }

    private static bool IsPatientIdentifier(string name) => PatientIdentifierName().IsMatch(name);

    // A resource id that resolves to one patient's record. {PatientFound} (a bool) and {DocumentType} (an enum) do not.
    [GeneratedRegex("^(patient|subject|document|binary|encounter)(id|identifier|ref|reference)?$|mrn$", RegexOptions.IgnoreCase)]
    private static partial Regex PatientIdentifierName();

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)[^}]*\}")]
    private static partial Regex Placeholder();

    private static List<LogTemplate> DiscoverTemplates()
    {
        // Walk from the host assembly, which references every src project, so a new project is scanned unasked.
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>([typeof(Program).Assembly]);
        while (pending.TryPop(out var assembly))
        {
            if (!seen.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()
                         .Where(r => r.Name!.StartsWith("AgentForge.", StringComparison.Ordinal) && !seen.ContainsKey(r.Name)))
            {
                pending.Push(Assembly.Load(reference));
            }
        }

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        return
        [
            .. from assembly in seen.Values
               from type in assembly.GetTypes()
               from method in type.GetMethods(all)
               let attribute = method.GetCustomAttribute<LoggerMessageAttribute>()
               where attribute is not null
               select new LogTemplate(
                   type.Name,
                   method.Name,
                   [
                       .. Placeholder().Matches(attribute.Message ?? string.Empty).Select(m => m.Groups[1].Value),
                       .. method.GetParameters()
                           .Where(p => !typeof(ILogger).IsAssignableFrom(p.ParameterType) && !typeof(Exception).IsAssignableFrom(p.ParameterType))
                           .Select(p => p.Name!),
                   ]),
        ];
    }

    private sealed record LogTemplate(string DeclaringType, string Method, IReadOnlyList<string> Names);
}
