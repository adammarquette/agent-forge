using System.Text.RegularExpressions;
using AgentForge.Evals;
using FluentAssertions;

namespace AgentForge.EvalTests;

/// <summary>
/// The repository-level half of the PHI-detection check: the committed artifacts <c>no_phi_in_logs</c>
/// cannot reach because no case run produces them - the eval datasets under <c>evals/golden</c> and the
/// committed baseline and results reports (NFR-SEC-W2-1: "eval datasets, and cost reports must not contain
/// patient identifiers"). The datasets are synthetic by construction and name synthetic patients on
/// purpose, so they are held to two narrower rules: every patient id is in the synthetic namespace, and no
/// string has the shape of a real identifier. The reports are held to the strict rule: none of the golden
/// set's patient identifiers at all.
/// </summary>
public sealed class CommittedArtifactPhiScanTests
{
    private const string SyntheticPatientPrefix = "syn-patient-";

    private static string EvalsDir => Path.GetDirectoryName(GoldenSet.GoldenDir)!;

    private static IReadOnlyList<string> ReportFiles() =>
        [GoldenSet.BaselinePath, .. Directory.EnumerateFiles(Path.Combine(EvalsDir, "results"), "*.json")];

    private static IReadOnlyList<string> DatasetFiles() =>
        [.. Directory.EnumerateFiles(GoldenSet.GoldenDir, "*.json", SearchOption.AllDirectories)];

    private static IReadOnlyList<GoldenCase> GoldenCases() =>
        [.. GoldenSet.CaseFileNames().Select(GoldenSet.Load)];

    /// <summary>
    /// Given the committed baseline and results reports, when they are scanned for every patient identifier
    /// the golden set names - its patient ids, the synthetic namespace itself and the charts' display names -
    /// then none appears. A report carries rates, counts and case ids; case ids are built from lab and drug
    /// names, so the generic clinical words some cases declare as <c>phi_tokens</c> are not scanned for here.
    /// </summary>
    [Fact]
    public void Reports_WhenScanned_CarryNoGoldenCasePatientIdentifier()
    {
        var tokens = GoldenCases()
            .SelectMany(c => PatientIdsOf(c).Append(c.Answer?.Chart?.DisplayName ?? string.Empty))
            .Append(SyntheticPatientPrefix)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hits = ReportFiles()
            .SelectMany(file => PhiArtifactScan.TokenHits(File.ReadAllText(file), tokens)
                .Concat(PhiArtifactScan.IdentifierShapeHits(File.ReadAllText(file)))
                .Select(hit => $"{Path.GetFileName(file)}: {hit}"))
            .ToArray();

        tokens.Should().NotBeEmpty("a scan for nothing passes anything");
        hits.Should().BeEmpty("a committed eval report must not carry a patient identifier or document text");
    }

    /// <summary>
    /// Given the committed eval datasets, when they are scanned for the shape of a real identifier (a US
    /// social security number, a phone number, an email address outside a reserved domain), then none is
    /// found. The synthetic fixtures need none of these, so one appearing is a real record pasted in.
    /// </summary>
    [Fact]
    public void Datasets_WhenScanned_CarryNoRealIdentifierShape()
    {
        var hits = DatasetFiles()
            .SelectMany(file => PhiArtifactScan.IdentifierShapeHits(File.ReadAllText(file))
                .Select(hit => $"{Path.GetFileName(file)}: {hit}"))
            .ToArray();

        hits.Should().BeEmpty("an eval dataset must be synthetic, and a real identifier's shape says it is not");
    }

    /// <summary>
    /// Given every golden case, when the patient ids it names are read, then each is in the synthetic
    /// <c>syn-patient-</c> namespace - so an id copied from a real chart cannot enter the set unnoticed.
    /// </summary>
    [Fact]
    public void Datasets_WhenLoaded_NamePatientsOnlyInTheSyntheticNamespace()
    {
        var foreign = GoldenCases()
            .SelectMany(c => PatientIdsOf(c).Select(id => (c.Id, PatientId: id)))
            .Where(p => !p.PatientId.StartsWith(SyntheticPatientPrefix, StringComparison.Ordinal))
            .Select(p => $"{p.Id}: {p.PatientId}")
            .ToArray();

        foreign.Should().BeEmpty($"every golden-set patient id must start with '{SyntheticPatientPrefix}'");
    }

    /// <summary>
    /// Given a planted identifier of each shape, when the scanner reads it, then it reports it - the red
    /// control without which the two dataset scans above could pass by matching nothing.
    /// </summary>
    [Theory]
    [InlineData("\"note\": \"SSN 123-45-6789\"")]
    [InlineData("\"phone\": \"(555) 867-5309\"")]
    [InlineData("\"phone\": \"555-867-5309\"")]
    [InlineData("\"email\": \"jane.doe@hospital.org\"")]
    public void IdentifierShapeHits_WhenARealIdentifierIsPlanted_ReportsIt(string planted) =>
        PhiArtifactScan.IdentifierShapeHits(planted).Should().ContainSingle();

    /// <summary>
    /// Given the strings the synthetic fixtures do carry - ISO dates, lab values, reserved-domain email - when
    /// the scanner reads them, then it reports nothing, so the scan is not red on the data it exists to allow.
    /// </summary>
    [Theory]
    [InlineData("\"collection_date\": \"2026-07-01\", \"date_of_birth\": \"1960-04-12\"")]
    [InlineData("\"quote\": \"eGFR 44 mL/min/1.73m2\", \"reference_range\": \"135-145\"")]
    [InlineData("\"requester\": \"clinician@example.org\", \"host\": \"syn@openemr.invalid\"")]
    public void IdentifierShapeHits_WhenOnlySyntheticShapesArePresent_ReportsNothing(string text) =>
        PhiArtifactScan.IdentifierShapeHits(text).Should().BeEmpty();

    /// <summary>
    /// Given a report carrying a golden case's patient id, when it is scanned, then the id is reported,
    /// compared case-insensitively - the red control for the report scan.
    /// </summary>
    [Fact]
    public void TokenHits_WhenAPatientIdIsPlantedInAReport_ReportsIt() =>
        PhiArtifactScan.TokenHits("{\"failing\": \"authz case for SYN-PATIENT-ALPHA\"}", ["syn-patient-alpha"])
            .Should().ContainSingle();

    private static IEnumerable<string> PatientIdsOf(GoldenCase testCase)
    {
        if (testCase.Authorization is { } authorization)
        {
            yield return authorization.SessionPatientId;
            foreach (var appointment in authorization.ClinicDayAppointments)
            {
                yield return appointment.PatientId;
            }
        }

        if (testCase.Answer is { } answer)
        {
            yield return answer.PatientId;
        }

        if (testCase.Evidence is { } evidence)
        {
            yield return evidence.PatientId;
        }
    }
}

/// <summary>Plain-text scans over a committed artifact; kept separate so each has a planted red control.</summary>
internal static class PhiArtifactScan
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // Shapes no synthetic fixture needs. Reserved domains (RFC 2606 / RFC 6761) are what fixtures use instead.
    private static readonly Regex[] IdentifierShapes =
    [
        new(@"(?<![\d-])\d{3}-\d{2}-\d{4}(?![\d-])", RegexOptions.CultureInvariant, Timeout),
        new(@"(?<!\d)(?:\(\d{3}\)\s?|\d{3}[-.])\d{3}[-.]\d{4}(?!\d)", RegexOptions.CultureInvariant, Timeout),
        new(@"[A-Za-z0-9._%+-]+@(?![A-Za-z0-9.-]*\b(?:example\.(?:com|org|net)|invalid|test|localhost)\b)[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
            RegexOptions.CultureInvariant, Timeout),
    ];

    /// <summary>Each substring of <paramref name="text"/> shaped like a real identifier.</summary>
    public static IReadOnlyList<string> IdentifierShapeHits(string text) =>
        [.. IdentifierShapes.SelectMany(shape => shape.Matches(text).Select(m => m.Value))];

    /// <summary>Each of <paramref name="tokens"/> present in <paramref name="text"/>, ignoring case.</summary>
    public static IReadOnlyList<string> TokenHits(string text, IEnumerable<string> tokens) =>
        [.. tokens.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))];
}
