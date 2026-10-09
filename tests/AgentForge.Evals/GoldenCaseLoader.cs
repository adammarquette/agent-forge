using System.Text.Json;

namespace AgentForge.Evals;

/// <summary>
/// The one place a golden-case file is turned into a <see cref="GoldenCase"/>. The console gate and the
/// xUnit tier both call it, for the same reason both call <see cref="EvalCaseRunner"/>: two loaders drift,
/// and a rule enforced by only one of them is a rule a new case can be added around.
/// reference: evals/README.md
/// </summary>
internal static class GoldenCaseLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads and validates every <c>*.json</c> under <paramref name="goldenDir"/>, recursively,
    /// ordered by file path so two runs report in the same order.</summary>
    public static IReadOnlyList<GoldenCase> LoadAll(string goldenDir) =>
        [.. Directory.GetFiles(goldenDir, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(LoadFile)];

    /// <summary>Reads and validates one golden-case file.</summary>
    public static GoldenCase LoadFile(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>
    /// Parses one golden case. <paramref name="origin"/> is the file name the message should name — a
    /// rejection that says only which member was missing sends the reader to every case in the set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The case does not parse, it states no failure mode, or it declares no rubrics. <c>guards</c> is
    /// required because documentation that can be skipped is skipped: FR-EVAL-1 asks every case to document
    /// the failure mode it guards, and a field the loader tolerates the absence of is a field the next case
    /// omits. At least one rubric is required because a case that threw fails the rubrics it declares and
    /// nothing else, so with none a crashed run reads as a pass.
    /// </exception>
    public static GoldenCase Parse(string json, string origin)
    {
        GoldenCase? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GoldenCase>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Golden case '{origin}' could not be parsed: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            throw new InvalidOperationException($"Golden case '{origin}' parsed to null.");
        }

        if (string.IsNullOrWhiteSpace(parsed.Guards))
        {
            throw new InvalidOperationException(
                $"Golden case '{origin}' states a blank 'guards' - name the boundary, invariant or regression "
                + "risk this case defends, not what its id already says (REQUIREMENTS.md FR-EVAL-1).");
        }

        // A fault fails every declared rubric, so a case declaring none passes even when its run throws.
        // `null` gets here too: the serializer does not enforce the non-nullable annotation.
        if (parsed.Rubrics is null || parsed.Rubrics.Count == 0)
        {
            throw new InvalidOperationException(
                $"Golden case '{origin}' declares no 'rubrics' - a case checked by nothing cannot fail, so it "
                + "would pass even if its run threw.");
        }

        return parsed;
    }
}
