using AgentForge.Documents.Extraction;

namespace AgentForge.Documents;

/// <summary>
/// What <see cref="CitationBoundingBoxResolver"/> concluded about one citation's quote: the outcome, and the
/// exact geometry when — and only when — the quote was located. A negative outcome carries no box, so the
/// caller cannot accidentally treat "I could not find this" as "here is where it is".
/// </summary>
/// <param name="Match">Whether the quote was located, searched for and missing, or not checkable at all.</param>
/// <param name="BoundingBox">
/// Normalized top-left <c>[x, y, w, h]</c> of the located words; null unless <paramref name="Match"/> is
/// <see cref="CitationQuoteMatch.Exact"/>.
/// </param>
public readonly record struct CitationQuoteLocation(CitationQuoteMatch Match, double[]? BoundingBox);
