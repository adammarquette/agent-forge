namespace AgentForge.Documents;

/// <summary>
/// Reads a digital PDF's text layer - the words with their normalized rectangles, for pixel-accurate citation
/// boxes (FR-CITE-2), plus the page count the extractor range-checks citations against.
/// Implementations return <see cref="PdfTextLayer.None"/> for a scanned/image-only, encrypted or unreadable
/// PDF, so the extractor degrades to the model's estimate rather than failing - and records every citation on
/// such a document as `unchecked` rather than as located or as fabricated.
/// </summary>
public interface IPdfWordReader
{
    /// <summary>Reads the PDF's text layer; <see cref="PdfTextLayer.None"/> when nothing can be read.</summary>
    PdfTextLayer ReadTextLayer(ReadOnlyMemory<byte> pdf);
}
