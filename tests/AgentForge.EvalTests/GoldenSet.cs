using AgentForge.Evals;

namespace AgentForge.EvalTests;

/// <summary>
/// Locates the committed golden set, loads each case through <see cref="GoldenCaseLoader"/> and runs it
/// through <see cref="EvalCaseRunner"/> — the same two entry points the console gate's Program uses, so the
/// xUnit tests and the <c>evals</c> job cannot drift into loading or running a case differently.
/// reference: evals/README.md
/// </summary>
internal static class GoldenSet
{
    /// <summary>The repo's <c>evals/golden</c> directory, resolved by walking up from the test assembly
    /// location — robust to the working directory the runner uses.</summary>
    public static string GoldenDir { get; } = ResolveGoldenDir();

    /// <summary>The committed <c>evals/baseline.json</c> beside that directory — the gate policy itself, so
    /// <see cref="EvalGatePolicyTests"/> asserts the file the <c>evals</c> job reads rather than a copy of
    /// it. A separate change</summary>
    public static string BaselinePath { get; } =
        Path.Combine(Path.GetDirectoryName(GoldenDir)!, "baseline.json");

    /// <summary>Golden-case file names (not full paths), ordered — used as the theory's data source.</summary>
    public static IReadOnlyList<string> CaseFileNames() =>
        [.. Directory.EnumerateFiles(GoldenDir, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)];

    public static GoldenCase Load(string fileName) =>
        GoldenCaseLoader.LoadFile(Path.Combine(GoldenDir, fileName));

    /// <summary>Runs one case with its pinned inputs (no live API, no network, no clock dependency).</summary>
    public static Task<CaseOutcome> RunAsync(GoldenCase testCase) => EvalCaseRunner.RunAsync(testCase);

    private static string ResolveGoldenDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "evals", "golden");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate evals/golden by walking up from the test assembly directory.");
    }
}
