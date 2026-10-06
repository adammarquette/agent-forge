using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentForge.GenerateFixtureDocuments;

/// <summary>The layout a document in the set exercises - which condition the citation path is tested against.</summary>
public static class DocumentLayout
{
    /// <summary>One table, one line per row, drawn in reading order.</summary>
    public const string Clean = "clean";

    /// <summary>Two tables side by side, so each printed line carries two rows.</summary>
    public const string TwoColumn = "two-column";

    /// <summary>A table whose long test names wrap onto a second line inside their cell.</summary>
    public const string WrappedTable = "wrapped-table";

    /// <summary>Facts on more than one page.</summary>
    public const string MultiPage = "multi-page";

    /// <summary>Values and units split across tokens: a glued unit, a superscript, a unit drawn out of order.</summary>
    public const string SplitUnits = "split-units";

    /// <summary>A raster copy - rotated, speckled, low resolution - with no text layer at all.</summary>
    public const string Scanned = "scanned";

    /// <summary>A clean raster image upload (a PNG): no skew or noise, but no text layer either.</summary>
    public const string Image = "image";
}

/// <summary>
/// One fact a correct extraction of the document yields, and the exact span of printed text it cites. The
/// span is what a reader sees, in reading order - which is not always how the text layer sequences it, and
/// that gap is what the set exists to measure. A separate change
/// </summary>
public sealed record ExpectedFact
{
    /// <summary>A lab result row.</summary>
    public const string LabResult = "lab.result";

    /// <summary>The intake form's demographics block (its citation is the form-level one).</summary>
    public const string IntakeDemographics = "intake.demographics";

    /// <summary>The intake form's chief concern.</summary>
    public const string IntakeChiefConcern = "intake.chief_concern";

    /// <summary>One listed medication.</summary>
    public const string IntakeMedication = "intake.medication";

    /// <summary>One listed allergy.</summary>
    public const string IntakeAllergy = "intake.allergy";

    /// <summary>One family-history item.</summary>
    public const string IntakeFamilyHistory = "intake.family_history";

    /// <summary>The derived-fact type this becomes (the values of <c>DerivedFactType</c>).</summary>
    public required string FactType { get; init; }

    /// <summary>Test name, medication name, or the field name for a free-text item.</summary>
    public required string Label { get; init; }

    /// <summary>The value as printed: a result, a dose, or the demographics block's full name.</summary>
    public string? Value { get; init; }

    /// <summary>Unit of measure, for a lab result that prints one.</summary>
    public string? Unit { get; init; }

    /// <summary>Reference range as printed, for a lab result.</summary>
    public string? ReferenceRange { get; init; }

    /// <summary>Whether the report flags the result, for a lab result.</summary>
    public bool? AbnormalFlag { get; init; }

    /// <summary>Collection date of the result, for a lab result.</summary>
    public string? CollectionDate { get; init; }

    /// <summary>1-based page the span is printed on.</summary>
    public required int Page { get; init; }

    /// <summary>The exact printed span the fact cites, words separated by single spaces.</summary>
    public required string Quote { get; init; }

    /// <summary>
    /// Where the span really sits as a normalized top-left <c>[x, y, w, h]</c> - given for raster documents
    /// only, where nothing can be read back from the file to check a box against.
    /// </summary>
    public IReadOnlyList<double>? Region { get; init; }

    /// <summary>A lab result fact from a printed row.</summary>
    public static ExpectedFact Lab(LabRow row, string collectionDate, int page = 1, string? quote = null, IReadOnlyList<double>? region = null) =>
        new()
        {
            FactType = LabResult,
            Label = row.Test,
            Value = row.Result,
            Unit = row.Units.Length > 0 ? row.Units : null,
            ReferenceRange = row.Reference.Length > 0 ? row.Reference : null,
            AbnormalFlag = row.Flag.Length > 0,
            CollectionDate = collectionDate,
            Page = page,
            Quote = quote ?? row.Quote,
            Region = region,
        };
}

/// <summary>
/// What one document in the set is and what a correct extraction of it yields - committed beside the document
/// as <c>&lt;file&gt;.manifest.json</c> so any tier (Bruno, evals, the integration smoke) can use it without
/// this project. A separate change
/// </summary>
public sealed record DocumentManifest
{
    /// <summary>The <c>doc_type</c> wire value for a lab report.</summary>
    public const string LabPdf = "lab_pdf";

    /// <summary>The <c>doc_type</c> wire value for an intake form.</summary>
    public const string IntakeForm = "intake_form";

    /// <summary>File name under <see cref="FixtureDocuments.RelativeDirectory"/>.</summary>
    public required string FileName { get; init; }

    /// <summary>IANA media type the file is uploaded as.</summary>
    public required string MediaType { get; init; }

    /// <summary><see cref="LabPdf"/> or <see cref="IntakeForm"/>.</summary>
    public required string DocumentType { get; init; }

    /// <summary>One of <see cref="DocumentLayout"/>.</summary>
    public required string Layout { get; init; }

    /// <summary>What the document is, and what it is in the set to test.</summary>
    public required string Description { get; init; }

    /// <summary>The patient it is written for.</summary>
    public required SyntheticPatient Patient { get; init; }

    /// <summary>Number of pages.</summary>
    public required int PageCount { get; init; }

    /// <summary>Whether a PDF text layer carries the printed text (false for a scan or an image).</summary>
    public required bool HasTextLayer { get; init; }

    /// <summary>Every fact, in print order.</summary>
    public required IReadOnlyList<ExpectedFact> Facts { get; init; }

    /// <summary>The manifest as indented, LF-terminated UTF-8 JSON. Same bytes on every call.</summary>
    public byte[] ToJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteString("file_name", FileName);
            json.WriteString("media_type", MediaType);
            json.WriteString("doc_type", DocumentType);
            json.WriteString("layout", Layout);
            json.WriteString("description", Description);
            json.WriteStartObject("patient");
            json.WriteString("name", Patient.Name);
            json.WriteString("birth_date", Patient.BirthDate);
            json.WriteString("sex", Patient.Sex);
            json.WriteString("mrn", Patient.Mrn);
            json.WriteEndObject();
            json.WriteNumber("page_count", PageCount);
            json.WriteBoolean("has_text_layer", HasTextLayer);
            json.WriteStartArray("facts");
            foreach (var fact in Facts)
            {
                json.WriteStartObject();
                json.WriteString("fact_type", fact.FactType);
                json.WriteString("label", fact.Label);
                WriteOptional(json, "value", fact.Value);
                WriteOptional(json, "unit", fact.Unit);
                WriteOptional(json, "reference_range", fact.ReferenceRange);
                if (fact.AbnormalFlag is { } flag)
                {
                    json.WriteBoolean("abnormal_flag", flag);
                }

                WriteOptional(json, "collection_date", fact.CollectionDate);
                json.WriteNumber("page", fact.Page);
                json.WriteString("quote", fact.Quote);
                if (fact.Region is { } region)
                {
                    json.WriteStartArray("region");
                    foreach (var v in region)
                    {
                        json.WriteNumberValue(v);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }
}
