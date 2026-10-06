using System.Text;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgentForge.Api.OpenApi;

/// <summary>
/// Renders the live OpenAPI document to a file and exits, so the committed spec is produced by a repeatable
/// command rather than maintained by hand:
/// <code>AgentForge.Api --export-openapi &lt;path&gt;</code> (INTERFACES.md §D.1 lists the configuration it needs)
/// It runs after the endpoints are mapped and the host has started, so it describes exactly the routes this
/// build serves — including the Week 2 routes, which are only mapped when the data tier is configured.
/// </summary>
public static class OpenApiSpecExport
{
    /// <summary>The command-line flag that selects export mode.</summary>
    public const string Flag = "--export-openapi";

    /// <summary>
    /// The output path requested on the command line, or <c>null</c> when the app was not asked to export.
    /// </summary>
    /// <param name="args">The process command line.</param>
    /// <returns>The path to write the document to, or <c>null</c> to run normally.</returns>
    /// <exception cref="ArgumentException">The flag was given without a path.</exception>
    public static string? OutputPathFrom(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var flagIndex = Array.IndexOf(args, Flag);
        if (flagIndex < 0)
        {
            return null;
        }

        if (flagIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[flagIndex + 1]))
        {
            throw new ArgumentException($"{Flag} requires an output file path.", nameof(args));
        }

        return args[flagIndex + 1];
    }

    /// <summary>
    /// Writes the generated document to <paramref name="outputPath"/> as OpenAPI 3.0 JSON.
    /// </summary>
    /// <param name="services">The started application's services.</param>
    /// <param name="outputPath">Where to write the document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAsync(
        IServiceProvider services, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(AgentForgeOpenApi.DocumentName);
        var document = await provider.GetOpenApiDocumentAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var buffer = new MemoryStream();
        await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_0, cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(outputPath, Normalize(Encoding.UTF8.GetString(buffer.ToArray())), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the rendered document byte-identical across platforms. Two different newline problems: the
    /// file's own endings, and the ones <i>inside</i> the JSON strings — summaries and descriptions are doc
    /// comments joined with <see cref="Environment.NewLine"/>, so without this the same command would
    /// produce a different file on Windows and on Linux and the committed spec would never settle.
    /// </summary>
    /// <param name="json">The serialized document.</param>
    /// <returns>The document with LF endings, LF-escaped strings, and a single trailing newline.</returns>
    internal static string Normalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return RewriteEscapedCarriageReturns(json.ReplaceLineEndings("\n")).TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Rewrites the <c>\r</c> and <c>\r\n</c> <i>escapes</i> inside the document's JSON strings as a single
    /// <c>\n</c>, counting the backslashes in front of each one so a literal backslash is never mistaken for
    /// the start of an escape. A plain string replace cannot tell the two apart: a doc comment naming a
    /// Windows path such as <c>C:\repo</c> is serialized as <c>C:\\repo</c>, whose second backslash and
    /// following <c>r</c> look exactly like a carriage-return escape, and rewriting them would publish
    /// <c>C:\nepo</c> in the contract. Only an odd-length run of backslashes leaves one unconsumed to open an
    /// escape. A separate change review
    /// </summary>
    /// <param name="json">The serialized document, with the file's own endings already normalized.</param>
    /// <returns>The document with every carriage-return escape rewritten as a line-feed escape.</returns>
    private static string RewriteEscapedCarriageReturns(string json)
    {
        var rewritten = new StringBuilder(json.Length);
        var backslashes = 0;

        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (character == 'r' && backslashes % 2 == 1)
            {
                rewritten.Append('n');
                backslashes = 0;

                // \r\n is one line break, not two: consume the line-feed escape that follows rather than
                // emitting a second one.
                if (index + 2 < json.Length && json[index + 1] == '\\' && json[index + 2] == 'n')
                {
                    index += 2;
                }

                continue;
            }

            backslashes = character == '\\' ? backslashes + 1 : 0;
            rewritten.Append(character);
        }

        return rewritten.ToString();
    }
}
