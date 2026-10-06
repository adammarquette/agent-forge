using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentForge.Documents.Extraction;

// This <summary> is published verbatim as the description of every inlined `citation.match` field in
// the OpenAPI document (CONVENTIONS.md §13), so a tracker citation inside it
// is external API copy pointing somewhere an external reader cannot open. It lives out here instead.

/// <summary>
/// What the extractor found when it looked for a citation's <see cref="ExtractionCitation.Quote"/> in the
/// source document's own text — the deterministic backstop behind the extraction prompt's VERBATIM rule
/// (FR-CITE-2; UC-1, UC-3). <b>Server-stamped, never model-supplied:</b> the extractor overwrites this on
/// every citation, so a model cannot assert its own grounding.
/// </summary>
/// <remarks>
/// <b>Match policy.</b> A quote counts as located only when its comparison key — whitespace removed,
/// case-folded, punctuation and digits kept — is identical to the concatenated key of a contiguous run of
/// words on the cited page. Expressed as a threshold, the admissible similarity floor for
/// <see cref="Exact"/> is <b>1.0</b> on that key; nothing below it may claim a bounding box.
/// <c>a separate change</c> may add a forgiving match, but only as a <i>distinct</i> member with its own stated floor,
/// so a fuzzy hit can never be mistaken for a verbatim one. Lowering <see cref="Exact"/>'s own floor is
/// forbidden: it is the only evidence that the model copied the quote rather than composed it.
/// </remarks>
[JsonConverter(typeof(CitationQuoteMatchJsonConverter))]
public enum CitationQuoteMatch
{
    /// <summary>
    /// Nothing looked, because there was nothing to look at. Precisely: the document reported no page count
    /// at all (a non-PDF, or a file that would not open), or the cited page <b>is</b> in the document but
    /// carries no extractable text — a scan, an image page, a scanned insert in an otherwise digital PDF. The
    /// quote is neither corroborated nor impeached, so the model's estimated box, if it gave one, is the only
    /// overlay available. The default, which is what an absent value means.
    /// <b>A page the document does not have is <see cref="Unlocatable"/>, not this</b> — the quote cannot be
    /// on a page that does not exist, and keeping the two apart is what stops a fabricated page number
    /// hiding in the same bucket as every scan. Nor is a fact whose own text its quote does not carry.
    /// </summary>
    Unchecked = 0,

    /// <summary>
    /// The quote was found verbatim in the cited page's own glyphs, and the bounding box is the union of
    /// those glyphs rather than anything the model estimated. The only value that evidences grounding.
    /// </summary>
    Exact = 1,

    /// <summary>
    /// The citation's central claim — that this fact's text appears on this page — is false, so no bounding
    /// box is carried: a highlight drawn around text that says something else is worse than no highlight.
    /// Three ways to land here: the cited page's text was searched and the quote is not in it; the cited
    /// page is not in the document at all (out of range, or below 1), in which case the quote cannot be on it
    /// either; or the quote does not carry the fact it is cited for — an intake item's or medication's own
    /// text, or a lab result's analyte, value and unit (<see cref="FactQuoteSupport"/>) — which is decided
    /// from the two strings alone, so it applies even where the page could not be read.
    /// </summary>
    Unlocatable = 2,
}

/// <summary>
/// Reads and writes <see cref="CitationQuoteMatch"/> as the token
/// <see cref="CitationQuoteMatchExtensions.ToWireName"/> produces, so one spelling serves the JSON contract
/// and the metric tag alike.
/// </summary>
/// <remarks>
/// <b>Deliberately tolerant on read, and only on read.</b> An unrecognised value yields
/// <see cref="CitationQuoteMatch.Unchecked"/> rather than throwing. The strict-schema gate is right to reject
/// a wrong <i>shape</i>, but this field is server-stamped a moment later, so rejecting a whole document over
/// a value that was about to be overwritten would lose good facts for nothing. Two ways it arises: a model
/// inventing the field — it is absent from the prompt shape, so anything there is a guess — and canonical
/// JSON written by a build that knows a member this one does not, which is exactly what adding a
/// fuzzy member will create. Unknown means "nothing looked", which is the safe reading.
/// a separate change review
/// </remarks>
public sealed class CitationQuoteMatchJsonConverter : JsonConverter<CitationQuoteMatch>
{
    /// <inheritdoc />
    public override CitationQuoteMatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() switch
            {
                "exact" => CitationQuoteMatch.Exact,
                "unlocatable" => CitationQuoteMatch.Unlocatable,
                _ => CitationQuoteMatch.Unchecked,
            };
        }

        // A non-scalar has to be consumed or the reader is left mid-value; a scalar is already whole.
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        return CitationQuoteMatch.Unchecked;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CitationQuoteMatch value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireName());
}

/// <summary>Helpers for reporting a <see cref="CitationQuoteMatch"/> outside the JSON contract.</summary>
public static class CitationQuoteMatchExtensions
{
    /// <summary>
    /// The bounded, PHI-free token this outcome is reported under — the metric tag, and the same spelling the
    /// JSON contract uses, so a dashboard and a stored fact name the outcome identically.
    /// </summary>
    public static string ToWireName(this CitationQuoteMatch match) => match switch
    {
        CitationQuoteMatch.Unchecked => "unchecked",
        CitationQuoteMatch.Exact => "exact",
        CitationQuoteMatch.Unlocatable => "unlocatable",
        _ => throw new ArgumentOutOfRangeException(nameof(match), match, "Unknown citation quote match."),
    };
}
