using System.Globalization;
using System.Text.Json;

namespace AgentForge.Observability;

/// <summary>
/// One eval-gate run (<c>evals/results.json</c>) read against the policy it was judged by
/// (<c>evals/baseline.json</c>): the per-category pass rates and baselines
/// <c>AgentForgeEvalCategoryRegression</c> compares. The category set is the baseline file's, so the
/// <c>category</c> label is bounded by a repository-owned list and never by what a run happens to contain.
/// </summary>
/// <param name="GeneratedAt">When the run was made - the results file's own <c>generated_at</c>.</param>
/// <param name="Passed">The gate's own verdict on that run - the results file's <c>passed</c>.</param>
/// <param name="CategoryPassRates">Pass rate per category the run evaluated and the policy names.</param>
/// <param name="BaselinePassRates">Baseline pass rate per category the policy names.</param>
public sealed record EvalResultsSnapshot(
    DateTimeOffset GeneratedAt,
    bool Passed,
    IReadOnlyDictionary<string, double> CategoryPassRates,
    IReadOnlyDictionary<string, double> BaselinePassRates)
{
    /// <summary>
    /// Parses a results document and a baseline document. Throws <see cref="FormatException"/> when either
    /// is not the shape the eval gate writes - a missing <c>generated_at</c>, boolean <c>passed</c> or <c>category_rates</c>, a
    /// baseline entry that is not an object carrying a numeric <c>baseline</c>, or any rate outside [0, 1].
    /// Result categories the baseline does not name are dropped.
    /// </summary>
    public static EvalResultsSnapshot Parse(string resultsJson, string baselineJson)
    {
        var baselines = ParseBaselines(baselineJson);

        using var results = ParseDocument(resultsJson, "results");
        var root = results.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("generated_at", out var generatedAtElement)
            || generatedAtElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                generatedAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var generatedAt))
        {
            throw new FormatException("eval results carry no parseable `generated_at`");
        }

        if (!root.TryGetProperty("passed", out var passedElement)
            || passedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new FormatException("eval results carry no boolean `passed`");
        }

        if (!root.TryGetProperty("category_rates", out var ratesElement) || ratesElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("eval results carry no `category_rates` object");
        }

        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var rate in ratesElement.EnumerateObject())
        {
            var value = ReadRate(rate.Value, $"category_rates.{rate.Name}");
            if (baselines.ContainsKey(rate.Name))
            {
                rates[rate.Name] = value;
            }
        }

        return new EvalResultsSnapshot(generatedAt, passedElement.GetBoolean(), rates, baselines);
    }

    private static Dictionary<string, double> ParseBaselines(string baselineJson)
    {
        using var document = ParseDocument(baselineJson, "baseline");
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("categories", out var categories)
            || categories.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("eval baseline carries no `categories` object");
        }

        var baselines = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var category in categories.EnumerateObject())
        {
            // Objects; a bare number is the old shape and must not read as "no baseline".
            if (category.Value.ValueKind != JsonValueKind.Object || !category.Value.TryGetProperty("baseline", out var baseline))
            {
                throw new FormatException($"eval baseline `categories.{category.Name}` is not an object carrying `baseline`");
            }

            baselines[category.Name] = ReadRate(baseline, $"categories.{category.Name}.baseline");
        }

        return baselines;
    }

    private static JsonDocument ParseDocument(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"eval {what} is not JSON: {ex.Message}", ex);
        }
    }

    private static double ReadRate(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || value is < 0 or > 1)
        {
            throw new FormatException($"eval `{path}` is not a rate in [0, 1]");
        }

        return value;
    }
}
