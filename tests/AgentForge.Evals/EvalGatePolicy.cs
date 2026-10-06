using System.Text.Json;

namespace AgentForge.Evals;

/// <summary>
/// The two per-category controls <c>evals/baseline.json</c> declares, in one place so the console gate and
/// the xUnit tier score them identically and so the policy itself can be asserted rather than only run.
/// <para>
/// The controls are deliberately <b>two</b>, and the Week 2 brief names both: an <b>absolute floor</b>
/// (the category's tier in <c>pass_thresholds</c>) below which a category is unacceptable whatever it used
/// to score, and a <b>relative</b> one (<c>max_regression</c>) that refuses a category which has fallen
/// more than five points below the rate this repository last recorded for it. Until <c>a separate change</c> the
/// second was unreachable everywhere: it is evaluated only when the first passes, so it needs
/// <c>baseline &gt; pass_threshold + max_regression</c>, and with a single threshold of <c>1.0</c> and
/// every baseline at <c>1.0</c> that is <c>&gt; 1.05</c> — unsatisfiable for every rubric at every
/// magnitude.
/// </para>
/// <para>
/// <b>There are now two tiers, and they are not the same control.</b> <c>quality</c> sits at <c>0.80</c>,
/// the largest floor at which some achievable <c>k/n</c> lands in the regression window for every rubric in
/// that tier, so both arms are live there. <c>safety</c> sits at <c>1.0</c>, where a single failing case
/// must block — <b>its regression arm is unreachable, and that is the ruling rather than a defect</b>: when
/// any drop at all is refused, a rule about drops larger than five points has nothing left to say. Do not
/// "fix" it by lowering a safety floor. <c>evals/README.md</c> §<i>Gate policy</i> is the decision record;
/// <c>EvalGatePolicyTests</c> pins <b>three</b> routes back into the defect, not two: reachability for
/// every rubric outside the zero-tolerance tier — iterated over <c>pass_thresholds</c>, so a third
/// tier is governed the day it is added rather than falling outside both guards — the deliberate
/// emptiness of the safety tier's window, and the safety tier's <b>membership</b>, asserted as an
/// equality, because demoting a rubric out of it reopens the same hole by moving a name instead of a
/// number.
/// </para>
/// </summary>
internal static class EvalGatePolicy
{
    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads <c>baseline.json</c>. One entry point so the gate and the tests cannot parse it
    /// differently — a policy read two ways is two policies.</summary>
    /// <exception cref="InvalidOperationException">The file is not a readable policy. Always this type and
    /// never a raw <see cref="JsonException"/>: the three ways a hand-edited <c>categories</c> entry goes
    /// wrong — an undefined tier, a typo'd tier and an <b>absent</b> <c>tier</c> key — must all report as
    /// the gate's own verdict, and the third used to surface as a deserializer stack trace because
    /// <see cref="CategoryBaseline.Tier"/> is <c>required</c>. A separate change</exception>
    public static EvalBaseline Load(string baselinePath)
    {
        try
        {
            return JsonSerializer.Deserialize<EvalBaseline>(File.ReadAllText(baselinePath), BaselineJsonOptions)
                ?? throw new InvalidOperationException($"Failed to parse {baselinePath}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{baselinePath} is not a readable gate policy: {ex.Message} Each categories entry is " +
                "{\"baseline\": <rate>, \"tier\": <a name pass_thresholds defines>}, and both keys are " +
                "required - a category with no tier has no absolute floor, and defaulting one is the " +
                "decision this file exists to force.", ex);
        }
    }

    /// <summary>
    /// Scores one category against the policy, returning the gate failure line or <c>null</c> when the
    /// category is acceptable. The order is load-bearing and is mirrored by
    /// the eval snapshot panel's status ladder: a rate under the absolute floor is
    /// reported as <i>below threshold</i> even when it is also a regression, so the dashboard row and the
    /// gate's own line never disagree about why a category is red.
    /// </summary>
    public static string? Evaluate(string rubric, double rate, EvalBaseline baseline)
    {
        // A rubric with no recorded baseline used to fall back to the single pass_threshold, which was
        // harmless only while that was 1.0. Under a two-tier policy the same fallback would hand an
        // unrecorded rubric whichever floor the code happened to pick - a new rubric arriving already
        // exempt from the decision this file exists to force. So it is a gate failure now, in the same
        // shape as the population guards in Program.cs: an unmeasured category is not a clean one.
        if (!baseline.Categories.TryGetValue(rubric, out var category))
        {
            return $"{rubric}: no baseline entry in baseline.json - a category with no recorded baseline " +
                "has no regression to measure and no tier saying how strictly it is floored";
        }

        // The tier is resolved rather than defaulted, for the reason the ruling gives: a SAFETY rubric
        // added later must not land in the quality tier and inherit a 95% floor by omission or by typo.
        // There is no "unknown tier" behaviour to get wrong, because there is no default.
        if (!baseline.PassThresholds.TryGetValue(category.Tier, out var passThreshold))
        {
            var known = string.Join(", ", baseline.PassThresholds.Keys.OrderBy(t => t, StringComparer.Ordinal));
            return $"{rubric}: declares tier '{category.Tier}', which baseline.json's pass_thresholds does " +
                $"not define (known: {known}) - a category whose floor cannot be resolved has no absolute " +
                "control at all";
        }

        if (rate < passThreshold)
        {
            return $"{rubric}: {rate:P0} is below the {passThreshold:P0} pass threshold for the {category.Tier} tier";
        }

        if (rate < category.Baseline - baseline.MaxRegression)
        {
            return $"{rubric}: {rate:P0} regressed more than {baseline.MaxRegression:P0} from baseline {category.Baseline:P0}";
        }

        return null;
    }

    /// <summary>The <see cref="EvalBaseline.PinnedEscapes"/> key for <c>SourceAttributionEngine</c>'s
    /// keyword-boundary limit. A separate change</summary>
    public const string KeywordBoundaryEscape = "keyword_boundary";

    /// <summary>The <see cref="EvalBaseline.PinnedEscapes"/> key for NG1's prompt-only "does not recommend
    /// treatment". A separate change</summary>
    public const string ScopeEscape = "scope";

    /// <summary>
    /// Compares the pinned-escape counts a run observed with the ones <c>baseline.json</c> records, over the
    /// union of both key sets, and returns one gate failure line per kind that differs. A pinned escape is
    /// printed beside its metric rather than inside it; if its case disappeared the line would print zero,
    /// which reads as the limit having closed. A separate change
    /// </summary>
    public static IReadOnlyList<string> PinnedEscapeDrift(
        EvalBaseline baseline, IReadOnlyDictionary<string, int> observed)
    {
        return [.. baseline.PinnedEscapes.Keys
            .Union(observed.Keys, StringComparer.Ordinal)
            .OrderBy(kind => kind, StringComparer.Ordinal)
            .Select(kind => (Kind: kind, Seen: observed.GetValueOrDefault(kind), Recorded: baseline.PinnedEscapes.GetValueOrDefault(kind)))
            .Where(x => x.Seen != x.Recorded)
            .Select(x =>
                $"M1: {x.Seen} pinned {x.Kind} escape(s) observed but baseline.json records {x.Recorded} - a " +
                "documented limit is restated by editing pinned_escapes, never by a case appearing or " +
                "disappearing unnoticed")];
    }

    /// <summary>
    /// The other direction of the same correspondence: a baseline entry no case scores. The gate iterates
    /// the rubrics the <i>run</i> reported, so deleting every case that declares a rubric removes it from
    /// the loop silently while <c>baseline.json</c> goes on claiming it at 100%. Returns the orphaned
    /// category names, ordinal-ordered.
    /// </summary>
    public static IReadOnlyList<string> OrphanedBaselineCategories(
        EvalBaseline baseline, IEnumerable<string> reportedRubrics)
    {
        var reported = new HashSet<string>(reportedRubrics, StringComparer.Ordinal);
        return [.. baseline.Categories.Keys.Where(c => !reported.Contains(c)).OrderBy(c => c, StringComparer.Ordinal)];
    }
}
