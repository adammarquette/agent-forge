using AgentForge.Api.OpenApi;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.OpenApi;

/// <summary>
/// The committed OpenAPI document is only worth committing if the command that writes it produces the same
/// bytes wherever it runs. <see cref="OpenApiSpecExport.Normalize"/> is what makes that true, and the case
/// that bites is invisible: descriptions come from doc comments joined with <see cref="Environment.NewLine"/>,
/// so a Windows run and a Linux run of the same command would otherwise disagree on every multi-line
/// description in the file and the spec would never settle.
/// </summary>
public sealed class OpenApiSpecExportTests
{
    [Fact]
    public void Normalize_DescriptionCarriesWindowsNewlinesInsideTheJsonString_RewritesThemAsLineFeeds()
    {
        // The escaped \r\n *inside* a JSON string - not the file's own line endings, which the next test covers.
        var document = "{\"description\":\"first\\r\\nsecond\"}";

        var normalized = OpenApiSpecExport.Normalize(document);

        normalized.Should().Be("{\"description\":\"first\\nsecond\"}\n");
    }

    [Fact]
    public void Normalize_DescriptionCarriesALoneCarriageReturn_RewritesItAsALineFeed()
    {
        var document = "{\"description\":\"first\\rsecond\"}";

        var normalized = OpenApiSpecExport.Normalize(document);

        normalized.Should().Be("{\"description\":\"first\\nsecond\"}\n");
    }

    [Fact]
    public void Normalize_DescriptionCarriesAnEscapedBackslashBeforeAnR_LeavesThePathAlone()
    {
        // A doc comment naming a Windows path - "C:\repo" - reaches the serializer as the escaped
        // C:\\repo, whose last two characters before "epo" read as a \r escape to a blind search. Rewriting
        // them would silently publish C:\nepo, a corruption of the contract nothing downstream could
        // detect. Only an *odd* run of backslashes starts an escape. A separate change review
        var document = "{\"description\":\"C:\\\\repo\"}";

        var normalized = OpenApiSpecExport.Normalize(document);

        normalized.Should().Be("{\"description\":\"C:\\\\repo\"}\n");
    }

    [Fact]
    public void Normalize_DocumentWrittenWithWindowsLineEndings_RewritesTheFileToLineFeeds()
    {
        var document = "{\r\n  \"openapi\": \"3.0.4\"\r\n}";

        var normalized = OpenApiSpecExport.Normalize(document);

        normalized.Should().Be("{\n  \"openapi\": \"3.0.4\"\n}\n");
    }

    [Fact]
    public void Normalize_DocumentAlreadyEndsInNewlines_LeavesExactlyOne()
    {
        OpenApiSpecExport.Normalize("{}\n\n\n").Should().Be("{}\n");
    }

    [Fact]
    public void OutputPathFrom_CommandLineDoesNotAskForAnExport_ReturnsNull()
    {
        OpenApiSpecExport.OutputPathFrom(["--urls", "http://localhost:5000"]).Should().BeNull();
    }

    [Fact]
    public void OutputPathFrom_FlagCarriesAPath_ReturnsThatPath()
    {
        OpenApiSpecExport.OutputPathFrom([OpenApiSpecExport.Flag, "contracts/openapi/agentforge-v1.json"])
            .Should().Be("contracts/openapi/agentforge-v1.json");
    }

    [Fact]
    public void OutputPathFrom_FlagWithNothingAfterIt_RefusesRatherThanStartingTheApp()
    {
        // The alternative is an app that boots and serves when the operator asked it to export.
        var act = () => OpenApiSpecExport.OutputPathFrom([OpenApiSpecExport.Flag]);

        act.Should().Throw<ArgumentException>();
    }
}
