using FluentAssertions;
using AgentForge.Api.Contracts;

namespace AgentForge.UnitTests.Api.Contracts;

/// <summary>
/// The command line that selects tool-schema export (<c>--export-tool-schemas</c>). Guarded like the
/// graph-schema flag: a normal start is never mistaken for an export, and an export with no path refuses rather
/// than booting the host from a script. A separate change
/// </summary>
public sealed class McpToolSchemaExportTests
{
    [Fact]
    public void OutputPathFrom_CommandLineDoesNotAskForAnExport_ReturnsNull()
    {
        McpToolSchemaExport.OutputPathFrom(["--export-graph-schema", "out/graph.json"]).Should().BeNull();
    }

    [Fact]
    public void OutputPathFrom_FlagCarriesAPath_ReturnsThatPath()
    {
        McpToolSchemaExport.OutputPathFrom(["--export-tool-schemas", "out/tools.json"])
            .Should().Be("out/tools.json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutputPathFrom_FlagWithNoUsablePath_RefusesRatherThanStartingTheApp(bool blank)
    {
        string[] args = blank ? ["--export-tool-schemas", " "] : ["--export-tool-schemas"];

        var act = () => McpToolSchemaExport.OutputPathFrom(args);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task WriteAsync_ToAFreshDirectory_WritesTheCatalogRendering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gl625-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "tools.json");
        try
        {
            await McpToolSchemaExport.WriteAsync(path);

            (await File.ReadAllTextAsync(path)).Should().Be(AgentForge.Agent.McpToolCatalog.RenderSchemas());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
