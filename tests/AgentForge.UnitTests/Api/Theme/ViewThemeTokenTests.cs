using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Theme;

/// <summary>
/// Pins the Daily Agenda and Copilot chat views to the shared theme tokens mirrored from the OpenEMR fork's new
/// frontend (<c>wwwroot/theme.css</c>): both views load the token sheet and its selector script, neither view
/// hard-codes a colour, the explicit dark block cannot drift from the device-dark block, and every text pair the
/// views draw clears WCAG AA 4.5:1 in both themes. Reads the shipped static files, as nothing else executes them.
/// </summary>
public sealed class ViewThemeTokenTests
{
    private const string WwwRoot = "src/AgentForge.Api/wwwroot";

    private static readonly (string Foreground, string Background)[] TextPairs =
    [
        ("--oe-text", "--oe-page"), ("--oe-text", "--oe-surface"), ("--oe-text", "--oe-surface-alt"),
        ("--oe-muted", "--oe-page"), ("--oe-muted", "--oe-surface"), ("--oe-muted", "--oe-surface-alt"),
        ("--oe-link", "--oe-page"), ("--oe-link", "--oe-surface"), ("--oe-link", "--oe-surface-alt"),
        ("--oe-on-primary", "--oe-primary"), ("--oe-on-warning", "--oe-warning"), ("--oe-on-danger", "--oe-danger"),
    ];

    [Theory]
    [InlineData("index.html")]
    [InlineData("agenda.html")]
    public void View_AsShipped_LoadsTheSharedThemeSheetAndSelectorBeforeItsOwnStyles(string view)
    {
        var html = ReadWwwRoot(view);

        var sheet = html.IndexOf("<link rel=\"stylesheet\" href=\"theme.css\"", StringComparison.Ordinal);
        var script = html.IndexOf("<script src=\"theme.js\"></script>", StringComparison.Ordinal);
        var style = html.IndexOf("<style>", StringComparison.Ordinal);

        sheet.Should().BeGreaterThan(-1, "{0} takes its colours from the shared token sheet", view);
        script.Should().BeGreaterThan(-1, "{0} must pick the host theme before first paint", view);
        sheet.Should().BeLessThan(style, "the view's own rules read tokens the sheet defines");
        script.Should().BeLessThan(style);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("agenda.html")]
    public void View_OwnStyles_HardCodeNoColour(string view)
    {
        var html = ReadWwwRoot(view);
        var style = Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        // Comments cite issues as "#N", which is not a colour.
        style = Regex.Replace(style, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        // The overlay scrim is theme-neutral black, as MUI's own backdrop is; nothing else may be a literal.
        var literals = Regex.Matches(style, @"#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)")
            .Select(m => m.Value)
            .Where(v => v != "rgba(0,0,0,.5)");

        literals.Should().BeEmpty("{0} must draw every colour from theme.css so both themes stay in step", view);
    }

    [Fact]
    public void ThemeSheet_ExplicitDarkChoice_UsesExactlyTheDeviceDarkValues()
    {
        var css = ReadWwwRoot("theme.css");

        Tokens(DeviceDarkBlock(css)).Should().Equal(Tokens(ExplicitDarkBlock(css)));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void ThemeSheet_EveryTextPair_MeetsWcagAa(string theme)
    {
        var css = ReadWwwRoot("theme.css");
        var tokens = Tokens(theme == "light" ? LightBlock(css) : ExplicitDarkBlock(css));

        var failures = TextPairs
            .Select(p => (p.Foreground, p.Background, Ratio: ContrastRatio(tokens[p.Foreground], tokens[p.Background])))
            .Where(p => p.Ratio < 4.5)
            .Select(p => $"{p.Foreground} on {p.Background}: {p.Ratio.ToString("0.00", CultureInfo.InvariantCulture)}");

        failures.Should().BeEmpty("text in the {0} theme must reach WCAG AA 4.5:1 (NFR-A11Y)", theme);
    }

    [Theory]
    [InlineData("--user-bg", "--oe-primary", "--user-text", "--oe-on-primary")]
    [InlineData("--accent", "--oe-primary", "--on-accent", "--oe-on-primary")]
    [InlineData("--warn-bg", "--oe-warning", "--warn-text", "--oe-on-warning")]
    [InlineData("--flag-bg", "--oe-warning", "--flag-text", "--oe-on-warning")]
    [InlineData("--fail-bg", "--oe-danger", "--fail-text", "--oe-on-danger")]
    public void ThemeSheet_FilledSurfaceAlias_KeepsItsCheckedTextPartner(
        string backgroundAlias, string backgroundToken, string textAlias, string textToken)
    {
        var aliases = Tokens(AliasBlock(ReadWwwRoot("theme.css")));

        aliases[backgroundAlias].Should().Be($"var({backgroundToken})");
        aliases[textAlias].Should().Be($"var({textToken})", "the pair is only AA-checked as {0} on {1}", textToken, backgroundToken);
    }

    private static string LightBlock(string css) => Block(css, @"^:root \{\s*\n\s*color-scheme: light;");

    private static string DeviceDarkBlock(string css) => Block(css, @"^\s+:root:not\(\[data-theme=""light""\]\) \{");

    private static string ExplicitDarkBlock(string css) => Block(css, @"^:root\[data-theme=""dark""\] \{");

    private static string AliasBlock(string css) => Block(css, @"^:root \{\s*\n\s*--bg:");

    private static string Block(string css, string opener)
    {
        var start = Regex.Match(css, opener, RegexOptions.Multiline);
        start.Success.Should().BeTrue("theme.css must contain a block opening with {0}", opener);
        var end = css.IndexOf('}', start.Index);
        return css[start.Index..end];
    }

    private static Dictionary<string, string> Tokens(string block) =>
        Regex.Matches(block, @"(--[a-z-]+):\s*([^;]+);")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim(), StringComparer.Ordinal);

    private static double ContrastRatio(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    private static double Luminance(string hex)
    {
        hex.Should().MatchRegex("^#[0-9a-fA-F]{6}$", "contrast is only computed on a six-digit token");
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(1 + offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }

    private static string ReadWwwRoot(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentForge.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root is found by walking up to AgentForge.slnx");
        var path = Path.Combine(directory!.FullName, WwwRoot, file);
        File.Exists(path).Should().BeTrue("{0} is the shipped file this test guards", path);
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
