namespace AgentForge.GenerateFixtureDocuments;

/// <summary>One printed result row of the lab panel.</summary>
public sealed record LabRow(string Test, string Result, string Units, string Reference, string Flag)
{
    /// <summary>The row as its words read left to right - what a verbatim citation of the row quotes.</summary>
    public string Quote => string.Join(' ', new[] { Test, Result, Units, Reference, Flag }.Where(s => s.Length > 0));
}

/// <summary>
/// A one-page synthetic cardiology lab panel (<c>lab_pdf</c>): a digital PDF with a real text layer, so
/// a verbatim quote of any row resolves to its glyphs and the citation carries an exact bounding box.
/// Values sit around real reference ranges so the abnormal flags mean something; they are not advice.
/// </summary>
public static class SyntheticLabPanel
{
    /// <summary>File name under <c>tests/fixtures/documents/</c>.</summary>
    public const string FileName = "synthetic-lab-panel.pdf";

    /// <summary>IANA media type.</summary>
    public const string MediaType = "application/pdf";

    /// <summary>Specimen collection date printed on the report.</summary>
    public const string CollectionDate = "2026-09-01";

    /// <summary>The result rows, in print order.</summary>
    public static IReadOnlyList<LabRow> Rows { get; } =
    [
        new("Potassium", "5.9", "mmol/L", "3.5-5.1", "H"),
        new("Creatinine", "1.6", "mg/dL", "0.7-1.3", "H"),
        new("Sodium", "138", "mmol/L", "135-145", ""),
        new("NT-proBNP", "1450", "pg/mL", "<300", "H"),
    ];

    private static readonly double[] Columns = [54, 200, 270, 350, 450];

    /// <summary>What the panel is and what a correct extraction yields: one fact per printed row.</summary>
    public static DocumentManifest Manifest { get; } = new()
    {
        FileName = FileName,
        MediaType = MediaType,
        DocumentType = DocumentManifest.LabPdf,
        Layout = DocumentLayout.Clean,
        Description = "The hermetic tier's original one-page panel: potassium, creatinine, sodium and NT-proBNP "
            + "for a patient on spironolactone.",
        Patient = SyntheticCohort.Harold,
        PageCount = 1,
        HasTextLayer = true,
        Facts = [.. Rows.Select(r => ExpectedFact.Lab(r, CollectionDate))],
    };

    /// <summary>Renders the panel. Same bytes on every call.</summary>
    public static byte[] Render()
    {
        var runs = new List<PdfTextRun>
        {
            new(54, 740, 14, true, "AgentForge Synthetic Laboratory"),
            new(54, 722, 9, false, "TEST FIXTURE - synthetic data, not a real patient or a real report"),
            new(54, 692, 11, false, $"Patient: {SyntheticCohort.PatientName}"),
            new(320, 692, 11, false, $"DOB: {SyntheticCohort.BirthDate}"),
            new(450, 692, 11, false, $"MRN: {SyntheticCohort.Mrn}"),
            new(54, 674, 11, false, $"Collected: {CollectionDate}"),
            new(54, 656, 11, false, "Panel: Cardiology basic chemistry"),
        };

        string[] header = ["Test", "Result", "Units", "Reference", "Flag"];
        for (var c = 0; c < header.Length; c++)
        {
            runs.Add(new(Columns[c], 620, 11, true, header[c]));
        }

        var y = 600.0;
        foreach (var row in Rows)
        {
            string[] cells = [row.Test, row.Result, row.Units, row.Reference, row.Flag];
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c].Length > 0)
                {
                    runs.Add(new(Columns[c], y, 11, false, cells[c]));
                }
            }

            y -= 18;
        }

        runs.Add(new(54, y - 20, 9, false, "H = above reference range. End of report."));
        return SyntheticPdf.Render(runs);
    }
}
