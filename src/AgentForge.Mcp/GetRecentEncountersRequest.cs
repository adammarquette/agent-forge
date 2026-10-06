using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AgentForge.Mcp;

/// <summary>
/// Input for the <c>get_recent_encounters</c> tool (ARCHITECTURE.md §8.1) - a thin list (date,
/// type, reason); the agent requests detail explicitly rather than pulling everything upfront.
/// </summary>
public sealed record GetRecentEncountersRequest
{
    /// <summary>OpenEMR multi-site segment.</summary>
    [SessionBound]
    [Required(AllowEmptyStrings = false)]
    public required string Site { get; init; }

    /// <summary>The patient this query is scoped to (minimum-necessary - never whole-chart).</summary>
    [SessionBound]
    [Required(AllowEmptyStrings = false)]
    public required string PatientId { get; init; }

    /// <summary>How many of the most recent encounters to return. Bounded to prevent an unbounded pull.</summary>
    /// <remarks>The [Description] is prompt surface - it reaches the model verbatim (PROMPTS.md §4).</remarks>
    [Description("How many of the most recent encounters to return. Defaults to 3.")]
    [Range(1, 20)]
    public int Count { get; init; } = 3;
}
