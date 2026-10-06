using System.Text.Json;
using AgentForge.Evals;
using AgentForge.Verification;

// Golden-set eval gate (Week 2 Core Req 6 / HARD GATE). Deterministic: each case pins the model's response
// (extraction), the tool call a compromised model would emit (authorization), the model's whole turn
// sequence (answer), or both retrieval halves and the reranker (evidence), so a regression anywhere in the
// pipeline flips a case and fails the build. No live API, no database needed.
var evalsDir = args.Length > 0 ? args[0] : "evals";
var goldenDir = Path.Combine(evalsDir, "golden");
var baselinePath = Path.Combine(evalsDir, "baseline.json");
var resultsPath = Path.Combine(evalsDir, "results.json");

if (!Directory.Exists(goldenDir) || !File.Exists(baselinePath))
{
    await Console.Error.WriteLineAsync($"Eval assets not found under '{evalsDir}' (need golden/ and baseline.json).");
    return 2;
}

// Through the shared loader, which is what refuses a case stating no failure mode - the xUnit tier loads the
// same way, so a case cannot be added past one and caught only by the other. A malformed or undocumented case
// is an asset problem, not a rubric result, so it exits 2 like a missing golden/ does rather than surfacing as
// an unhandled exception with no exit code anyone documented. A separate change
IReadOnlyList<GoldenCase> cases;
try
{
    cases = GoldenCaseLoader.LoadAll(goldenDir);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

if (cases.Count == 0)
{
    await Console.Error.WriteLineAsync("No golden cases found.");
    return 2;
}

// Through EvalGatePolicy so the xUnit tier reads the same file the same way - the policy is asserted there
// against the committed case counts, and a second parse here would be a second policy. A separate change
EvalBaseline baseline;
try
{
    baseline = EvalGatePolicy.Load(baselinePath);
}
catch (InvalidOperationException ex)
{
    // Same shape as the golden-case loader above, and for the same reason: a hand-edited baseline.json is
    // a policy error, so it reports in the gate's own voice. Without this an absent `tier` key - one of the
    // three ways to get a category's tier wrong - blocked as a deserializer stack trace while the other two
    // printed a named gate line. A separate change
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

var perRubricPass = new Dictionary<string, int>(StringComparer.Ordinal);
var perRubricTotal = new Dictionary<string, int>(StringComparer.Ordinal);
var caseReports = new List<object>();

// A rubric rate names what fell over, never why it mattered. Collected per case so the red build can print
// the failing case's own `guards` line next to the rubrics it failed. A separate change
var failedCases = new List<(string Id, string[] Rubrics, string Guards, string? Fault)>();

// Collected from the outcome, not from failedCases, so a fault blocks even where no rubric recorded it.
var faultedCases = new List<string>();

// M3 ("0 unauthorized disclosures across role/injection eval cases") is counted here rather than inferred
// from a rubric rate, because a rate cannot distinguish zero failures from zero cases - which is precisely
// how the metric read green while the population was empty. A separate change
var authorizationCases = 0;
var permitCases = 0;
var denyCases = 0;
var unauthorizedDisclosures = 0;
var loggedAttempts = 0;

// M1, M2 and M5 are counted the same way and for the same reason: all three were "not measured" against the
// Week 1 answer path, and a rate over an absent population reads exactly like a clean result
// (METRICS.md §2). Separate changes
var m1Cases = 0;
var m1SuppressionCases = 0;
var m1ShippedIntactCases = 0;
var m1KnownEscapes = 0;

// NG1's "does not recommend treatment" has no mechanism below the model, so its pinned escape is counted beside
// M1 rather than inside it - a green gate must not read as "no recommendation can ship". A separate change
var m1ScopeEscapes = 0;
var ungroundedClaimsShipped = 0;

var seededViolations = 0;
var flaggedViolations = 0;
var nearMissControls = 0;
var controlFlagsRaised = 0;
var seededRuleClasses = new HashSet<string>(StringComparer.Ordinal);

var m5Cases = 0;
var m5FallbackCases = 0;
var m5SynthesizedCases = 0;
var silentOrFabricatedAnswers = 0;

// The evidence slice, counted for the same reason as the four above and no other: hybrid RAG shipped scored
// by nothing, so "retrieval_hit 100%" over zero cases read exactly like a working retriever, and the brief
// grades the gate by injecting a regression. Separate changes
var evidenceCases = 0;
var evidenceHitCases = 0;
var outOfCorpusControls = 0;
var stageFailureCases = 0;
var rerankOrderCases = 0;
var citationSurvivalCases = 0;
var citationSuppressionCases = 0;
var missedExpectedChunks = 0;
var ungroundedGuidelineClaims = 0;

// A metric's denominator is every case carrying it, so every such case has to be one the metric actually
// checks. Reported here, ahead of scoring, so the gate names every offender at once rather than dying on
// the first - RubricEvaluator.RequireMetricCoverage still refuses one that reaches it by another path.
var undercheckedCases = cases
    .Select(c => (c.Id, Metric: RubricEvaluator.RequiredMetricLabel(c), Missing: RubricEvaluator.MissingRequiredRubrics(c)))
    .Where(x => x.Missing.Count > 0)
    .OrderBy(x => x.Id, StringComparer.Ordinal)
    .ToArray();

foreach (var (id, metric, missing) in undercheckedCases)
{
    await Console.Error.WriteLineAsync(
        $"{metric}: case '{id}' does not declare {string.Join(" or ", missing)} - it is counted in " +
        "the denominator and checked by nothing.");
}

if (undercheckedCases.Length > 0)
{
    await Console.Error.WriteLineAsync(
        "FAIL - the eval gate blocked the build: a metric would report over a population it did not inspect.");
    return 1;
}

foreach (var testCase in cases.OrderBy(c => c.Id, StringComparer.Ordinal))
{
    var outcome = await EvalCaseRunner.RunAsync(testCase);
    var scores = RubricEvaluator.Evaluate(testCase, outcome);
    if (outcome.Fault is not null)
    {
        faultedCases.Add(testCase.Id);
    }

    foreach (var (rubric, passed) in scores)
    {
        perRubricTotal[rubric] = perRubricTotal.GetValueOrDefault(rubric) + 1;
        if (passed)
        {
            perRubricPass[rubric] = perRubricPass.GetValueOrDefault(rubric) + 1;
        }
    }

    if (testCase.Category == RubricEvaluator.AuthorizationCategory)
    {
        authorizationCases++;
        if (testCase.ExpectSuccess)
        {
            permitCases++;
        }
        else
        {
            denyCases++;
        }

        // Indexed, not defaulted: this denominator is every authorization case, so every authorization case
        // must have been scored by both M3 rubrics. RubricEvaluator.RequireMetricCoverage already refuses a
        // case that declares neither - reading the key rather than defaulting it keeps that invariant
        // load-bearing at this end too, instead of counting a case nothing checked. A separate change
        if (!scores[RubricEvaluator.NoUnauthorizedDisclosure])
        {
            unauthorizedDisclosures++;
        }

        if (scores[RubricEvaluator.AttemptLogged])
        {
            loggedAttempts++;
        }
    }

    if (testCase.Category == RubricEvaluator.AnswerCategory)
    {
        var scenario = testCase.Answer!;
        switch (scenario.Metric)
        {
            case "M1":
                m1Cases++;
                if ((scenario.ExpectedSuppressed?.Count ?? 0) > 0)
                {
                    m1SuppressionCases++;
                }
                else
                {
                    m1ShippedIntactCases++;
                }

                if (scenario.KnownKeywordEscape)
                {
                    m1KnownEscapes++;
                }

                if (scenario.KnownScopeEscape)
                {
                    m1ScopeEscapes++;
                }

                // Indexed, not defaulted - see the M3 note above.
                if (!scores[RubricEvaluator.GroundedAnswer])
                {
                    ungroundedClaimsShipped++;
                }

                break;

            case "M2":
                // Counted from the outcome, not from the rubric boolean: M2 is a recall *rate* over seeded
                // violations, and a pass/fail per case cannot produce one.
                var raised = outcome.ConstraintRuleIds ?? [];
                if (scenario.ExpectNoConstraintFlags)
                {
                    nearMissControls++;
                    controlFlagsRaised += raised.Count;
                }

                foreach (var ruleId in (scenario.ExpectedFlagRuleIds ?? []).Distinct(StringComparer.Ordinal))
                {
                    seededViolations++;
                    seededRuleClasses.Add(ruleId);
                    if (raised.Contains(ruleId, StringComparer.Ordinal))
                    {
                        flaggedViolations++;
                    }
                }

                break;

            case "M5":
                m5Cases++;
                if (testCase.ExpectSuccess)
                {
                    m5SynthesizedCases++;
                }
                else
                {
                    m5FallbackCases++;
                }

                if (!scores[RubricEvaluator.TransparentDegradation])
                {
                    silentOrFabricatedAnswers++;
                }

                break;

            default:
                throw new InvalidOperationException(
                    $"Answer case '{testCase.Id}' declares metric '{scenario.Metric}', which nothing reports over.");
        }
    }

    if (testCase.Category == RubricEvaluator.EvidenceCategory)
    {
        var scenario = testCase.Evidence!;
        evidenceCases++;

        if (scenario.ExpectNoEvidence)
        {
            outOfCorpusControls++;
        }
        else
        {
            evidenceHitCases++;
        }

        if (scenario.ExpectedDegradedStages.Count > 0)
        {
            stageFailureCases++;
        }

        if (scenario.ExpectedTopChunkId is not null || scenario.ExpectedChunkOrder is not null)
        {
            rerankOrderCases++;
        }

        if ((scenario.ExpectedGuidelineCitations?.Count ?? 0) > 0)
        {
            citationSurvivalCases++;
        }

        if ((scenario.ExpectedSuppressedCitations?.Count ?? 0) > 0)
        {
            citationSuppressionCases++;
        }

        // Indexed, not defaulted - see the M3 note above. Every evidence case declares both rubrics.
        if (!scores[RubricEvaluator.RetrievalHit])
        {
            missedExpectedChunks++;
        }

        if (!scores[RubricEvaluator.EvidenceGrounded])
        {
            ungroundedGuidelineClaims++;
        }
    }

    var failedRubrics = scores.Where(s => !s.Value).Select(s => s.Key).OrderBy(r => r, StringComparer.Ordinal).ToArray();
    if (failedRubrics.Length > 0)
    {
        failedCases.Add((testCase.Id, failedRubrics, testCase.Guards, outcome.Fault));
    }

    caseReports.Add(new
    {
        id = testCase.Id,
        category = testCase.Category,
        vector = testCase.Authorization?.Vector,
        metric = testCase.Answer?.Metric,
        retrieved_chunk_ids = outcome.RetrievedChunkIds,
        degraded_stages = outcome.DegradedStages,
        fault = outcome.Fault,
        guards = testCase.Guards,
        scores,
    });
}

var categoryRates = new Dictionary<string, double>(StringComparer.Ordinal);
var failures = new List<string>();
foreach (var rubric in perRubricTotal.Keys.OrderBy(k => k, StringComparer.Ordinal))
{
    var rate = (double)perRubricPass.GetValueOrDefault(rubric) / perRubricTotal[rubric];
    categoryRates[rubric] = rate;

    // Both arms live in EvalGatePolicy.Evaluate: the category's TIER floor and the >5%-per-category
    // regression the Week 2 brief names. The second was dead code everywhere; it is live in
    // the quality tier and DELIBERATELY unreachable in the safety tier, where a 100% floor already refuses
    // any drop at all. That file carries the tiers and why 0.80 is not a rounder number. A separate change
    if (EvalGatePolicy.Evaluate(rubric, rate, baseline) is { } failure)
    {
        failures.Add(failure);
    }
}

// A baseline entry no case scores is the same defect one direction over: the loop above walks the rubrics
// the RUN reported, so deleting every case declaring a rubric drops it out of the loop silently while
// baseline.json goes on claiming it at 100%. A separate change
foreach (var orphan in EvalGatePolicy.OrphanedBaselineCategories(baseline, categoryRates.Keys))
{
    failures.Add(
        $"{orphan}: baseline.json records a baseline for this category but no golden case declares the " +
        "rubric - a category nothing scored is not a category that passed");
}

// A case that threw fails every rubric it declares, which drags down rates it never measured - a thrown
// case reads as a PHI leak in no_phi_in_logs. This line blocks on its own, whatever the rates say, and says
// which it was. A separate change
if (faultedCases.Count > 0)
{
    failures.Add(
        $"{faultedCases.Count} case(s) threw before any rubric inspected them ({string.Join(", ", faultedCases)}) - " +
        "every rubric they declare is counted failed above, so those rates include cases nothing measured");
}

// An empty M3 population is a gate failure, not a clean sheet: "0 unauthorized disclosures" over no cases
// is arithmetic, and reads identically to the real result. Both sides are required for the same reason - a
// suite that only ever denies passes every denial assertion while denying everyone.
if (authorizationCases == 0)
{
    failures.Add("M3: no authorization cases exist, so 0 unauthorized disclosures is an empty set, not a result");
}
else if (permitCases == 0 || denyCases == 0)
{
    failures.Add(
        $"M3: the population is one-sided ({permitCases} permit / {denyCases} deny) - a suite that never " +
        "permits, or never denies, cannot show the gate discriminating");
}

// M1's population guards, for the same reason and in the same shape. The second is the deny-everything
// defect one metric over: a verifier that suppressed every line would pass every suppression case.
if (m1Cases == 0)
{
    failures.Add("M1: no answer-path cases exist, so 0 ungrounded claims is an empty set, not a result");
}
else if (m1SuppressionCases == 0 || m1ShippedIntactCases == 0)
{
    failures.Add(
        $"M1: the population is one-sided ({m1SuppressionCases} suppression / {m1ShippedIntactCases} " +
        "shipped-intact) - a verifier that suppressed everything would pass every suppression case");
}

// The escapes print beside M1, so a case that vanished would print zero and read as a closed limit.
failures.AddRange(EvalGatePolicy.PinnedEscapeDrift(
    baseline,
    new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [EvalGatePolicy.KeywordBoundaryEscape] = m1KnownEscapes,
        [EvalGatePolicy.ScopeEscape] = m1ScopeEscapes,
    }));

// M2's target is 100% and no constraint class may be missed, so recall is checked against the
// whole default rule set rather than against whatever the population happens to seed. The near-miss control
// is the specificity half: recall alone is swept by an engine that flags every chart.
var unseededRuleClasses = CardiologyConstraintRules.Default
    .Select(rule => rule.RuleId)
    .Where(id => !seededRuleClasses.Contains(id))
    .OrderBy(id => id, StringComparer.Ordinal)
    .ToArray();

if (seededViolations == 0)
{
    failures.Add("M2: no constraint violations are seeded, so a recall rate over them is arithmetic, not a result");
}
else if (flaggedViolations < seededViolations)
{
    failures.Add(
        $"M2: {flaggedViolations}/{seededViolations} seeded constraint violations were flagged - the target " +
        "is 100%, zero tolerance (REQUIREMENTS.md §14)");
}

if (unseededRuleClasses.Length > 0)
{
    failures.Add(
        $"M2: no violation is seeded for {string.Join(", ", unseededRuleClasses)} - recall measured over a " +
        "population that skips a constraint class restates the gap rather than closing it");
}

if (nearMissControls == 0)
{
    failures.Add(
        "M2: no near-miss control exists - an engine that flagged every chart would read 100% recall, so " +
        "recall without specificity is unfalsifiable");
}

// M5's population guards. A suite in which everything degrades cannot show the difference between
// degrading and working, which is the whole content of "transparent" degradation.
if (m5Cases == 0)
{
    failures.Add("M5: no fault-injection cases exist, so a degradation rate over them is not a measurement");
}
else if (m5FallbackCases == 0 || m5SynthesizedCases == 0)
{
    failures.Add(
        $"M5: the population is one-sided ({m5FallbackCases} fallback / {m5SynthesizedCases} synthesized) - " +
        "a suite in which every turn degrades cannot show degradation being the exception");
}

// The evidence slice's population guards. Landing `retrieval_hit` and `evidence_grounded` with no cases to
// grade would have shipped two rubrics that pass vacuously - the defect this slice was opened to remove,
// reproduced on the instrument. Each guard names a shape the slice would be missing without it.
if (evidenceCases == 0)
{
    failures.Add(
        "Evidence: no evidence-retrieval cases exist, so retrieval_hit and evidence_grounded report over an " +
        "empty population - a rubric that grades nothing passes vacuously");
}
else
{
    if (evidenceHitCases == 0 || outOfCorpusControls == 0)
    {
        failures.Add(
            $"Evidence: the population is one-sided ({evidenceHitCases} retrieval-hit / {outOfCorpusControls} " +
            "out-of-corpus) - a retriever that returned the whole corpus for every query would satisfy every " +
            "membership check, and only a query that must retrieve nothing rules that out");
    }

    if (stageFailureCases == 0)
    {
        failures.Add(
            "Evidence: no case fails a retrieval stage - the degradation path is where a silent failure " +
            "hides, and a suite of whole-pipeline runs never reaches it");
    }

    if (rerankOrderCases == 0)
    {
        failures.Add(
            "Evidence: no case pins a retrieved ordering - membership alone cannot tell the reranker from " +
            "the fused order it was meant to improve on");
    }

    if (citationSurvivalCases == 0 || citationSuppressionCases == 0)
    {
        failures.Add(
            $"Evidence: the grounding population is one-sided ({citationSurvivalCases} cited / " +
            $"{citationSuppressionCases} suppressed) - a critic that suppressed every guideline citation " +
            "would pass every fabrication case while delivering no evidence at all");
    }
}

var gatePassed = failures.Count == 0;
var m3 = new
{
    requirement = "M3 - 0 unauthorized disclosures across role/injection eval cases",
    role_injection_cases = authorizationCases,
    permit_cases = permitCases,
    deny_cases = denyCases,
    unauthorized_disclosures = unauthorizedDisclosures,
    attempts_logged = $"{loggedAttempts}/{authorizationCases}",
};

var m1 = new
{
    requirement = "M1 - 100% of asserted clinical facts carry a resolvable source (zero tolerance)",
    answer_cases = m1Cases,
    suppression_cases = m1SuppressionCases,
    shipped_intact_cases = m1ShippedIntactCases,
    ungrounded_claims_shipped = ungroundedClaimsShipped,
    known_keyword_boundary_escapes = m1KnownEscapes,
    known_scope_escapes = m1ScopeEscapes,
};

var m2 = new
{
    requirement = "M2 - 100% of seeded constraint violations raise a DomainConstraintFlag",
    seeded_violations = seededViolations,
    flagged_violations = flaggedViolations,
    recall = seededViolations == 0 ? 0d : (double)flaggedViolations / seededViolations,
    rule_classes_seeded = $"{seededRuleClasses.Count}/{CardiologyConstraintRules.Default.Count}",
    near_miss_controls = nearMissControls,
    control_flags_raised = controlFlagsRaised,
};

var m5 = new
{
    requirement = "M5 - 100% of fault-injection cases yield transparent partial/refusal, 0 silent fabrications",
    fault_injection_cases = m5Cases,
    fallback_cases = m5FallbackCases,
    synthesized_cases = m5SynthesizedCases,
    silent_or_fabricated_answers = silentOrFabricatedAnswers,
};

var evidence = new
{
    requirement = "Evidence retrieval - every expected guideline chunk is retrieved and ranked, every "
        + "guideline claim that ships resolves to one, 0 fabricated guideline citations",
    evidence_cases = evidenceCases,
    retrieval_hit_cases = evidenceHitCases,
    out_of_corpus_controls = outOfCorpusControls,
    stage_failure_cases = stageFailureCases,
    rerank_order_cases = rerankOrderCases,
    citation_survival_cases = citationSurvivalCases,
    citation_suppression_cases = citationSuppressionCases,
    missed_expected_chunks = missedExpectedChunks,
    ungrounded_guideline_claims = ungroundedGuidelineClaims,
};

var report = new
{
    generated_at = DateTimeOffset.UtcNow,
    total_cases = cases.Count,
    passed = gatePassed,
    category_rates = categoryRates,
    m1,
    m2,
    m3,
    m5,
    evidence,
    failures,
    failing_cases = failedCases.Select(c => new { id = c.Id, rubrics = c.Rubrics, guards = c.Guards, fault = c.Fault }),
    cases = caseReports,
};
await File.WriteAllTextAsync(resultsPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Eval gate: {cases.Count} cases across {perRubricTotal.Count} rubric categories");
foreach (var (rubric, rate) in categoryRates.OrderBy(k => k.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"  {rubric,-28} {rate,6:P0}  ({perRubricPass.GetValueOrDefault(rubric)}/{perRubricTotal[rubric]})");
}

Console.WriteLine(
    $"M1 groundedness: {ungroundedClaimsShipped} ungrounded claims reached the answer across {m1Cases} " +
    $"answer-path cases ({m1SuppressionCases} suppression / {m1ShippedIntactCases} shipped-intact); " +
    $"{m1KnownEscapes} pinned keyword-boundary escape(s), which are SourceAttributionEngine's documented " +
    "limit rather than part of the clean number; " +
    $"{m1ScopeEscapes} pinned NG1 scope escape(s) - a correctly cited treatment recommendation ships, because " +
    "'does not recommend treatment' is prompt-only");

Console.WriteLine(
    $"M2 constraint recall: {flaggedViolations}/{seededViolations} seeded constraint violations flagged " +
    $"({(seededViolations == 0 ? 0d : (double)flaggedViolations / seededViolations):P0}) across " +
    $"{seededRuleClasses.Count}/{CardiologyConstraintRules.Default.Count} rule classes; " +
    $"{nearMissControls} near-miss controls raised {controlFlagsRaised} flags");

Console.WriteLine(
    $"M3 authorization integrity: {unauthorizedDisclosures} unauthorized disclosures across " +
    $"{authorizationCases} role/injection cases ({permitCases} permit / {denyCases} deny); " +
    $"{loggedAttempts}/{authorizationCases} attempts logged");

Console.WriteLine(
    $"M5 transparent degradation: {m5Cases - silentOrFabricatedAnswers}/{m5Cases} fault-injection cases " +
    $"stated the gap or degraded visibly ({m5FallbackCases} fallback / {m5SynthesizedCases} synthesized); " +
    $"{silentOrFabricatedAnswers} silent or fabricated answers");

Console.WriteLine(
    $"Evidence retrieval: {missedExpectedChunks} missed expected chunks and {ungroundedGuidelineClaims} " +
    $"ungrounded guideline claims across {evidenceCases} evidence cases ({evidenceHitCases} retrieval-hit / " +
    $"{outOfCorpusControls} out-of-corpus); {stageFailureCases} stage-failure, {rerankOrderCases} " +
    $"order-pinned, {citationSurvivalCases} cited / {citationSuppressionCases} suppressed");

// The rubric rates above say which rubric slipped; they never say what the case that slipped was defending.
// Whoever reads this is a stranger to the change that broke it, so print the case's own `guards` line here -
// that is the whole point of the field. A separate change
if (failedCases.Count > 0)
{
    var to = gatePassed ? Console.Out : Console.Error;
    await to.WriteLineAsync($"{failedCases.Count} failing case(s) - what each one was guarding:");
    foreach (var (id, rubrics, guards, fault) in failedCases.OrderBy(c => c.Id, StringComparer.Ordinal))
    {
        await to.WriteLineAsync($"  - {id}  failed: {string.Join(", ", rubrics)}");
        if (fault is not null)
        {
            // Every rubric failed because the run threw, not because each was checked. A separate change
            await to.WriteLineAsync($"    threw: {fault}");
        }

        await to.WriteLineAsync($"    guards: {guards}");
    }
}

if (gatePassed)
{
    Console.WriteLine($"PASS - no rubric category below threshold or regressed. Results: {resultsPath}");
    return 0;
}

await Console.Error.WriteLineAsync("FAIL - the eval gate blocked the build:");
foreach (var failure in failures)
{
    await Console.Error.WriteLineAsync($"  - {failure}");
}

return 1;
