namespace AgentForge.GenerateFixtureDocuments;

/// <summary>One fictitious patient as the documents print them.</summary>
public sealed record SyntheticPatient(string Name, string BirthDate, string Sex, string Mrn);

/// <summary>
/// The fixed cohort the fixture documents are written for. Every name is a seeded demo patient from
/// <c>tools/SeedDemoPatients</c> (same name, same date of birth), so a document here and a chart in QA
/// OpenEMR describe the same fictitious person; every MRN is in a <c>SYN-</c> namespace no real system
/// issues. Nothing here is, or is derived from, a real record. Separate changes
/// </summary>
public static class SyntheticCohort
{
    /// <summary>The patient's full name as printed on every document.</summary>
    public const string PatientName = "Demo Harold Whitfield";

    /// <summary>Date of birth, matching <c>DemoPatientCatalog</c>.</summary>
    public const string BirthDate = "1948-07-22";

    /// <summary>A medical record number in a namespace no real system issues.</summary>
    public const string Mrn = "SYN-0701";

    /// <summary>The hermetic tier's original patient: heart failure on spironolactone and lisinopril.</summary>
    public static SyntheticPatient Harold { get; } = new(PatientName, BirthDate, "male", Mrn);

    /// <summary>Palpitations and hyperlipidaemia; on a beta-blocker, apixaban and a statin.</summary>
    public static SyntheticPatient Eleanor { get; } = new("Demo Eleanor Castellano", "1952-03-14", "female", "SYN-0692-01");

    /// <summary>Atrial fibrillation on warfarin, with CKD; exertional dyspnoea.</summary>
    public static SyntheticPatient Walter { get; } = new("Demo Walter Ibekwe", "1960-01-09", "male", "SYN-0692-02");

    /// <summary>Exertional chest pain under work-up: serial troponin and natriuretic peptides.</summary>
    public static SyntheticPatient Rosalind { get; } = new("Demo Rosalind Ng", "1955-11-02", "female", "SYN-0692-03");

    /// <summary>Every patient any document is written for.</summary>
    public static IReadOnlyList<SyntheticPatient> Patients { get; } = [Harold, Eleanor, Walter, Rosalind];
}
