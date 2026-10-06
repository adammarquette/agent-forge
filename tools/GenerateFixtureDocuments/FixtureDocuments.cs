namespace AgentForge.GenerateFixtureDocuments;

/// <summary>One committed file under <see cref="FixtureDocuments.RelativeDirectory"/> and how to render its bytes.</summary>
public sealed record FixtureFile(string FileName, Func<byte[]> Render);

/// <summary>One generated document: what it is and yields (its manifest), and its bytes.</summary>
public sealed record FixtureDocument(DocumentManifest Manifest, Func<byte[]> Render)
{
    /// <summary>File name under <see cref="FixtureDocuments.RelativeDirectory"/>.</summary>
    public string FileName => Manifest.FileName;

    /// <summary>IANA media type the file is uploaded as.</summary>
    public string MediaType => Manifest.MediaType;

    /// <summary>The committed manifest's file name, beside the document.</summary>
    public string ManifestFileName => $"{FileName}.manifest.json";
}

/// <summary>
/// The committed synthetic cardiology document set and the one place that says what it contains. Everything
/// is rendered from code with no clock, font file or compression library, and the scans' "randomness" is a
/// fixed-seed integer sequence, so regenerating is byte-identical on any machine - which is what lets the
/// secret scan pin each binary by sha256 and a test hold every committed file to this generator.
/// </summary>
public static class FixtureDocuments
{
    /// <summary>Where the set is committed, relative to the repository root.</summary>
    public const string RelativeDirectory = "tests/fixtures/documents";

    /// <summary>Every document in the set: the lab reports first, then the intake forms.</summary>
    public static IReadOnlyList<FixtureDocument> All { get; } =
    [
        new(SyntheticLabPanel.Manifest, SyntheticLabPanel.Render),
        .. LabReports.All,
        new(SyntheticIntakeForm.Manifest, SyntheticIntakeForm.Render),
        .. IntakeForms.All,
    ];

    /// <summary>Every committed file: each document, then each document's manifest.</summary>
    public static IReadOnlyList<FixtureFile> Files { get; } =
    [
        .. All.Select(d => new FixtureFile(d.FileName, d.Render)),
        .. All.Select(d => new FixtureFile(d.ManifestFileName, d.Manifest.ToJson)),
    ];
}
