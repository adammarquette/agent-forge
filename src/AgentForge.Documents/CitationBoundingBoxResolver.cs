using System.Text;
using AgentForge.Documents.Extraction;

namespace AgentForge.Documents;

/// <summary>
/// Decides whether a citation's quote is actually in the source document, and where. Two jobs in one pass,
/// and they are not the same job: it resolves the pixel geometry the click-to-source overlay draws
/// (FR-CITE-2) <b>and</b> it is the deterministic backstop behind the extraction prompt's
/// VERBATIM rule — the only thing that distinguishes a quote the model copied from one
/// it composed. Pure and whitespace-insensitive: it finds the contiguous run of words whose letters spell
/// the quote (spaces dropped, case-folded), then unions their boxes.
/// </summary>
/// <remarks>
/// <b>The match policy, and its threshold, are stated on <see cref="CitationQuoteMatch"/>.</b> Read it before
/// making this more forgiving — that is the subject, and the policy says what a forgiving match may
/// and may not do.
/// </remarks>
public static class CitationBoundingBoxResolver
{
    /// <summary>Looks for <paramref name="quote"/> on <paramref name="page"/> and reports what it found.</summary>
    public static CitationQuoteLocation Resolve(PdfTextLayer text, int page, string quote)
    {
        // Checked before the page is looked at: an empty or whitespace-only quote grounds nothing whatever
        // the page contains, so a missing text layer must not excuse it. A separate change review
        var target = Normalize(quote);
        if (target.Length == 0)
        {
            return new(CitationQuoteMatch.Unlocatable, null);
        }

        // No page count means no document to range-check against - a non-PDF, or one that would not open.
        // Everything on it is unreadable rather than absent.
        if (text.PageCount <= 0)
        {
            return new(CitationQuoteMatch.Unchecked, null);
        }

        // The cited page is not in the document. That is not "we could not check": the quote cannot be on a
        // page that does not exist, so it is as absent as a quote searched for and missed. Kept distinct from
        // the case below because a model that fabricates the page number alongside the quote would otherwise
        // score in the same bucket as every scan - the one bucket the unlocatable/exact ratio cannot see
        // into. A separate change review
        if (page < 1 || page > text.PageCount)
        {
            return new(CitationQuoteMatch.Unlocatable, null);
        }

        var pageWords = new List<PdfWord>();
        foreach (var word in text.Words)
        {
            if (word.PageNumber == page)
            {
                pageWords.Add(word);
            }
        }

        // The page is in the document but carries no glyphs - a scanned insert in an otherwise digital PDF,
        // or an image page. Nothing was searched, so nothing may be concluded: calling this unlocatable would
        // impeach an honest citation on the strength of a missing text layer.
        if (pageWords.Count == 0)
        {
            return new(CitationQuoteMatch.Unchecked, null);
        }

        // Grow a contiguous run from each start until its folded text reaches the quote's length: an exact
        // match unions that run's boxes; an overshoot without a match abandons this start for the next.
        for (var start = 0; start < pageWords.Count; start++)
        {
            var run = new StringBuilder();
            for (var end = start; end < pageWords.Count; end++)
            {
                run.Append(Normalize(pageWords[end].Text));
                if (run.Length < target.Length)
                {
                    continue;
                }

                if (run.Length == target.Length && run.ToString() == target)
                {
                    return new(CitationQuoteMatch.Exact, Union(pageWords, start, end));
                }

                break;
            }
        }

        // The page's own text was available and the quote is not in it.
        return new(CitationQuoteMatch.Unlocatable, null);
    }

    private static double[] Union(List<PdfWord> words, int from, int to)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var k = from; k <= to; k++)
        {
            var word = words[k];
            minX = Math.Min(minX, word.X);
            minY = Math.Min(minY, word.Y);
            maxX = Math.Max(maxX, word.X + word.Width);
            maxY = Math.Max(maxY, word.Y + word.Height);
        }

        return [minX, minY, maxX - minX, maxY - minY];
    }

    // Fold to a comparison key: drop whitespace, lower-case. Punctuation/digits are kept so "3.5-5.1" and
    // "mmol/L" still align with how the model quotes them.
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
