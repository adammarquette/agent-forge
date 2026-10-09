namespace AgentForge.GenerateFixtureDocuments;

/// <summary>
/// A page of monospaced <see cref="BitmapFont"/> text run through what a cheap office scanner does to it:
/// low resolution, a small skew, speckle and dropped-out ink. All of it is integer arithmetic or plain
/// IEEE multiply-and-add on constants - no <c>Math.Sin</c>, no <c>Random</c> - so the pixels are the same on
/// every machine and runtime. <see cref="RegionOf"/> reports where a printed span lands <i>after</i> the
/// skew, as the manifest's truth.
/// </summary>
public sealed class ScannedPage
{
    /// <summary>Page width in pixels: 8.5 in at 75 dpi.</summary>
    public const int Width = 638;

    /// <summary>Page height in pixels: 11 in at 75 dpi.</summary>
    public const int Height = 825;

    private const int Scale = 2;
    private const int Advance = (BitmapFont.GlyphWidth + 1) * Scale;
    private const int LineHeight = 22;
    private const int Left = 48;
    private const int Top = 56;

    private readonly IReadOnlyList<string> _lines;
    private readonly double _sin;
    private readonly double _cos;
    private readonly uint _seed;

    /// <summary>Lays out <paramref name="lines"/> and fixes the skew and the noise.</summary>
    /// <param name="lines">Printed lines, top to bottom; an empty string is a blank line.</param>
    /// <param name="sin">Sine of the skew angle (a constant, so no trigonometry runs).</param>
    /// <param name="cos">Cosine of the same angle.</param>
    /// <param name="seed">Seed for the speckle; any fixed value.</param>
    public ScannedPage(IReadOnlyList<string> lines, double sin, double cos, uint seed)
    {
        var widest = Left + (lines.Max(l => l.Length) * Advance);
        if (widest > Width - Left || Top + (lines.Count * LineHeight) > Height - Top)
        {
            throw new ArgumentException("The lines do not fit on the page.", nameof(lines));
        }

        _lines = lines;
        _sin = sin;
        _cos = cos;
        _seed = seed;
    }

    /// <summary>
    /// The normalized top-left <c>[x, y, w, h]</c> of <paramref name="text"/>'s first printed occurrence
    /// after the skew - the axis-aligned box around the rotated span - rounded to 4 places.
    /// </summary>
    public IReadOnlyList<double> RegionOf(string text)
    {
        for (var line = 0; line < _lines.Count; line++)
        {
            var column = _lines[line].IndexOf(text, StringComparison.Ordinal);
            if (column < 0)
            {
                continue;
            }

            double x0 = Left + (column * Advance);
            double y0 = Top + (line * LineHeight);
            var x1 = x0 + (text.Length * Advance) - Scale;
            var y1 = y0 + (BitmapFont.GlyphHeight * Scale);
            (double X, double Y)[] corners = [Rotate(x0, y0), Rotate(x1, y0), Rotate(x0, y1), Rotate(x1, y1)];
            var minX = corners.Min(c => c.X);
            var minY = corners.Min(c => c.Y);
            var maxX = corners.Max(c => c.X);
            var maxY = corners.Max(c => c.Y);
            return [Round(minX / Width), Round(minY / Height), Round((maxX - minX) / Width), Round((maxY - minY) / Height)];
        }

        throw new ArgumentException($"'{text}' is not printed on the page.", nameof(text));
    }

    /// <summary>The scanned page as an image-only PDF. Same bytes on every call.</summary>
    public byte[] RenderPdf() => SyntheticPdf.RenderImage(Scan());

    private bool[,] Scan()
    {
        var clean = new bool[Width, Height];
        for (var line = 0; line < _lines.Count; line++)
        {
            var text = _lines[line];
            for (var column = 0; column < text.Length; column++)
            {
                DrawGlyph(clean, text[column], Left + (column * Advance), Top + (line * LineHeight));
            }
        }

        var scanned = new bool[Width, Height];
        var state = _seed;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Inverse-map each output pixel into the unskewed page, nearest neighbour.
                var (sx, sy) = Unrotate(x, y);
                var ix = (int)Math.Floor(sx + 0.5);
                var iy = (int)Math.Floor(sy + 0.5);
                var ink = ix >= 0 && iy >= 0 && ix < Width && iy < Height && clean[ix, iy];

                state = Next(state);
                var roll = state % 1000;
                scanned[x, y] = ink ? roll >= 30 : roll < 4; // 3% of ink drops out; 0.4% of paper speckles
            }
        }

        return scanned;
    }

    private (double X, double Y) Rotate(double x, double y)
    {
        double cx = Width / 2.0, cy = Height / 2.0;
        var dx = x - cx;
        var dy = y - cy;
        return (cx + (dx * _cos) - (dy * _sin), cy + (dx * _sin) + (dy * _cos));
    }

    private (double X, double Y) Unrotate(double x, double y)
    {
        double cx = Width / 2.0, cy = Height / 2.0;
        var dx = x - cx;
        var dy = y - cy;
        return (cx + (dx * _cos) + (dy * _sin), cy - (dx * _sin) + (dy * _cos));
    }

    // xorshift32: a fixed sequence for a fixed seed, on every runtime.
    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.ToEven);

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
}
