using AgentForge.Evals;
using FluentAssertions;

namespace AgentForge.EvalTests;

/// <summary>
/// The evidence slice's own guards — the tests that grade the grader, one slice over from
/// <see cref="AnswerPathRubricTests"/>. Everything here is about the instrument rather than about any one
/// golden case: that an evidence case cannot be counted by a rubric that does not check it, that neither
/// rubric can be claimed over an empty expectation, that each population is two-sided, and — the part this
/// slice exists for — that each rubric actually <b>fails</b> on the mutation it claims to catch.
/// <para>
/// A rubric added with no cases to grade passes vacuously, which is indistinguishable from a working
/// retriever. Separate changes were deliberately landed together for that reason; these tests are what
/// keep the pairing true after the fact.
/// </para>
/// </summary>
public sealed class EvidenceRubricTests
{
    private const string RelevantChunk = "syn-gl-af-warfarin-inr-01";
    private const string DistractorChunk = "syn-gl-af-doac-renal-02";
    private const string InventedChunk = "syn-gl-af-doac-dose-reduction-77";

    // Both rubrics, because RubricEvaluator refuses an evidence case declaring only one: the slice counts
    // every evidence case, so a case either rubric skipped would be counted and inspected by nothing. The
    // behavioural tests below therefore declare the pair and assert the one under test.
    private static readonly string[] BothRubrics = ["retrieval_hit", "evidence_grounded"];

    private static readonly string GroundedAnswer =
        $"For atrial fibrillation on warfarin the target INR is 2.0 to 3.0 [Guideline/{RelevantChunk}].";

    /// <summary>A retrieved set and a shipped answer that satisfy every clause, so only the mutation under
    /// test can turn a rubric false.</summary>
    private static CaseOutcome RetrievedOutcome => new(
        Succeeded: true,
        ResultJson: GroundedAnswer,
        RejectionReason: null,
        Logs: [],
        SuppressedLines: [],
        ConstraintRuleIds: [],
        RetrievedChunkIds: [RelevantChunk, DistractorChunk],
        DegradedStages: []);

    /// <summary>A scenario every field of which is populated, so only the guard under test can throw.</summary>
    private static EvidenceScenario ScorableScenario() => new()
    {
        Intent = "synthetic scenario for the guard under test",
        PatientId = "syn-patient-guard",
        Question = "synthetic question",
        Corpus =
        [
            new GuidelineChunkFixture
            {
                ChunkId = RelevantChunk,
                DocumentId = "Synthetic Cardiology Guidance 2026 - Anticoagulation",
                Section = "Warfarin INR target",
                Text = "For most patients with atrial fibrillation on warfarin, the target INR is 2.0 to 3.0.",
            },
        ],
        SparseRanking = [RelevantChunk],
        DenseRanking = [RelevantChunk],
        ExpectedChunkIds = [RelevantChunk],
        ExpectedGuidelineCitations = [$"Guideline/{RelevantChunk}"],
        ModelScript = [new ModelTurnFixture { Text = GroundedAnswer }],
    };

    private static GoldenCase EvidenceCase(
        IReadOnlyList<string> rubrics, EvidenceScenario? scenario = null, IReadOnlyList<string>? expectedValues = null) => new()
        {
            Id = "evidence-guard-synthetic",
            Guards = "A synthetic evidence case standing in for one whose rubrics do not cover what it is counted by.",
            Category = RubricEvaluator.EvidenceCategory,
            ExpectSuccess = true,
            ExpectedValues = expectedValues,
            Rubrics = rubrics,
            Evidence = scenario ?? ScorableScenario(),
        };

    private static IReadOnlyList<GoldenCase> EvidenceCases() =>
        [.. GoldenSet.CaseFileNames().Select(GoldenSet.Load)
            .Where(c => c.Category == RubricEvaluator.EvidenceCategory)];

    // FluentAssertions' Contain takes an expression tree, which may not carry `?.` — so the null-tolerant
    // count lives in a method the tree can call instead.
    private static int Size(IReadOnlyList<string>? items) => items?.Count ?? 0;

    // Same reason as Size: an expression tree may not carry an `is` pattern either.
    private static bool PinsAnOrder(EvidenceScenario scenario) =>
        scenario.ExpectedTopChunkId != null || scenario.ExpectedChunkOrder != null;

    /// <summary>
    /// Given an evidence case that does not declare one of the slice's two rubrics, when it is scored, then
    /// scoring fails loudly and names the rubric. Same defect as `M3`'s and `M1`/`M2`/`M5`'s
    /// : the slice's denominator is every evidence case, but a rubric only counts the cases that
    /// declare it, so an undeclared case is reported as inspected and found clean.
    /// </summary>
    [Theory]
    [InlineData("no_phi_in_logs", "retrieval_hit")]
    [InlineData("retrieval_hit", "evidence_grounded")]
    [InlineData("evidence_grounded", "retrieval_hit")]
    public void Evaluate_WhenEvidenceCaseOmitsAnEvidenceRubric_Throws(string rubrics, string missing)
    {
        var undercheckedCase = EvidenceCase(rubrics.Split(','));

        var score = () => RubricEvaluator.Evaluate(undercheckedCase, RetrievedOutcome);

        score.Should().Throw<InvalidOperationException>(
                "an evidence case lands in the slice's denominator whatever it declares")
            .WithMessage("*evidence-guard-synthetic*")
            .WithMessage($"*{missing}*");
    }

    /// <summary>
    /// Given a case claiming `retrieval_hit` that names no expected chunk and is not the out-of-corpus
    /// control, when it is scored, then scoring throws — a retrieval check with nothing to look for is the
    /// failure the rubric exists to prevent, and it is the shape a vacuous rubric takes.
    /// </summary>
    [Fact]
    public void Evaluate_WhenRetrievalHitClaimedWithNothingExpected_Throws()
    {
        var emptyCase = EvidenceCase(
            BothRubrics, ScorableScenario() with { ExpectedChunkIds = null });

        var score = () => RubricEvaluator.Evaluate(emptyCase, RetrievedOutcome);

        score.Should().Throw<InvalidOperationException>().WithMessage("*retrieval_hit*");
    }

    /// <summary>
    /// Given a case claiming `evidence_grounded` that names neither a citation that must survive nor one
    /// that must be suppressed, and is not the out-of-corpus control, when it is scored, then scoring
    /// throws.
    /// </summary>
    [Fact]
    public void Evaluate_WhenEvidenceGroundedClaimedWithNothingToCheck_Throws()
    {
        var emptyCase = EvidenceCase(
            BothRubrics,
            ScorableScenario() with { ExpectedGuidelineCitations = null, ExpectedSuppressedCitations = null });

        var score = () => RubricEvaluator.Evaluate(emptyCase, RetrievedOutcome);

        score.Should().Throw<InvalidOperationException>().WithMessage("*evidence_grounded*");
    }

    /// <summary>
    /// Given a seeded chunk the case says must come back, when the retriever did not return it, then
    /// `retrieval_hit` fails. This is the rubric's whole content and the first thing a fusion regression
    /// breaks.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTheExpectedChunkIsMissingFromTheRetrievedSet_FailsRetrievalHit()
    {
        var testCase = EvidenceCase(BothRubrics);

        var scores = RubricEvaluator.Evaluate(
            testCase, RetrievedOutcome with { RetrievedChunkIds = [DistractorChunk] });

        scores["retrieval_hit"].Should().BeFalse(
            "the chunk that answers the question was not among what retrieval returned");
    }

    /// <summary>
    /// Given a case that pins which chunk must rank first, when the reranked list leads with another, then
    /// `retrieval_hit` fails — membership cannot see this, because the relevant chunk is still in the set.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTheRerankedTopChunkIsNotTheRelevantOne_FailsRetrievalHit()
    {
        var testCase = EvidenceCase(
            BothRubrics, ScorableScenario() with { ExpectedTopChunkId = RelevantChunk });

        var scores = RubricEvaluator.Evaluate(
            testCase, RetrievedOutcome with { RetrievedChunkIds = [DistractorChunk, RelevantChunk] });

        scores["retrieval_hit"].Should().BeFalse(
            "the composer leads with whatever ranked first, so a reranker that stopped promoting the "
            + "relevant chunk changes the answer without changing the retrieved set");
    }

    /// <summary>
    /// Given a case that expects the whole pipeline to run, when a stage degraded anyway, then
    /// `retrieval_hit` fails. The stage set is compared for equality rather than containment precisely so a
    /// half that silently drops out cannot pass a case that never asked for it.
    /// </summary>
    [Fact]
    public void Evaluate_WhenAStageDegradedWithoutTheCaseExpectingIt_FailsRetrievalHit()
    {
        var testCase = EvidenceCase(BothRubrics);

        var scores = RubricEvaluator.Evaluate(
            testCase, RetrievedOutcome with { DegradedStages = ["rerank"] });

        scores["retrieval_hit"].Should().BeFalse(
            "a retriever quietly running on one half is the failure RecordRetrievalDegradation exists to surface");
    }

    /// <summary>
    /// Given the out-of-corpus control, when the retriever returned something anyway, then `retrieval_hit`
    /// fails. This is the specificity half: without it, a retriever that returned the whole corpus for
    /// every query would satisfy every membership check in the slice.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTheOutOfCorpusControlRetrievedSomething_FailsRetrievalHit()
    {
        var control = EvidenceCase(
            BothRubrics,
            ScorableScenario() with { ExpectedChunkIds = null, ExpectNoEvidence = true });

        var scores = RubricEvaluator.Evaluate(control, RetrievedOutcome);

        scores["retrieval_hit"].Should().BeFalse(
            "a query the corpus does not cover must retrieve nothing, or the membership checks measure nothing");
    }

    /// <summary>
    /// Given a guideline citation the case says must ship, when it names a chunk this turn did not
    /// retrieve, then `evidence_grounded` fails — a citation that resolves to nothing is the fabrication
    /// the rubric is named for, however well formed the token is.
    /// </summary>
    [Fact]
    public void Evaluate_WhenACitedGuidelineChunkWasNeverRetrieved_FailsEvidenceGrounded()
    {
        var testCase = EvidenceCase(BothRubrics);

        var scores = RubricEvaluator.Evaluate(
            testCase, RetrievedOutcome with { RetrievedChunkIds = [DistractorChunk] });

        scores["evidence_grounded"].Should().BeFalse(
            "the answer cites a guideline chunk retrieval never returned, which is an invented source");
    }

    /// <summary>
    /// Given an answer carrying a guideline citation no retrieved chunk backs, when it survived into what
    /// shipped, then `evidence_grounded` fails even though every citation the case named is present. The
    /// anti-fabrication clause is over the answer, not over the case's list, because the case cannot
    /// enumerate what a model might invent.
    /// </summary>
    [Fact]
    public void Evaluate_WhenAFabricatedCitationSurvivedBesideARealOne_FailsEvidenceGrounded()
    {
        var testCase = EvidenceCase(BothRubrics);
        var leaked = RetrievedOutcome with
        {
            ResultJson = GroundedAnswer
                + $"\nReduce apixaban to 2.5 twice daily [Guideline/{InventedChunk}].",
        };

        var scores = RubricEvaluator.Evaluate(testCase, leaked);

        scores["evidence_grounded"].Should().BeFalse(
            "a fabricated citation beside a real one is the production shape, and the case never listed it");
    }

    /// <summary>
    /// Given a citation the case says the critic must suppress, when it was recorded as suppressed but is
    /// still present in what shipped, then `evidence_grounded` fails. Recording a line is not removing it —
    /// the same distinction `grounded_answer` draws for record facts.
    /// </summary>
    [Fact]
    public void Evaluate_WhenASuppressedCitationStillAppearsInTheShippedAnswer_FailsEvidenceGrounded()
    {
        var invented = $"Reduce apixaban to 2.5 twice daily [Guideline/{InventedChunk}].";
        var testCase = EvidenceCase(
            BothRubrics,
            ScorableScenario() with { ExpectedSuppressedCitations = [$"Guideline/{InventedChunk}"] });

        var scores = RubricEvaluator.Evaluate(
            testCase,
            RetrievedOutcome with { ResultJson = $"{GroundedAnswer}\n{invented}", SuppressedLines = [invented] });

        scores["evidence_grounded"].Should().BeFalse(
            "a claim in the suppressed list and in the answer at once reached the clinician anyway");
    }

    /// <summary>
    /// Given the out-of-corpus control, when the answer does not carry the gap statement the case pins,
    /// then `evidence_grounded` fails — "no evidence found" has to be said, not merely not contradicted.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTheOutOfCorpusAnswerDoesNotStateTheGap_FailsEvidenceGrounded()
    {
        var control = EvidenceCase(
            BothRubrics,
            ScorableScenario() with { ExpectedGuidelineCitations = null, ExpectNoEvidence = true },
            expectedValues: ["No guideline evidence on file covers"]);

        var scores = RubricEvaluator.Evaluate(
            control, RetrievedOutcome with { ResultJson = string.Empty, RetrievedChunkIds = [] });

        scores["evidence_grounded"].Should().BeFalse(
            "an empty answer is the silent failure, not a transparent one");
    }

    /// <summary>
    /// Given a case whose pipeline threw, when it is scored, then every rubric it declares fails — even
    /// the ones an empty outcome would satisfy. A thrown out-of-corpus control retrieved nothing and
    /// logged nothing, so without this it would score <c>retrieval_hit</c> and <c>no_phi_in_logs</c> as
    /// passes over a run that never happened.
    /// </summary>
    [Fact]
    public void Evaluate_WhenTheCaseFaulted_FailsEveryDeclaredRubric()
    {
        var control = EvidenceCase(
            [.. BothRubrics, "no_phi_in_logs"],
            ScorableScenario() with { ExpectedChunkIds = null, ExpectedGuidelineCitations = null, ExpectNoEvidence = true });

        var scores = RubricEvaluator.Evaluate(
            control,
            RetrievedOutcome with
            {
                ResultJson = string.Empty,
                RetrievedChunkIds = [],
                Fault = "KeyNotFoundException: The given key was not present in the dictionary.",
            });

        scores.Should().HaveCount(3).And.OnlyContain(
            score => !score.Value,
            "a case that threw was checked by nothing, so no rubric may report it inspected and clean");
    }

    /// <summary>
    /// Given an evidence case whose pipeline throws past the harness, when it is run, then the runner
    /// returns a faulted outcome naming the exception rather than propagating it — so the gate prints the
    /// case's rubric failures and its <c>guards</c> line instead of a stack trace that names neither.
    /// Losing a body from the hybrid retriever's hydration map throws exactly this way; the
    /// composer's provider failure is the one a fixture can reach without mutating production.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenThePipelineUnderTestThrows_ReturnsAFaultNamingTheException()
    {
        var throwing = EvidenceCase(
            BothRubrics,
            ScorableScenario() with { ModelScript = [new ModelTurnFixture { FailsWith = "provider_error" }] });

        var outcome = await EvalCaseRunner.RunAsync(throwing);

        outcome.Fault.Should().StartWith("HttpRequestException",
            "the reader of a red gate needs to know what threw, not only that a rubric slipped");
        RubricEvaluator.Evaluate(throwing, outcome).Values.Should().OnlyContain(passed => !passed);
    }

    /// <summary>
    /// Given the committed golden set, when the evidence cases are read, then every one declares both
    /// rubrics — so the numbers the slice prints cover exactly the population they name.
    /// </summary>
    [Fact]
    public void EvidenceCases_WhenLoaded_EachDeclareBothEvidenceRubrics()
    {
        var underchecked_ = EvidenceCases()
            .Where(c => RubricEvaluator.EvidenceRubrics.Any(r => !c.Rubrics.Contains(r, StringComparer.Ordinal)))
            .Select(c => c.Id)
            .ToArray();

        underchecked_.Should().BeEmpty(
            "the slice counts every evidence case, so one declaring neither retrieval_hit nor "
            + "evidence_grounded is reported as inspected and found clean");
    }

    /// <summary>
    /// Given the committed golden set, when the evidence population is counted, then it is non-empty and
    /// holds every shape the slice is stated over. The list is the whole content: a retrieval
    /// hit, an ordering the reranker has to produce, a query the corpus cannot answer, a stage that fails,
    /// and both sides of citation grounding. Delete any one and two rubrics start passing over a
    /// population that no longer contains what they claim to score.
    /// </summary>
    [Fact]
    public void EvidencePopulation_WhenCounted_HoldsEveryShapeTheSliceIsStatedOver()
    {
        var evidence = EvidenceCases();

        evidence.Should().NotBeEmpty(
            "retrieval_hit and evidence_grounded over an empty population pass vacuously, which reads "
            + "exactly like a working retriever");
        evidence.Should().Contain(
            c => !c.Evidence!.ExpectNoEvidence && Size(c.Evidence!.ExpectedChunkIds) > 0,
            "a seeded query that must return its chunk is the slice's floor");
        evidence.Should().Contain(
            c => c.Evidence!.ExpectNoEvidence,
            "a retriever returning the whole corpus for every query would satisfy every membership check");
        evidence.Should().Contain(
            c => PinsAnOrder(c.Evidence!),
            "membership alone cannot tell the reranker from the fused order it was meant to improve on");
        evidence.Should().Contain(
            c => c.Evidence!.ExpectedDegradedStages.Count > 0,
            "the degradation path is where a silent retrieval failure hides");
        evidence.Should().Contain(
            c => Size(c.Evidence!.ExpectedGuidelineCitations) > 0,
            "a guideline claim that must ship, cited, is evidence_grounded's permit case");
        evidence.Should().Contain(
            c => Size(c.Evidence!.ExpectedSuppressedCitations) > 0,
            "a critic that suppressed every guideline citation would pass every fabrication case while "
            + "delivering no evidence at all");
    }

    /// <summary>
    /// Given the committed golden set, when the evidence cases are read, then each of the three places
    /// <c>HybridEvidenceRetriever</c> cuts the pool to <c>topK</c> — the reranker's own ranking, an empty
    /// ranking that leaves the fused order standing, and a reranker that threw — has a case whose fused
    /// pool of at least six chunks is longer than the order it pins, and whose draft cites a corpus chunk
    /// that order leaves out. Until a separate change no evidence corpus exceeded three chunks, so no pool reached
    /// <c>topK</c> and deleting every truncation left the gate green. The shape is checked rather than
    /// the constant: pinning five ids over a six-chunk pool fails whatever <c>topK</c> becomes.
    /// </summary>
    [Fact]
    public void EvidencePopulation_WhenCounted_TruncatesTheFusedPoolOnEveryTopKPath()
    {
        var truncating = EvidenceCases().Select(c => c.Evidence!).Where(DropsACitedCorpusChunk).ToArray();

        truncating.Should().Contain(
            s => s.RerankRanking == null && !Fails(s, "rerank"),
            "the fused order stands when the reranker returns nothing, which is the disabled reranker's "
            + "production shape, and that path has its own truncation");
        truncating.Should().Contain(
            s => s.RerankRanking != null && s.RerankIgnoresTopK && Size(s.RerankRanking) > Size(s.ExpectedChunkOrder),
            "a reranker returning more than it was asked for is the only way the reranked path's own cap "
            + "is reachable - one that honours topK hides it");
        truncating.Should().Contain(
            s => Fails(s, "rerank"),
            "the degradation path falls back to the fused order and has to cap it too");
    }

    // Same reason as Size: an expression tree may not carry a method group with a comparer argument.
    private static bool Fails(EvidenceScenario scenario, string stage) =>
        scenario.FailingStages.Contains(stage, StringComparer.Ordinal);

    private static bool DropsACitedCorpusChunk(EvidenceScenario scenario)
    {
        if (scenario.ExpectedChunkOrder is not { } order)
        {
            return false;
        }

        var pool = scenario.SparseRanking.Concat(scenario.DenseRanking).Distinct(StringComparer.Ordinal).ToArray();
        var corpus = scenario.Corpus.Select(chunk => chunk.ChunkId).ToHashSet(StringComparer.Ordinal);
        var dropped = pool.Except(order, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        return pool.Length >= 6
            && pool.Length > order.Count
            && (scenario.ExpectedSuppressedCitations ?? []).Any(citation =>
            {
                var chunkId = citation[(citation.IndexOf('/', StringComparison.Ordinal) + 1)..];
                return corpus.Contains(chunkId) && dropped.Contains(chunkId);
            });
    }
}
