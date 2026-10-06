using System.Globalization;
using System.Text;

namespace AgentForge.GenerateFixtureDocuments;

/// <summary>One run of text on a PDF page, positioned in points from the bottom-left corner.</summary>
public sealed record PdfTextRun(double X, double Y, double Size, bool Bold, string Text);

/// <summary>
/// Writes US-Letter PDFs with nothing in them the set does not need: either a real text layer in the two
/// standard Helvetica faces (no embedded font, so PdfPig uses its built-in metrics) with an <b>uncompressed</b>
/// content stream, so <c>grep</c> on the file shows its text; or a single full-page 1-bit image and no text
/// at all, which is what a scanner produces. No <c>/Info</c> dictionary or timestamp, so the same input always
/// produces the same bytes.
/// </summary>
public static class SyntheticPdf
{
    /// <summary>Page width in points.</summary>
    public const double PageWidth = 612;

    /// <summary>Page height in points.</summary>
    public const double PageHeight = 792;

    /// <summary>Renders the runs onto a single page.</summary>
    public static byte[] Render(IReadOnlyList<PdfTextRun> runs) => RenderPages([runs]);

    /// <summary>Renders one page per element of <paramref name="pages"/>. A single page is byte-identical to <see cref="Render"/>.</summary>
    public static byte[] RenderPages(IReadOnlyList<IReadOnlyList<PdfTextRun>> pages)
    {
        // Object numbers: 1 catalog, 2 page tree, then the pages, the two fonts, and one content stream per page.
        var n = pages.Count;
        var fontRegular = 3 + n;
        var fontBold = 4 + n;
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii($"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, n).Select(k => $"{k} 0 R"))}] /Count {n} >>"),
        };
        for (var p = 0; p < n; p++)
        {
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] "
                + $"/Resources << /Font << /F1 {fontRegular} 0 R /F2 {fontBold} 0 R >> >> /Contents {5 + n + p} 0 R >>"));
        }

        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        foreach (var runs in pages)
        {
            var content = new StringBuilder();
            foreach (var run in runs)
            {
                content.Append(CultureInfo.InvariantCulture,
                    $"BT /{(run.Bold ? "F2" : "F1")} {Num(run.Size)} Tf 1 0 0 1 {Num(run.X)} {Num(run.Y)} Tm ({Escape(run.Text)}) Tj ET\n");
            }

            objects.Add(Stream(string.Empty, Ascii(content.ToString())));
        }

        return Assemble(objects);
    }

    /// <summary>
    /// Renders <paramref name="ink"/> (true = black) as the only content of a single page, stretched to fill it:
    /// an image-only PDF with a page count and no glyphs, the shape of a scan.
    /// </summary>
    public static byte[] RenderImage(bool[,] ink)
    {
        var width = ink.GetLength(0);
        var height = ink.GetLength(1);
        var rowBytes = (width + 7) / 8;
        var data = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < rowBytes * 8; x++)
            {
                // DeviceGray at 1 bit: 1 is white, and padding past the width stays white.
                if (x >= width || !ink[x, y])
                {
                    data[(y * rowBytes) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        var draw = Ascii($"q {Num(PageWidth)} 0 0 {Num(PageHeight)} 0 0 cm /Im1 Do Q\n");
        return Assemble(
        [
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] "
                + "/Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>"),
            Stream(
                $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 1 ",
                data),
            Stream(string.Empty, draw),
        ]);
    }

    private static byte[] Stream(string dictionary, byte[] data) =>
        [.. Ascii($"<< {dictionary}/Length {data.Length} >>\nstream\n"), .. data, .. Ascii("endstream")];

    private static byte[] Assemble(List<byte[]> objects)
    {
        using var output = new MemoryStream();
        Write(output, "%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Length);
            Write(output, $"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            Write(output, "\nendobj\n");
        }

        var xref = output.Length;
        Write(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(output, $"{offset:D10} 00000 n \n");
        }

        Write(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static void Write(Stream output, string text) => output.Write(Ascii(text));

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // Printable ASCII only: anything else would need an encoding decision this writer does not make.
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException($"Only printable ASCII is supported; got U+{(int)c:X4}.", nameof(text));
            }

            if (c is '(' or ')' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
