namespace AgentForge.Documents;

/// <summary>
/// What a document's text layer yields: every word's normalized rectangle, and how many pages the document
/// has. The page count travels with the words because only the reader knows it, and without it a citation
/// naming a page the document does not have is indistinguishable from one naming a page that simply carries
/// no glyphs - one is a fabricated citation and the other is a scanned insert.
/// </summary>
/// <param name="Words">Every word read, across all pages; empty when none could be read.</param>
/// <param name="PageCount">Pages in the document, or <c>0</c> when that is unknown (a non-PDF, or a file
/// that could not be opened) - in which case no citation on it can be range-checked.</param>
public sealed record PdfTextLayer(IReadOnlyList<PdfWord> Words, int PageCount)
{
    /// <summary>No text layer at all: a non-PDF, a scan, or a document that could not be opened.</summary>
    public static PdfTextLayer None { get; } = new([], 0);
}
