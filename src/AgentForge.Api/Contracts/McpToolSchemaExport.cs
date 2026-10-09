using AgentForge.Agent;

namespace AgentForge.Api.Contracts;

/// <summary>
/// Writes the MCP tool schemas advertised to the model to a file and exits, so the tool schemas are produced by a
/// repeatable command rather than maintained by hand: <code>AgentForge.Api --export-tool-schemas &lt;path&gt;</code>
/// Like the graph-schema export it needs no host and no configuration, so it runs before the builder is
/// created.
/// </summary>
public static class McpToolSchemaExport
{
    /// <summary>The command-line flag that selects tool-schema export.</summary>
    public const string Flag = "--export-tool-schemas";

    /// <summary>The output path requested on the command line, or <c>null</c> when no export was asked for.</summary>
    /// <param name="args">The process command line.</param>
    /// <returns>The path to write the schemas to, or <c>null</c> to run normally.</returns>
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

    /// <summary>Writes <see cref="McpToolCatalog.RenderSchemas"/> to <paramref name="outputPath"/>.</summary>
    /// <param name="outputPath">Where to write the schemas.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, McpToolCatalog.RenderSchemas(), cancellationToken)
            .ConfigureAwait(false);
    }
}
