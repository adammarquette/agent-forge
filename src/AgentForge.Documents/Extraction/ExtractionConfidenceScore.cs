namespace AgentForge.Documents.Extraction;

/// <summary>
/// The one definition of what a stored <c>DerivedFact.ExtractionConfidence</c> number means, and the only
/// place the mapping between a <see cref="CitationQuoteMatch"/> and that number lives.
/// </summary>
/// <remarks>
/// <b>Read the name carefully.</b> This is a <i>grounding/locatability</i> confidence — how well the citation
/// could be corroborated against the document's own text — and <b>not</b> a model-reported confidence in the
/// extracted value. A fact scoring <see cref="Located"/> is one whose quote was found verbatim in the source;
/// it says nothing about whether the model read the number off it correctly. A reader who assumes otherwise
/// inverts the meaning of the most important value the column carries.
/// <para>
/// <b>The round trip is the contract.</b> <see cref="For"/> writes the score at ingestion and
/// <see cref="MatchFor"/> reads the outcome back off it for the field-level metric, so the two must stay
/// inverses; <c>ExtractionConfidenceScoreTests</c> pins that. Equality on <see cref="double"/> is safe here
/// precisely because every live score is one of these three constants rather than a computed value.
/// </para>
/// <para>
/// <b>Forward-only.</b> The rule changed in <c>a separate change</c> (it used to be two values, read off whether a
/// bounding box was present) and rows written before it were not backfilled (<c>a separate change</c>), so the column
/// can hold a score this type would not produce. <see cref="MatchFor"/> answers <c>null</c> for those rather
/// than rounding one into a bucket it does not belong in.
/// </para>
/// </remarks>
public static class ExtractionConfidenceScore
{
    /// <summary>The quote was found verbatim in the cited page's own text, and carries the fact's own text where that is checked — the only value evidencing grounding.</summary>
    public const double Located = 1.0;

    /// <summary>There was no text to check against (a scan, an image page, a file that would not open). Neither corroborated nor impeached.</summary>
    public const double NothingToCheck = 0.5;

    /// <summary>The page's text was searched and the quote is not in it, or the quote does not carry the fact it is cited for — the fabricated-fact signal, and the floor rather than the ceiling.</summary>
    public const double QuoteAbsent = 0.0;

    /// <summary>The score a citation with this locatability outcome is persisted with.</summary>
    public static double For(CitationQuoteMatch match) => match switch
    {
        CitationQuoteMatch.Exact => Located,
        CitationQuoteMatch.Unchecked => NothingToCheck,
        CitationQuoteMatch.Unlocatable => QuoteAbsent,
        _ => throw new ArgumentOutOfRangeException(nameof(match), match, "Unknown citation quote match."),
    };

    /// <summary>
    /// The locatability outcome a persisted score encodes, or <c>null</c> when the score is absent or is one
    /// the current rule cannot produce — a pre-<c>a separate change</c> row. A <c>null</c> means "no outcome to report",
    /// and a caller drops it rather than charging it to a bucket.
    /// </summary>
    public static CitationQuoteMatch? MatchFor(double? score) => score switch
    {
        Located => CitationQuoteMatch.Exact,
        NothingToCheck => CitationQuoteMatch.Unchecked,
        QuoteAbsent => CitationQuoteMatch.Unlocatable,
        _ => null,
    };
}
