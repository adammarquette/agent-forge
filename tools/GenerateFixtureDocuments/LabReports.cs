namespace AgentForge.GenerateFixtureDocuments;

/// <summary>
/// The cardiology lab reports of the set beyond <see cref="SyntheticLabPanel"/>: a lipid panel, a BMP, serial
/// cardiac markers, warfarin INR monitoring and HbA1c with renal markers, each in a layout the citation path
/// is brittle to, plus a scanned copy. Values sit in and around real reference ranges so the flags mean
/// something; none of it is advice. A separate change
/// </summary>
public static class LabReports
{
    private const string Pdf = "application/pdf";

    private static readonly double[] Columns = [54, 200, 270, 350, 450];

    /// <summary>Lipid panel rows, shared by the digital report and its scanned copy.</summary>
    public static IReadOnlyList<LabRow> LipidRows { get; } =
    [
        new("Total cholesterol", "232", "mg/dL", "<200", "H"),
        new("LDL cholesterol", "151", "mg/dL", "<100", "H"),
        new("HDL cholesterol", "44", "mg/dL", ">=40", ""),
        new("Triglycerides", "186", "mg/dL", "<150", "H"),
        new("Non-HDL cholesterol", "188", "mg/dL", "<130", "H"),
    ];

    private const string LipidCollected = "2026-08-28";

    /// <summary>Every lab report this class contributes to the set.</summary>
    public static IReadOnlyList<FixtureDocument> All { get; } =
    [
        LipidPanel(),
        TwoColumnBmp(),
        WrappedCardiacMarkers(),
        MultiPageInr(),
        SplitUnits(),
        ScannedLipidPanel(),
    ];

    private static FixtureDocument LipidPanel()
    {
        var patient = SyntheticCohort.Eleanor;
        var runs = Header(patient, "Lipid panel (fasting)", LipidCollected);
        TableHeader(runs, 620, Columns);
        var y = 600.0;
        foreach (var row in LipidRows)
        {
            Row(runs, y, Columns, 11, [row.Test, row.Result, row.Units, row.Reference, row.Flag]);
            y -= 18;
        }

        Footer(runs, y - 20);
        return Text(
            "lab-lipid-panel.pdf", DocumentLayout.Clean, patient, [runs],
            "Clean one-table lipid panel: every row one line in reading order, the case the exact matcher handles.",
            [.. LipidRows.Select(r => ExpectedFact.Lab(r, LipidCollected))]);
    }

    private static FixtureDocument TwoColumnBmp()
    {
        const string collected = "2026-09-03";
        var patient = SyntheticCohort.Walter;
        LabRow[] left =
        [
            new("Sodium", "136", "mmol/L", "135-145", ""),
            new("Potassium", "5.3", "mmol/L", "3.5-5.1", "H"),
            new("Chloride", "101", "mmol/L", "98-107", ""),
            new("CO2", "23", "mmol/L", "22-29", ""),
        ];
        LabRow[] right =
        [
            new("BUN", "28", "mg/dL", "7-20", "H"),
            new("Creatinine", "1.5", "mg/dL", "0.7-1.3", "H"),
            new("Glucose", "112", "mg/dL", "70-99", "H"),
            new("Calcium", "9.4", "mg/dL", "8.6-10.3", ""),
        ];
        double[] leftColumns = [54, 140, 172, 222, 268];
        double[] rightColumns = [318, 404, 436, 486, 532];

        var runs = Header(patient, "Basic metabolic panel", collected);
        TableHeader(runs, 620, leftColumns, 9);
        TableHeader(runs, 620, rightColumns, 9);
        var y = 602.0;
        for (var i = 0; i < left.Length; i++)
        {
            // Drawn across the line, left row then right row, as a report writer fills a two-up grid.
            Row(runs, y, leftColumns, 9, [left[i].Test, left[i].Result, left[i].Units, left[i].Reference, left[i].Flag]);
            Row(runs, y, rightColumns, 9, [right[i].Test, right[i].Result, right[i].Units, right[i].Reference, right[i].Flag]);
            y -= 15;
        }

        Footer(runs, y - 20);
        return Text(
            "lab-bmp-two-column.pdf", DocumentLayout.TwoColumn, patient, [runs],
            "BMP printed as two tables side by side, so every text line carries two rows.",
            [.. left.Concat(right).Select(r => ExpectedFact.Lab(r, collected))]);
    }

    private static FixtureDocument WrappedCardiacMarkers()
    {
        const string collected = "2026-09-05";
        var patient = SyntheticCohort.Rosalind;
        (string[] Name, LabRow Row, string Prior)[] rows =
        [
            (["Troponin I, high", "sensitivity, 0 h"], new("Troponin I, high sensitivity, 0 h", "18", "ng/L", "<=34", ""), "-"),
            (["Troponin I, high", "sensitivity, 3 h"], new("Troponin I, high sensitivity, 3 h", "61", "ng/L", "<=34", "H"), "-"),
            (["BNP"], new("BNP", "412", "pg/mL", "<100", "H"), "288"),
            (["NT-proBNP"], new("NT-proBNP", "2350", "pg/mL", "<900", "H"), "1720"),
            (["CK-MB"], new("CK-MB", "7.9", "ng/mL", "<=6.6", "H"), "3.1"),
        ];
        double[] columns = [54, 190, 240, 300, 370, 410];

        var runs = Header(patient, "Cardiac markers - chest pain protocol", collected);
        string[] header = ["Test", "Result", "Units", "Reference", "Flag", "Prior (2026-07-30)"];
        for (var c = 0; c < header.Length; c++)
        {
            runs.Add(new(columns[c], 620, 10, true, header[c]));
        }

        var y = 600.0;
        foreach (var (name, row, prior) in rows)
        {
            // The name cell is drawn whole - both of its lines - before the cells to its right.
            for (var l = 0; l < name.Length; l++)
            {
                runs.Add(new(columns[0], y - (l * 12), 10, false, name[l]));
            }

            string[] cells = [row.Result, row.Units, row.Reference, row.Flag, prior];
            for (var c = 0; c < cells.Length; c++)
            {
                if (cells[c].Length > 0)
                {
                    runs.Add(new(columns[c + 1], y, 10, false, cells[c]));
                }
            }

            y -= name.Length > 1 ? 30 : 18;
        }

        Footer(runs, y - 20);
        return Text(
            "lab-cardiac-markers-wrapped.pdf", DocumentLayout.WrappedTable, patient, [runs],
            "Serial high-sensitivity troponin, BNP, NT-proBNP and CK-MB in a six-column table whose long test "
                + "names wrap onto a second line of their cell.",
            [.. rows.Select(r => ExpectedFact.Lab(r.Row, collected))]);
    }

    private static FixtureDocument MultiPageInr()
    {
        const string collected = "2026-09-01";
        var patient = SyntheticCohort.Walter;
        LabRow[] current =
        [
            new("PT", "27.4", "sec", "11.0-13.5", "H"),
            new("INR", "2.6", "", "2.0-3.0", ""),
            new("Hemoglobin", "12.9", "g/dL", "13.5-17.5", "L"),
        ];
        (string Date, LabRow Row)[] history =
        [
            ("2026-08-04", new("INR", "3.4", "", "2.0-3.0", "H")),
            ("2026-08-18", new("INR", "2.9", "", "2.0-3.0", "")),
        ];

        var page1 = Header(patient, "Coagulation - warfarin monitoring", collected, "Page 1 of 2");
        TableHeader(page1, 620, Columns);
        var y = 600.0;
        foreach (var row in current)
        {
            Row(page1, y, Columns, 11, [row.Test, row.Result, row.Units, row.Reference, row.Flag]);
            y -= 18;
        }

        page1.Add(new(54, y - 20, 9, false, "Target INR 2.0-3.0 on warfarin. Continued on page 2."));

        var page2 = Header(patient, "Anticoagulation history", collected, "Page 2 of 2");
        double[] historyColumns = [54, 160, 230, 300, 400];
        string[] header = ["Collected", "Test", "Result", "Target", "Flag"];
        for (var c = 0; c < header.Length; c++)
        {
            page2.Add(new(historyColumns[c], 620, 11, true, header[c]));
        }

        y = 600.0;
        foreach (var (date, row) in history)
        {
            Row(page2, y, historyColumns, 11, [date, row.Test, row.Result, row.Reference, row.Flag]);
            y -= 18;
        }

        Footer(page2, y - 20);
        ExpectedFact[] facts =
        [
            .. current.Select(r => ExpectedFact.Lab(r, collected)),
            .. history.Select(h => ExpectedFact.Lab(h.Row, h.Date, page: 2, quote: $"{h.Date} {h.Row.Quote}")),
        ];
        return Text(
            "lab-inr-warfarin-multipage.pdf", DocumentLayout.MultiPage, patient, [page1, page2],
            "Two pages: the current PT/INR and hemoglobin on page 1, the prior INR results on page 2.",
            facts);
    }

    private static FixtureDocument SplitUnits()
    {
        const string collected = "2026-09-02";
        var patient = SyntheticCohort.Harold;
        double[] columns = [54, 200, 260, 370, 450];
        var runs = Header(patient, "Diabetes and renal markers", collected);
        TableHeader(runs, 620, columns);

        LabRow a1c = new("Hemoglobin A1c", "7.8", "%", "4.0-5.6", "H");
        Row(runs, 600, columns, 11, [a1c.Test, a1c.Result, a1c.Units, a1c.Reference, a1c.Flag]);

        // Value and unit glued into one token, the units column left empty.
        LabRow glucose = new("Glucose, fasting", "131", "mg/dL", "70-99", "H");
        Row(runs, 582, columns, 11, [glucose.Test, "131mg/dL", string.Empty, glucose.Reference, glucose.Flag]);

        // The unit's exponent is a raised, smaller glyph - "1.73 m2" as printed.
        LabRow egfr = new("eGFR", "52", "mL/min/1.73 m2", ">=60", "L");
        Row(runs, 564, columns, 11, [egfr.Test, egfr.Result, "mL/min/1.73 m"]);
        runs.Add(new(columns[2] + 72.74, 568, 7, false, "2")); // 72.74pt: Helvetica 11pt width of "mL/min/1.73 m"
        Row(runs, 564, columns[3..], 11, [egfr.Reference, egfr.Flag]);

        // The unit is written last, after the footer, as some report writers fill one column at a time.
        LabRow uacr = new("Albumin/creatinine ratio", "38", "mg/g", "<30", "H");
        Row(runs, 546, columns, 11, [uacr.Test, uacr.Result, string.Empty, uacr.Reference, uacr.Flag]);
        Footer(runs, 526);
        runs.Add(new(columns[2], 546, 11, false, uacr.Units));

        return Text(
            "lab-hba1c-split-units.pdf", DocumentLayout.SplitUnits, patient, [runs],
            "HbA1c and renal markers where value and unit do not sit in one clean token: a unit glued to its "
                + "value, a superscript exponent, and a unit drawn out of order in the content stream.",
            [
                ExpectedFact.Lab(a1c, collected),
                ExpectedFact.Lab(glucose, collected, quote: "Glucose, fasting 131mg/dL 70-99 H"),
                ExpectedFact.Lab(egfr, collected),
                ExpectedFact.Lab(uacr, collected),
            ]);
    }

    private static FixtureDocument ScannedLipidPanel()
    {
        var patient = SyntheticCohort.Eleanor;
        List<string> lines =
        [
            "AgentForge Synthetic Laboratory",
            "TEST FIXTURE - synthetic, not a real patient",
            string.Empty,
            $"Patient: {patient.Name}",
            $"DOB: {patient.BirthDate}  MRN: {patient.Mrn}",
            $"Collected: {LipidCollected}",
            "Panel: Lipid panel (fasting)",
            string.Empty,
            Cells("Test", "Result", "Units", "Ref", "Flag"),
        ];
        lines.AddRange(LipidRows.Select(r => Cells(r.Test, r.Result, r.Units, r.Reference, r.Flag)));
        lines.Add(string.Empty);
        lines.Add("H = above reference range. End of report.");

        // About 1.2 degrees of skew.
        var page = new ScannedPage(lines, sin: 0.0209, cos: 0.99978, seed: 0x0692_0001);
        return new(
            new DocumentManifest
            {
                FileName = "lab-lipid-panel-scanned.pdf",
                MediaType = Pdf,
                DocumentType = DocumentManifest.LabPdf,
                Layout = DocumentLayout.Scanned,
                Description = "A scanned copy of the lipid panel: 75 dpi, skewed about 1.2 degrees, speckled, "
                    + "image only - no text layer, so no quote on it can be checked.",
                Patient = patient,
                PageCount = 1,
                HasTextLayer = false,
                Facts = [.. LipidRows.Select(r =>
                {
                    var printed = Cells(r.Test, r.Result, r.Units, r.Reference, r.Flag).TrimEnd();
                    return ExpectedFact.Lab(r, LipidCollected, region: page.RegionOf(printed));
                })],
            },
            page.RenderPdf);

        // Fixed-width cells, as a line printer lays out a table.
        static string Cells(string test, string result, string units, string reference, string flag) =>
            $"{test,-20}{result,-7}{units,-7}{reference,-7}{flag}";
    }

    private static List<PdfTextRun> Header(SyntheticPatient patient, string panel, string collected, string? pageLabel = null)
    {
        var runs = new List<PdfTextRun>
        {
            new(54, 740, 14, true, "AgentForge Synthetic Laboratory"),
            new(54, 722, 9, false, "TEST FIXTURE - synthetic data, not a real patient or a real report"),
            new(54, 692, 11, false, $"Patient: {patient.Name}"),
            new(320, 692, 11, false, $"DOB: {patient.BirthDate}"),
            new(450, 692, 11, false, $"MRN: {patient.Mrn}"),
            new(54, 674, 11, false, $"Collected: {collected}"),
            new(54, 656, 11, false, $"Panel: {panel}"),
        };
        if (pageLabel is not null)
        {
            runs.Add(new(450, 674, 11, false, pageLabel));
        }

        return runs;
    }

    private static void TableHeader(List<PdfTextRun> runs, double y, double[] columns, double size = 11)
    {
        string[] header = ["Test", "Result", "Units", "Reference", "Flag"];
        for (var c = 0; c < header.Length; c++)
        {
            runs.Add(new(columns[c], y, size, true, header[c]));
        }
    }

    private static void Row(List<PdfTextRun> runs, double y, double[] columns, double size, string[] cells)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            if (cells[c].Length > 0)
            {
                runs.Add(new(columns[c], y, size, false, cells[c]));
            }
        }
    }

    private static void Footer(List<PdfTextRun> runs, double y) =>
        runs.Add(new(54, y, 9, false, "H = above, L = below reference range. End of report."));

    private static FixtureDocument Text(
        string fileName, string layout, SyntheticPatient patient, IReadOnlyList<IReadOnlyList<PdfTextRun>> pages,
        string description, IReadOnlyList<ExpectedFact> facts) =>
        new(
            new DocumentManifest
            {
                FileName = fileName,
                MediaType = Pdf,
                DocumentType = DocumentManifest.LabPdf,
                Layout = layout,
                Description = description,
                Patient = patient,
                PageCount = pages.Count,
                HasTextLayer = true,
                Facts = facts,
            },
            () => SyntheticPdf.RenderPages(pages));
}
