using AgentForge.Data.Entities;

namespace AgentForge.Documents;

/// <summary>Helpers for reporting a <see cref="ClinicalDocumentType"/> outside the domain model.</summary>
public static class ClinicalDocumentTypeExtensions
{
    /// <summary>
    /// The bounded, PHI-free token this document type is reported under. The set is the enum's members and
    /// cannot grow at run time, which is what makes it admissible as an exported metric label.
    /// </summary>
    public static string ToWireName(this ClinicalDocumentType documentType) => documentType switch
    {
        ClinicalDocumentType.LabPdf => "lab_pdf",
        ClinicalDocumentType.IntakeForm => "intake_form",
        _ => throw new ArgumentOutOfRangeException(
            nameof(documentType), documentType, "Unknown clinical document type."),
    };
}
