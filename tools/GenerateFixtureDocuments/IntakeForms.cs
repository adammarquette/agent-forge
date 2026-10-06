namespace AgentForge.GenerateFixtureDocuments;

/// <summary>
/// The cardiology intake forms of the set beyond <see cref="SyntheticIntakeForm"/>: a form filled in
/// electronically (a real text layer), a two-page form, and a skewed low-resolution scan. Each lists a chief
/// concern, current cardiac medications, allergies and a family history of premature coronary disease.
/// </summary>
public static class IntakeForms
{
    private const string Pdf = "application/pdf";
    private const double Left = 54;

    /// <summary>Every intake form this class contributes to the set.</summary>
    public static IReadOnlyList<FixtureDocument> All { get; } = [Digital(), MultiPage(), Scanned()];

    private static FixtureDocument Digital()
    {
        var patient = SyntheticCohort.Eleanor;
        var name = $"Name: {patient.Name}";
        const string concern = "Palpitations and a racing heartbeat for two weeks";
        (string Name, string Dose)[] medications =
        [
            ("Metoprolol succinate", "50 mg daily"),
            ("Apixaban", "5 mg twice daily"),
            ("Atorvastatin", "40 mg nightly"),
        ];
        const string allergy = "Sulfa drugs (hives)";
        const string family = "Brother - coronary artery disease at 48";

        var lines = new Lines(740);
        lines.Title("AgentForge Synthetic Cardiology Intake Form");
        lines.Add(name).Add($"Date of birth: {patient.BirthDate}").Add("Sex: Female").Add($"MRN: {patient.Mrn}").Gap();
        lines.Label("Chief concern:").Add(concern).Gap();
        lines.Label("Current medications:");
        foreach (var (medication, dose) in medications)
        {
            lines.Add($"{medication} {dose}");
        }

        lines.Gap().Label("Allergies:").Add(allergy).Gap();
        lines.Label("Family history:").Add(family).Gap();
        lines.Add("Signature: ____________________    Date: 2026-08-28");

        return Form(
            "intake-form-digital.pdf", DocumentLayout.Clean, patient, [lines.Runs], hasTextLayer: true,
            "An intake form filled in electronically, so it has a real text layer and each line is one run.",
            [
                Demographics(patient, name),
                Fact(ExpectedFact.IntakeChiefConcern, "chief_concern", concern),
                .. medications.Select(m => Medication(m.Name, m.Dose)),
                Fact(ExpectedFact.IntakeAllergy, "allergy", allergy),
                Fact(ExpectedFact.IntakeFamilyHistory, "family_history", family),
            ]);
    }

    private static FixtureDocument MultiPage()
    {
        var patient = SyntheticCohort.Walter;
        var name = $"Name: {patient.Name}";
        const string concern = "Short of breath climbing one flight of stairs";
        (string Name, string Dose)[] medications =
        [
            ("Warfarin", "5 mg daily"),
            ("Losartan", "50 mg daily"),
            ("Furosemide", "40 mg daily"),
            ("Rosuvastatin", "20 mg nightly"),
        ];
        const string allergy = "Lisinopril (angioedema)";
        string[] family = ["Mother - myocardial infarction at 55", "Father - stroke at 60"];

        var page1 = new Lines(740);
        page1.Title("AgentForge Synthetic Cardiology Intake Form");
        page1.Add("Page 1 of 2").Gap();
        page1.Add(name).Add($"Date of birth: {patient.BirthDate}").Add("Sex: Male").Add($"MRN: {patient.Mrn}").Gap();
        page1.Label("Chief concern:").Add(concern).Gap();
        page1.Add("Continued on page 2.");

        var page2 = new Lines(740);
        page2.Add($"Patient: {patient.Name}    MRN: {patient.Mrn}    Page 2 of 2").Gap();
        page2.Label("Current medications:");
        foreach (var (medication, dose) in medications)
        {
            page2.Add($"{medication} {dose}");
        }

        page2.Gap().Label("Allergies:").Add(allergy).Gap();
        page2.Label("Family history:");
        foreach (var item in family)
        {
            page2.Add(item);
        }

        page2.Gap().Add("Signature: ____________________    Date: 2026-09-01");

        return Form(
            "intake-form-multipage.pdf", DocumentLayout.MultiPage, patient, [page1.Runs, page2.Runs], hasTextLayer: true,
            "A two-page intake form: demographics and chief concern on page 1; medications, allergies and "
                + "family history on page 2.",
            [
                Demographics(patient, name),
                Fact(ExpectedFact.IntakeChiefConcern, "chief_concern", concern),
                .. medications.Select(m => Medication(m.Name, m.Dose, page: 2)),
                Fact(ExpectedFact.IntakeAllergy, "allergy", allergy, page: 2),
                .. family.Select(f => Fact(ExpectedFact.IntakeFamilyHistory, "family_history", f, page: 2)),
            ]);
    }

    private static FixtureDocument Scanned()
    {
        var patient = SyntheticCohort.Rosalind;
        var name = $"Name: {patient.Name}";
        const string concern = "Exertional chest pain, eases with rest";
        (string Name, string Dose)[] medications =
        [
            ("Aspirin", "81 mg daily"),
            ("Bisoprolol", "5 mg daily"),
            ("Ramipril", "10 mg daily"),
            ("Atorvastatin", "80 mg nightly"),
        ];
        const string allergy = "Codeine (nausea)";
        const string family = "Sister - angina at 50";

        List<string> lines =
        [
            "AgentForge Synthetic Cardiology Intake Form",
            "TEST FIXTURE - not a real patient",
            string.Empty,
            name,
            $"Date of birth: {patient.BirthDate}   Sex: Female",
            $"MRN: {patient.Mrn}",
            string.Empty,
            "Chief concern:",
            concern,
            string.Empty,
            "Current medications:",
            .. medications.Select(m => $"[x] {m.Name} {m.Dose}"),
            string.Empty,
            $"Allergies: {allergy}",
            "Family history:",
            family,
            string.Empty,
            "Signature: ______________  Date: 2026-09-02",
        ];

        // About -1.5 degrees of skew, the other way from the scanned lab.
        var page = new ScannedPage(lines, sin: -0.0262, cos: 0.99966, seed: 0x0692_0002);
        return new(
            new DocumentManifest
            {
                FileName = "intake-form-scanned.pdf",
                MediaType = Pdf,
                DocumentType = DocumentManifest.IntakeForm,
                Layout = DocumentLayout.Scanned,
                Description = "A printed intake form scanned at 75 dpi, skewed about 1.5 degrees and "
                    + "speckled - an image-only PDF with no text layer.",
                Patient = patient,
                PageCount = 1,
                HasTextLayer = false,
                Facts =
                [
                    Demographics(patient, name) with { Region = page.RegionOf(name) },
                    Fact(ExpectedFact.IntakeChiefConcern, "chief_concern", concern) with { Region = page.RegionOf(concern) },
                    .. medications.Select(m => Medication(m.Name, m.Dose) with { Region = page.RegionOf($"{m.Name} {m.Dose}") }),
                    Fact(ExpectedFact.IntakeAllergy, "allergy", allergy) with { Region = page.RegionOf(allergy) },
                    Fact(ExpectedFact.IntakeFamilyHistory, "family_history", family) with { Region = page.RegionOf(family) },
                ],
            },
            page.RenderPdf);
    }

    private static ExpectedFact Demographics(SyntheticPatient patient, string nameLine) => new()
    {
        FactType = ExpectedFact.IntakeDemographics,
        Label = "demographics",
        Value = patient.Name,
        Page = 1,
        Quote = nameLine,
    };

    private static ExpectedFact Medication(string name, string dose, int page = 1) => new()
    {
        FactType = ExpectedFact.IntakeMedication,
        Label = name,
        Value = dose,
        Page = page,
        Quote = $"{name} {dose}",
    };

    private static ExpectedFact Fact(string factType, string field, string text, int page = 1) => new()
    {
        FactType = factType,
        Label = field,
        Value = text,
        Page = page,
        Quote = text,
    };

    private static FixtureDocument Form(
        string fileName, string layout, SyntheticPatient patient, IReadOnlyList<IReadOnlyList<PdfTextRun>> pages,
        bool hasTextLayer, string description, IReadOnlyList<ExpectedFact> facts) =>
        new(
            new DocumentManifest
            {
                FileName = fileName,
                MediaType = Pdf,
                DocumentType = DocumentManifest.IntakeForm,
                Layout = layout,
                Description = description,
                Patient = patient,
                PageCount = pages.Count,
                HasTextLayer = hasTextLayer,
                Facts = facts,
            },
            () => SyntheticPdf.RenderPages(pages));

    // A top-down cursor over a page: one run per line.
    private sealed class Lines(double top)
    {
        private double _y = top;

        public List<PdfTextRun> Runs { get; } = [];

        public void Title(string text)
        {
            Runs.Add(new(Left, _y, 14, true, text));
            Runs.Add(new(Left, _y - 18, 9, false, "TEST FIXTURE - synthetic data, not a real patient"));
            _y -= 44;
        }

        public Lines Add(string text) => Line(text, bold: false);

        public Lines Label(string text) => Line(text, bold: true);

        public Lines Gap()
        {
            _y -= 10;
            return this;
        }

        private Lines Line(string text, bool bold)
        {
            Runs.Add(new(Left, _y, 11, bold, text));
            _y -= 16;
            return this;
        }
    }
}
