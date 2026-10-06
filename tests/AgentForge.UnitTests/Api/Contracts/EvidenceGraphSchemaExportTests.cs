using FluentAssertions;
using AgentForge.Api.Contracts;

namespace AgentForge.UnitTests.Api.Contracts;

/// <summary>
/// The command line that selects graph-schema export (<c>--export-graph-schema</c>). Guarded: a
/// normal start is never mistaken for an export, and an export with no path refuses rather than starting the
/// app - which would otherwise boot the whole host and serve traffic from a script. A separate change
/// </summary>
public sealed class EvidenceGraphSchemaExportTests
{
    [Fact]
    public void OutputPathFrom_CommandLineDoesNotAskForAnExport_ReturnsNull()
    {
        EvidenceGraphSchemaExport.OutputPathFrom(["--urls", "http://localhost:5000"]).Should().BeNull();
    }

    [Fact]
    public void OutputPathFrom_FlagCarriesAPath_ReturnsThatPath()
    {
        EvidenceGraphSchemaExport.OutputPathFrom(["--export-graph-schema", "out/graph.json"])
            .Should().Be("out/graph.json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputPathFrom_FlagWithNoUsablePath_RefusesRatherThanStartingTheApp(bool blank)
    {
        string[] args = blank ? ["--export-graph-schema", " "] : ["--export-graph-schema"];

        var act = () => EvidenceGraphSchemaExport.OutputPathFrom(args);

        act.Should().Throw<ArgumentException>();
    }
}
