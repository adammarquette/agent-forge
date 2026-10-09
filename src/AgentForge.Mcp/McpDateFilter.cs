using System.Globalization;

namespace AgentForge.Mcp;

/// <summary>
/// Shared validation and parsing for the FHIR date-prefixed filter strings (e.g.
/// <c>ge2026-01-01</c>) several tool requests accept, per the search-param shape confirmed in
/// INTERFACES.md §B.2.
/// </summary>
public static class McpDateFilter
{
    /// <summary>
    /// Requires one of the FHIR search-prefix codes followed by a date, e.g. <c>ge2026-01-01</c>.
    /// </summary>
    /// <remarks>
    /// <c>[0-9]</c> rather than <c>\d</c> deliberately: this same literal is now advertised to the
    /// model as a JSON Schema <c>pattern</c>, which is ECMA-262, where <c>\d</c> is ASCII-only - while
    /// .NET's <c>\d</c> is every Unicode decimal digit. The two dialects therefore disagreed on e.g.
    /// <c>ge٢٠٢٦</c>: accepted by the server, then unparseable by <see cref="ExtractDate"/>. Spelling
    /// the class out makes one literal correct in both.
    /// </remarks>
    public const string Pattern = "^(eq|ne|gt|lt|ge|le|sa|eb|ap)[0-9]{4}(-[0-9]{2}(-[0-9]{2})?)?$";

    /// <summary>Validation failure message for <see cref="Pattern"/>.</summary>
    public const string ErrorMessage =
        "must be a FHIR date-prefixed filter, e.g. 'ge2026-01-01' (prefix required: eq/ne/gt/lt/ge/le/sa/eb/ap).";

    private const int PrefixLength = 2;

    /// <summary>
    /// Extracts the date portion of a filter already validated against <see cref="Pattern"/>
    /// (e.g. <c>ge2026-01-01</c> -&gt; 2026-01-01T00:00:00Z), for client-side comparisons where
    /// the FHIR server can't filter server-side (e.g. MedicationRequest has no date search param).
    /// </summary>
    public static DateTimeOffset ExtractDate(string fhirDateFilter) =>
        DateTimeOffset.Parse(
            fhirDateFilter[PrefixLength..],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
