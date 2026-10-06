using AgentForge.Api.Observability;
using AgentForge.UnitTests.TestSupport;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// Where the per-category eval series comes from: the run the image build made against its own source
/// (<c>Dockerfile</c>, stage <c>evals</c>). The sidecar must boot without it - the series is optional
/// telemetry - but it must never be silently absent: a missing or unreadable run is logged by name.
/// </summary>
public sealed class EvalResultsSnapshotLoaderTests
{
    // Fully qualified on either OS, as IHostEnvironment.ContentRootPath always is.
    private static readonly string ContentRoot = Path.GetFullPath("/app");
    private const string Baseline = """{ "categories": { "no_phi_in_logs": { "baseline": 1.0, "tier": "safety" } } }""";
    private const string Results = """{ "generated_at": "2026-09-23T00:00:00Z", "passed": true, "category_rates": { "no_phi_in_logs": 0.9 } }""";

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<string> _reads = [];
    private readonly CapturingLogger<EvalResultsSnapshotLoader> _logger = new();

    private EvalResultsSnapshotLoader CreateSut() => new(
        path =>
        {
            _reads.Add(path);
            return _files.TryGetValue(path, out var content) ? content : null;
        },
        _logger);

    [Fact]
    public void Load_BothFilesPresent_ResolvesRelativePathsAgainstTheContentRoot()
    {
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/results.json"))] = Results;
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/baseline.json"))] = Baseline;

        var snapshot = CreateSut().Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().NotBeNull();
        snapshot!.CategoryPassRates["no_phi_in_logs"].Should().Be(0.9);
        _logger.Lines.Should().Contain(l => l.Contains("1 rubric categories", StringComparison.Ordinal)
            && l.Contains("2026-09-23", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_AbsolutePaths_AreReadAsGiven()
    {
        var results = Path.GetFullPath("/elsewhere/results.json");
        var baseline = Path.GetFullPath("/elsewhere/baseline.json");
        _files[results] = Results;
        _files[baseline] = Baseline;

        var snapshot = CreateSut().Load(new EvalResultsOptions { ResultsPath = results, BaselinePath = baseline }, ContentRoot);

        snapshot.Should().NotBeNull();
        _reads.Should().Equal(results, baseline);
    }

    [Fact]
    public void Load_ResultsFileMissing_ReturnsNullAndWarnsNamingThePath()
    {
        // A host-run sidecar before anyone ran the gate, or an image built by hand without the evals stage:
        // no series, which leaves the regression alert unable to fire - so it is said, not implied.
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/baseline.json"))] = Baseline;

        var snapshot = CreateSut().Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().BeNull();
        _logger.Lines.Should().ContainSingle(l => l.StartsWith("No eval run at", StringComparison.Ordinal)
            && l.Contains("results.json", StringComparison.Ordinal)
            && l.Contains("AgentForgeEvalCategoryRegression", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_BaselineFileMissing_ReturnsNullAndWarns()
    {
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/results.json"))] = Results;

        var snapshot = CreateSut().Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().BeNull();
        // Named as MISSING, not as unreadable: the operator's fix differs (ship the file, not repair it).
        _logger.Lines.Should().ContainSingle(l => l.StartsWith("No eval run at", StringComparison.Ordinal)
            && l.Contains("baseline.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_MalformedRun_ReturnsNullAndLogsTheReason()
    {
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/results.json"))] = """{ "category_rates": {} }""";
        _files[Path.GetFullPath(Path.Combine(ContentRoot, "evals/baseline.json"))] = Baseline;

        var snapshot = CreateSut().Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().BeNull();
        _logger.Lines.Should().ContainSingle(l => l.Contains("generated_at", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_ResultsPathBlank_ReadsNothingAndReturnsNull(string resultsPath)
    {
        var snapshot = CreateSut().Load(new EvalResultsOptions { ResultsPath = resultsPath }, ContentRoot);

        snapshot.Should().BeNull();
        _reads.Should().BeEmpty();
        _logger.Lines.Should().ContainSingle(l => l.Contains("disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_ReadThrowsIOException_ReturnsNullAndLogsItUnreadable()
    {
        // A file the process cannot read must not stop the sidecar booting: the loader runs inside the
        // eager resolve in Program.cs, so an escaping exception is a failed start. A separate change N2
        var sut = new EvalResultsSnapshotLoader(_ => throw new IOException("sharing violation"), _logger);

        var snapshot = sut.Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().BeNull();
        _logger.Lines.Should().ContainSingle(l => l.Contains("unreadable", StringComparison.Ordinal)
            && l.Contains("sharing violation", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_ReadThrowsUnauthorizedAccess_ReturnsNullAndLogsItUnreadable()
    {
        var sut = new EvalResultsSnapshotLoader(_ => throw new UnauthorizedAccessException("access denied"), _logger);

        var snapshot = sut.Load(new EvalResultsOptions(), ContentRoot);

        snapshot.Should().BeNull();
        _logger.Lines.Should().ContainSingle(l => l.Contains("unreadable", StringComparison.Ordinal)
            && l.Contains("access denied", StringComparison.Ordinal));
    }
}
