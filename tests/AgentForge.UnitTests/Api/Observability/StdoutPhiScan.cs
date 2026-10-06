using System.Text.RegularExpressions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>The synthetic identifiers one host run is scanned for.</summary>
/// <param name="PatientId">The patient id; the audit trail may carry it, in its own <c>patient=</c> field only.</param>
/// <param name="ClinicianId">The clinician id; the audit trail may carry it, in its own <c>clinician=</c> field only.</param>
/// <param name="DisplayName">A name token (the family name); no line may carry it, audit lines included.</param>
/// <param name="DocumentIds">Document ids the run touches; no line may carry them, audit lines included.</param>
internal sealed record ScanIdentifiers(string PatientId, string ClinicianId, string DisplayName, IReadOnlyList<string>? DocumentIds = null)
{
    public IEnumerable<string> All => [PatientId, ClinicianId, DisplayName, .. DocumentIds ?? []];
}

/// <summary>
/// Reads what both console sinks wrote and reports every line naming a scanned identifier outside the one place
/// it may appear. The exemption follows the rules for the <c>no_phi_in_logs</c> rubric: it is bound to the
/// <c>AgentForge.AccessAudit</c> category <em>and</em> the audit template, and it covers only the identifier's own
/// templated field - the patient id in <c>patient=</c>, the clinician id in <c>clinician=</c>. Everything else on
/// an audit line is scanned, and a line that does not match the template is scanned whole (fail closed).
/// </summary>
internal static class StdoutPhiScan
{
    public const string AccessAuditCategory = "AgentForge.AccessAudit";

    private const string AuditPrefix = "ACCESS AUDIT: ";

    private static readonly Regex ConsoleEntryHeader = new(
        @"^(trce|dbug|info|warn|fail|crit): (?<category>[^\[]+)\[", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*m", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Anchored on the template's own prefix, so only its first, templated fields can be exempt.
    private static readonly Regex AuditTemplate = new(
        @"^ACCESS AUDIT: clinician=(?<clinician>\S*) (?:accessed|REFUSED) patient=(?<patient>\S*) via tool=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every line that names an identifier where it may not appear.</summary>
    public static List<string> LinesLeaking(string stdout, ScanIdentifiers ids) =>
        [.. Classify(stdout)
            .Select(line => (line.Text, Scannable: line.IsAuditEvent ? WithoutOwnFields(line.Text.TrimStart(), ids) : line.Text))
            .Where(line => ids.All.Any(token => line.Scannable.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(line => line.Text)];

    /// <summary>The access-audit lines whose templated <c>patient=</c> field names <paramref name="patientId"/>.</summary>
    public static List<string> AuditLinesNaming(string stdout, string patientId) =>
        [.. Classify(stdout)
            .Where(line => line.IsAuditEvent && line.Text.Contains($"patient={patientId} ", StringComparison.Ordinal))
            .Select(line => line.Text)];

    /// <summary>
    /// The built-in console formatter writes an entry as a <c>level: category[event]</c> header followed by lines
    /// indented six spaces. A line is an audit event only when it continues an <c>AgentForge.AccessAudit</c> header
    /// and is the <c>ACCESS AUDIT:</c> message. Everything else - OpenTelemetry exporter records, scopes, exception
    /// text - is diagnostic.
    /// </summary>
    private static IEnumerable<(string Text, bool IsAuditEvent)> Classify(string stdout)
    {
        var inAuditEntry = false;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = AnsiEscape.Replace(raw.TrimEnd('\r'), string.Empty);
            var header = ConsoleEntryHeader.Match(line);
            if (header.Success)
            {
                inAuditEntry = header.Groups["category"].Value == AccessAuditCategory;
                yield return (line, false);
                continue;
            }

            inAuditEntry &= line.StartsWith("      ", StringComparison.Ordinal);
            yield return (line, inAuditEntry && line.TrimStart().StartsWith(AuditPrefix, StringComparison.Ordinal));
        }
    }

    // Drops a templated field's value only when it is the identifier that field exists for, so a patient id in
    // clinician= or a name in patient= stays on the line to be found.
    private static string WithoutOwnFields(string message, ScanIdentifiers ids)
    {
        var match = AuditTemplate.Match(message);
        if (!match.Success)
        {
            return message;
        }

        var patient = match.Groups["patient"];
        var clinician = match.Groups["clinician"];

        // patient= follows clinician=, so removing it first leaves the clinician group's index valid.
        var text = patient.Value == ids.PatientId ? message.Remove(patient.Index, patient.Length) : message;
        return clinician.Value == ids.ClinicianId ? text.Remove(clinician.Index, clinician.Length) : text;
    }
}
