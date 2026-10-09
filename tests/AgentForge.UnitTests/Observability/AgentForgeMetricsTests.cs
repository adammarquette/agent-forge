using System.Diagnostics.Metrics;
using FluentAssertions;
using AgentForge.Observability;

namespace AgentForge.UnitTests.Observability;

/// <summary>
/// Verifies <see cref="AgentForgeMetrics"/> actually records through the real
/// <see cref="Meter"/>/<see cref="Instrument"/> APIs, using a real <see cref="MeterListener"/> -
/// the standard BCL way to observe metrics without a live exporter/collector - rather than trusting
/// the plumbing by inspection.
/// </summary>
public sealed class AgentForgeMetricsTests : IDisposable
{
    private readonly List<Measurement> _measurements = [];
    private readonly List<Instrument> _instruments = [];
    private readonly MeterListener _listener;
    private readonly AgentForgeMetrics _sut = new();

    public AgentForgeMetricsTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Scoped to the SUT's own Meter, not the shared name: another test class's AgentForgeMetrics
            // publishes the same instruments in parallel and would otherwise leak into both lists.
            if (ReferenceEquals(instrument.Meter, _sut.Meter))
            {
                // Kept as well as enabled: the instruments themselves carry the configuration that never
                // shows up in a measurement - bucket advice, unit, description - and a test that only ever
                // sees measurements cannot tell whether any of it was wired.
                _instruments.Add(instrument);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            _measurements.Add(new Measurement(instrument.Name, value, ToDictionary(tags))));
        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        _sut.Dispose();
    }

    [Fact]
    public void RecordAgentTurn_Succeeded_RecordsACountTaggedSuccessAndTheDuration()
    {
        _sut.RecordAgentTurn(AgentTurnType.Brief, true, TimeSpan.FromSeconds(1.5));

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.agent_turns" && (string)m.Tags["outcome"]! == "success");
        _measurements.Should().Contain(m => m.InstrumentName == "agentforge.agent_turn.duration" && (double)m.Value == 1.5);
    }

    [Fact]
    public void RecordAgentTurn_Failed_RecordsACountTaggedFailure()
    {
        _sut.RecordAgentTurn(AgentTurnType.Brief, false, TimeSpan.FromSeconds(0.5));

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.agent_turns" && (string)m.Tags["outcome"]! == "failure");
    }

    [Theory]
    [InlineData(AgentTurnType.Brief, "brief")]
    [InlineData(AgentTurnType.Agenda, "agenda")]
    [InlineData(AgentTurnType.FollowUp, "follow_up")]
    public void RecordAgentTurn_Always_TagsTheDurationHistogramWithTheTurnType(AgentTurnType turnType, string wireName)
    {
        // The whole point: without this label AgentForgeHighTurnLatencyP95 cannot filter to
        // NFR-PERF-1's budgeted RequestBrief population, and agenda fan-out swamps the quantile.
        _sut.RecordAgentTurn(turnType, true, TimeSpan.FromSeconds(3));

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.agent_turn.duration" && (string)m.Tags["turn_type"]! == wireName);
    }

    [Fact]
    public void AgentTurnTypeToWireName_OverEveryDeclaredValue_StaysBoundedToThreeLowCardinalityTokens()
    {
        // Cardinality guard: the tag multiplies every bucket of the
        // turn-duration histogram, so a fourth value is a deliberate decision, not a drive-by addition -
        // and no value may ever carry a patient, site or correlation id.
        var wireNames = Enum.GetValues<AgentTurnType>().Select(t => t.ToWireName()).ToArray();

        wireNames.Should().BeEquivalentTo(["brief", "agenda", "follow_up"]);
        wireNames.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(EvidenceRetrievalEntryPoint.EvidenceAsk, "evidence_ask")]
    [InlineData(EvidenceRetrievalEntryPoint.ChatTool, "chat_tool")]
    public void RecordEvidenceRetrieval_Always_TagsCountDurationAndResultsWithTheEntryPoint(
        EvidenceRetrievalEntryPoint entryPoint, string wireName)
    {
        // Both call sites feed ONE duration series, so the SLO covers the retrieval stage; the label only
        // says which path a slow sample came from.
        _sut.RecordEvidenceRetrieval(true, 3, TimeSpan.FromSeconds(2), entryPoint);

        foreach (var name in new[] { "agentforge.evidence_retrievals", "agentforge.evidence_retrieval.duration", "agentforge.evidence_retrieval.results" })
        {
            _measurements.Should().Contain(m => m.InstrumentName == name && (string)m.Tags["entry_point"]! == wireName);
        }
    }

    [Fact]
    public void EvidenceRetrievalEntryPointToWireName_OverEveryDeclaredValue_StaysBoundedToTwoTokens()
    {
        // Cardinality guard: the tag multiplies every bucket of the retrieval
        // histogram, so a third value is a decision, and no value may come from a request.
        var wireNames = Enum.GetValues<EvidenceRetrievalEntryPoint>().Select(e => e.ToWireName()).ToArray();

        wireNames.Should().BeEquivalentTo(["evidence_ask", "chat_tool"]);
        wireNames.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EvidenceRetrievalEntryPointToWireName_UndeclaredValue_Throws()
    {
        // A cast integer must not become a label value: the set stays the declared one.
        var act = () => ((EvidenceRetrievalEntryPoint)99).ToWireName();

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AgentTurnDurationBucketBoundariesSeconds_Always_ResolveTheNfrPerf1ThresholdAsItsOwnBoundary()
    {
        // OpenTelemetry .NET's DEFAULT boundaries put nothing between 10 and 50 except 25, so
        // histogram_quantile interpolates across a 25-second-wide bucket exactly where the 26s
        // NFR-PERF-1 threshold sits. 26 has to BE a boundary for the rule to resolve it.
        var boundaries = AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds;

        // This side pins the INSTRUMENT. AlertRuleThresholdTests pins the other side, reading the
        // threshold out of the rule file, so the two cannot drift apart in either direction.
        boundaries.Should().Contain(26d, "AgentForgeHighTurnLatencyP95 fires at > 26");
        boundaries.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        boundaries.Where(b => b is >= 20d and <= 30d).Should().HaveCountGreaterThanOrEqualTo(5,
            "the quantile has to resolve the 20-30s range the budget and the 25.8s baseline both sit in");
    }

    [Theory]
    [InlineData("agentforge.evidence_retrieval.duration")]
    [InlineData("agentforge.document_ingestion.duration")]
    public void Week2DurationBucketBoundariesSeconds_Always_PutBothSloTargetsOnABoundaryAndResolveFiveToFifteen(string instrument)
    {
        // OpenTelemetry .NET's DEFAULT boundaries leave 6s (retrieval SLO) inside the 5-10s bucket and 11s
        // (ingestion SLO) inside the 10-25s one, so histogram_quantile interpolates across the band the target
        // sits in. Each target has to BE a boundary, with a boundary every second from 5 to 15 around it.
        var boundaries = Week2DurationBoundaries(instrument);

        boundaries.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        boundaries.Should().Contain([6d, 11d], "6s and 11s are the NFR-SLO-W2-1 targets these series are read against");
        boundaries.Where(b => b is >= 5d and <= 15d).Should().Equal(
            [5d, 6d, 7d, 8d, 9d, 10d, 11d, 12d, 13d, 14d, 15d],
            "the quantile has to resolve 5-15s a second at a time, not as one wide band");
    }

    [Fact]
    public void RecordToolCall_Succeeded_RecordsACountAndDurationTaggedWithTheToolName()
    {
        _sut.RecordToolCall("get_labs", true, TimeSpan.FromMilliseconds(250));

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.tool_calls" &&
            (string)m.Tags["tool"]! == "get_labs" &&
            (string)m.Tags["outcome"]! == "success");
        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.tool_call.duration" && (string)m.Tags["tool"]! == "get_labs");
    }

    [Fact]
    public void RecordVerificationResult_Passed_RecordsACountTaggedPass()
    {
        _sut.RecordVerificationResult(true);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.verification_results" && (string)m.Tags["outcome"]! == "pass");
    }

    [Fact]
    public void RecordVerificationResult_Failed_RecordsACountTaggedFail()
    {
        _sut.RecordVerificationResult(false);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.verification_results" && (string)m.Tags["outcome"]! == "fail");
    }

    [Fact]
    public void RecordAuthorizationDecision_Permitted_RecordsACountTaggedPermitAndTheReason()
    {
        _sut.RecordAuthorizationDecision(true, "clinical-relationship");

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.authorization_decisions" &&
            (string)m.Tags["outcome"]! == "permit" &&
            (string)m.Tags["reason"]! == "clinical-relationship");
    }

    [Fact]
    public void RecordAuthorizationDecision_Refused_RecordsACountTaggedRefuseAndTheReason()
    {
        _sut.RecordAuthorizationDecision(false, "no-clinical-relationship");

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.authorization_decisions" &&
            (string)m.Tags["outcome"]! == "refuse" &&
            (string)m.Tags["reason"]! == "no-clinical-relationship");
    }

    [Fact]
    public void RecordAuthorizationDecision_Always_StaysOutOfTheToolCallInstruments()
    {
        // the FR-AUTH-2 decision is its own series by design. If it ever lands on
        // agentforge.tool_calls, AgentForgeHighToolFailureRate starts paging for correct behaviour.
        _sut.RecordAuthorizationDecision(false, "relationship-unresolved");

        _measurements.Should().NotContain(m => m.InstrumentName.StartsWith("agentforge.tool_call", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordOutOfScopeToolCall_Always_RecordsACountOnItsOwnInstrumentWithNoTags()
    {
        // this counter is NG1's only run-time signal, and PROMPTS.md §7 tells an operator it
        // should read zero - so a hollow implementation and a quiet system are indistinguishable without
        // this. Untagged is asserted, not incidental: the only value that could distinguish two of these
        // is the attempted tool name, which the model supplies, so it must not reach an exported label.
        _sut.RecordOutOfScopeToolCall();

        _measurements.Should().ContainSingle(m => m.InstrumentName == "agentforge.out_of_scope_tool_calls")
            .Which.Tags.Should().BeEmpty();
    }

    [Fact]
    public void RecordOutOfScopeToolCall_Always_StaysOutOfTheToolCallInstruments()
    {
        // The same boundary drew for the FR-AUTH-2 decision, for the same reason: refusing a tool
        // the catalog does not offer is correct behaviour, and on agentforge.tool_calls it would page an
        // operator through AgentForgeHighToolFailureRate. IAgentForgeMetrics promises this in prose; only
        // this case holds it.
        _sut.RecordOutOfScopeToolCall();

        _measurements.Should().NotContain(m => m.InstrumentName.StartsWith("agentforge.tool_call", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordLlmUsage_Always_RecordsInputAndOutputTokensSeparatelyPlusCost()
    {
        _sut.RecordLlmUsage(inputTokens: 100, outputTokens: 40, estimatedCostUsd: 0.02m);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.llm_tokens" && (string)m.Tags["direction"]! == "input" && (long)m.Value == 100);
        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.llm_tokens" && (string)m.Tags["direction"]! == "output" && (long)m.Value == 40);
        _measurements.Should().Contain(m => m.InstrumentName == "agentforge.llm_cost_usd" && (double)m.Value == 0.02);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("unlocatable")]
    [InlineData("unchecked")]
    public void RecordCitationQuoteMatch_Always_RecordsACountTaggedWithTheOutcome(string outcome)
    {
        // the VERBATIM rule's only visible signal. The ratio of unlocatable to exact is
        // what makes a fabricating model, or a regressed matcher, show up as a trend instead of as one
        // clinician noticing a highlight in the wrong place.
        _sut.RecordCitationQuoteMatch(outcome);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.citation_quote_matches" && (string)m.Tags["outcome"]! == outcome);
    }

    [Theory]
    [InlineData("lab_pdf", 1.0)]
    [InlineData("intake_form", 0.5)]
    public void RecordExtractionConfidence_Always_RecordsTheValueTaggedWithTheDocumentType(
        string documentType, double confidence)
    {
        // FR-OBS-W2-2's "extraction confidence per document". The tag is the document
        // TYPE, a two-member enum, and never the document, the patient or a field value.
        _sut.RecordExtractionConfidence(documentType, confidence);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.extraction_confidence" &&
            (string)m.Tags["document_type"]! == documentType &&
            (double)m.Value == confidence);
    }

    [Fact]
    public void ExtractionConfidenceInstrument_Always_CarriesTheExplicitBucketBoundariesAsAdvice()
    {
        // The constant below is worth nothing until it reaches the instrument, and dropping the `advice:`
        // argument from the CreateHistogram call is a one-token edit that no measurement-level assertion can
        // see: the exporter silently falls back to OpenTelemetry's defaults (0, 5, 10, 25, ...), every
        // fraction in [0,1] lands in le="5", and the panel degenerates into the average the histogram exists
        // to avoid - permanently, and without reddening again. Instrument<T>.Advice is the only place that
        // wiring is observable.
        var histogram = _instruments.OfType<Histogram<double>>()
            .Should().ContainSingle(instrument => instrument.Name == "agentforge.extraction_confidence")
            .Which;

        histogram.Advice.Should().NotBeNull("the instrument must carry explicit bucket advice, not the defaults");
        histogram.Advice!.HistogramBucketBoundaries.Should()
            .Equal(AgentForgeMetrics.ExtractionConfidenceBuckets);
    }

    [Fact]
    public void ExtractionConfidenceBuckets_Always_IsolateTheFabricationFloorAndResolveTheMiddle()
    {
        // What those boundaries have to be, as opposed to merely being set. The instrument records a
        // fraction in [0,1] - the share of a document's checkable quotes that were located - so the
        // boundaries must span exactly that, put the fabrication floor (0.0: not one checkable quote was
        // found) alone in the first bucket, and keep enough resolution that a partly-grounded document is
        // distinguishable from a well-grounded one.
        var buckets = AgentForgeMetrics.ExtractionConfidenceBuckets;

        buckets.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        buckets.Should().StartWith(0.0).And.EndWith(1.0);
        BucketIndex(buckets, 0.0).Should().NotBe(BucketIndex(buckets, 0.25));
        BucketIndex(buckets, 0.25).Should().NotBe(BucketIndex(buckets, 0.5));
        BucketIndex(buckets, 0.5).Should().NotBe(BucketIndex(buckets, 0.75));
        BucketIndex(buckets, 0.75).Should().NotBe(BucketIndex(buckets, 1.0));
    }

    [Theory]
    [InlineData("lab.result", "exact")]
    [InlineData("intake.medication", "unlocatable")]
    [InlineData("other", "unchecked")]
    public void RecordExtractionFieldOutcome_Always_RecordsACountTaggedWithFieldAndOutcome(
        string field, string outcome)
    {
        // The denominator half of "extraction field-level pass rate": every outcome is counted, so the rate
        // is exact / (exact + unlocatable + unchecked) per field rather than a numerator with nothing under
        // it.
        _sut.RecordExtractionFieldOutcome(field, outcome);

        _measurements.Should().Contain(m =>
            m.InstrumentName == "agentforge.extraction_field_outcomes" &&
            (string)m.Tags["field"]! == field &&
            (string)m.Tags["outcome"]! == outcome);
    }

    private static IReadOnlyList<double> Week2DurationBoundaries(string instrument) => instrument switch
    {
        "agentforge.evidence_retrieval.duration" => AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds,
        "agentforge.document_ingestion.duration" => AgentForgeMetrics.DocumentIngestionDurationBucketBoundariesSeconds,
        _ => throw new ArgumentOutOfRangeException(nameof(instrument), instrument, null),
    };

    // Which bucket a value falls in: the first boundary it does not exceed, or one past the end.
    private static int BucketIndex(IReadOnlyList<double> boundaries, double value)
    {
        for (var i = 0; i < boundaries.Count; i++)
        {
            if (value <= boundaries[i])
            {
                return i;
            }
        }

        return boundaries.Count;
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> dictionary = [];
        foreach (var tag in tags)
        {
            dictionary[tag.Key] = tag.Value;
        }

        return dictionary;
    }

    private sealed record Measurement(string InstrumentName, object Value, IReadOnlyDictionary<string, object?> Tags);
}
