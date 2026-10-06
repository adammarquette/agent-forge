namespace AgentForge.GenerateFixtureDocuments;

/// <summary>
/// A synthetic cardiology intake form (<c>intake_form</c>) as a <b>raster image</b> - what the front desk
/// scans, and the case with no text layer: the extractor has nothing to check a quote against, so every
/// citation on it is <c>unchecked</c> and keeps the model's estimated box. <see cref="BoxOf"/> is where
/// each printed string really sits, so an estimate can be compared with the truth.
/// </summary>
public static class SyntheticIntakeForm
{
    /// <summary>File name under <c>tests/fixtures/documents/</c>.</summary>
    public const string FileName = "synthetic-intake-form.png";

    /// <summary>IANA media type.</summary>
    public const string MediaType = "image/png";

    /// <summary>Printed name line.</summary>
    public const string NameLine = "NAME: DEMO HAROLD WHITFIELD";

    /// <summary>Printed chief concern.</summary>
    public const string ChiefConcern = "CHEST PRESSURE WHEN CLIMBING STAIRS";

    /// <summary>First listed medication.</summary>
    public const string Spironolactone = "SPIRONOLACTONE 25 MG DAILY";

    /// <summary>Second listed medication.</summary>
    public const string Lisinopril = "LISINOPRIL 20 MG DAILY";

    /// <summary>Printed allergy.</summary>
    public const string Allergy = "PENICILLIN (RASH)";

    /// <summary>Printed family history.</summary>
    public const string FamilyHistory = "FATHER - MYOCARDIAL INFARCTION AT 52";

    /// <summary>Image width in pixels (8.5 in at 96 dpi).</summary>
    public const int Width = 816;

    /// <summary>Image height in pixels (11 in at 96 dpi).</summary>
    public const int Height = 1056;

    private const int Scale = 2;
    private const int Advance = (BitmapFont.GlyphWidth + 1) * Scale;
    private const int LineHeight = 22;
    private const int Left = 64;
    private const int Top = 72;

    /// <summary>Every printed line, top to bottom; an empty string is a blank line.</summary>
    public static IReadOnlyList<string> Lines { get; } =
    [
        "AGENTFORGE SYNTHETIC CARDIOLOGY INTAKE FORM",
        "TEST FIXTURE - NOT A REAL PATIENT",
        "",
        NameLine,
        $"DATE OF BIRTH: {SyntheticCohort.BirthDate}    SEX: MALE",
        $"MRN: {SyntheticCohort.Mrn}",
        "",
        $"CHIEF CONCERN: {ChiefConcern}",
        "",
        "CURRENT MEDICATIONS:",
        $"[X] {Spironolactone}",
        $"[X] {Lisinopril}",
        "",
        $"ALLERGIES: {Allergy}",
        $"FAMILY HISTORY: {FamilyHistory}",
        "",
        "SIGNATURE: ____________________    DATE: 2026-09-01",
    ];

    /// <summary>
    /// What the form is and what a correct extraction yields. Its quotes are its printed strings, and each
    /// carries its true region, because an image has no text layer to read a box back from.
    /// </summary>
    public static DocumentManifest Manifest { get; } = new()
    {
        FileName = FileName,
        MediaType = MediaType,
        DocumentType = DocumentManifest.IntakeForm,
        Layout = DocumentLayout.Image,
        Description = "The hermetic tier's original intake form: a clean 96 dpi PNG with no text layer, "
            + "upper case as a front-desk form prints.",
        Patient = SyntheticCohort.Harold,
        PageCount = 1,
        HasTextLayer = false,
        Facts =
        [
            Printed(ExpectedFact.IntakeDemographics, "demographics", NameLine, "DEMO HAROLD WHITFIELD"),
            Printed(ExpectedFact.IntakeChiefConcern, "chief_concern", ChiefConcern, ChiefConcern),
            Printed(ExpectedFact.IntakeMedication, "SPIRONOLACTONE", Spironolactone, "25 MG DAILY"),
            Printed(ExpectedFact.IntakeMedication, "LISINOPRIL", Lisinopril, "20 MG DAILY"),
            Printed(ExpectedFact.IntakeAllergy, "allergy", Allergy, Allergy),
            Printed(ExpectedFact.IntakeFamilyHistory, "family_history", FamilyHistory, FamilyHistory),
        ],
    };

    /// <summary>Renders the form. Same bytes on every call.</summary>
    public static byte[] Render()
    {
        var ink = new bool[Width, Height];
        DrawBorder(ink, 40, 40, Width - 40, Height - 40);
        for (var line = 0; line < Lines.Count; line++)
        {
            var text = Lines[line];
            for (var column = 0; column < text.Length; column++)
            {
                DrawGlyph(ink, text[column], Left + (column * Advance), Top + (line * LineHeight));
            }
        }

        return SyntheticPng.Encode(ink);
    }

    /// <summary>
    /// The normalized <c>[x, y, w, h]</c> box (top-left origin, the citation contract's convention) of the
    /// first printed occurrence of <paramref name="text"/>.
    /// </summary>
    public static double[] BoxOf(string text)
    {
        for (var line = 0; line < Lines.Count; line++)
        {
            var column = Lines[line].IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
            {
                double x = Left + (column * Advance);
                double y = Top + (line * LineHeight);
                double w = (text.Length * Advance) - Scale;
                double h = BitmapFont.GlyphHeight * Scale;
                return [x / Width, y / Height, w / Width, h / Height];
            }
        }

        throw new ArgumentException($"'{text}' is not printed on the form.", nameof(text));
    }

    private static ExpectedFact Printed(string factType, string label, string quote, string value) => new()
    {
        FactType = factType,
        Label = label,
        Value = value,
        Page = 1,
        Quote = quote,
        Region = BoxOf(quote),
    };

    private static void DrawGlyph(bool[,] ink, char c, int left, int top)
    {
        for (var gy = 0; gy < BitmapFont.GlyphHeight; gy++)
        {
            for (var gx = 0; gx < BitmapFont.GlyphWidth; gx++)
            {
                if (!BitmapFont.IsInk(c, gx, gy))
                {
                    continue;
                }

                for (var sy = 0; sy < Scale; sy++)
                {
                    for (var sx = 0; sx < Scale; sx++)
                    {
                        ink[left + (gx * Scale) + sx, top + (gy * Scale) + sy] = true;
                    }
                }
            }
        }
    }

    private static void DrawBorder(bool[,] ink, int x0, int y0, int x1, int y1)
    {
        for (var x = x0; x <= x1; x++)
        {
            ink[x, y0] = ink[x, y1] = true;
        }

        for (var y = y0; y <= y1; y++)
        {
            ink[x0, y] = ink[x1, y] = true;
        }
    }
}
