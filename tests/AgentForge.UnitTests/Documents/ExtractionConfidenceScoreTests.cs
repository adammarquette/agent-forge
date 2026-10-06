using FluentAssertions;
using AgentForge.Documents.Extraction;

namespace AgentForge.UnitTests.Documents;

/// <summary>
/// Drives <see cref="ExtractionConfidenceScore"/> — the single definition of what a stored
/// <c>DerivedFact.ExtractionConfidence</c> means. Two consumers read it in opposite directions
/// (<c>DerivedFactMapper</c> writes the score, <c>DocumentIngestionService</c> reads the outcome back off
/// it for the field-level metric), so the round trip is the contract: if the two ever disagree, a document
/// whose quote was absent would be metered as one that was never checked. A separate change
/// </summary>
public sealed class ExtractionConfidenceScoreTests
{
    [Theory]
    [InlineData(CitationQuoteMatch.Exact, 1.0)]
    [InlineData(CitationQuoteMatch.Unchecked, 0.5)]
    [InlineData(CitationQuoteMatch.Unlocatable, 0.0)]
    public void For_EveryMatch_ScoresTheDocumentedValue(CitationQuoteMatch match, double expected) =>
        ExtractionConfidenceScore.For(match).Should().Be(expected);

    [Theory]
    [InlineData(CitationQuoteMatch.Exact)]
    [InlineData(CitationQuoteMatch.Unchecked)]
    [InlineData(CitationQuoteMatch.Unlocatable)]
    public void MatchFor_AScoreThisTypeProduced_RoundTripsToTheSameMatch(CitationQuoteMatch match) =>
        ExtractionConfidenceScore.MatchFor(ExtractionConfidenceScore.For(match)).Should().Be(match);

    [Fact]
    public void For_EveryMatch_ScoresADistinctValue()
    {
        // Three meanings, three numbers. If two collapsed, the histogram's whole point - showing that a
        // located quote and an unlocatable one are different populations - would be lost silently.
        var scores = Enum.GetValues<CitationQuoteMatch>().Select(ExtractionConfidenceScore.For).ToList();

        scores.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.75)]
    [InlineData(-1.0)]
    public void MatchFor_AScoreTheCurrentRuleCannotProduce_ReturnsNull(double? score)
    {
        // The forward-only correction: rows written before the scoring rule changed keep the old
        // value and are not backfilled, so a score outside the current three is possible in the table. It
        // must not be guessed into one of them - null is "no outcome to report", and the caller drops it
        // rather than inventing a bucket.
        ExtractionConfidenceScore.MatchFor(score).Should().BeNull();
    }
}
