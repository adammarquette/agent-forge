using System.ComponentModel.DataAnnotations;

namespace AgentForge.Api.Chat;

/// <summary>
/// The per-session LLM budget (<c>SECURITY-PLATFORM.md</c>): how many LLM turns one session may bill within a
/// window. Chat turns (the brief and each follow-up), each <c>POST /evidence/ask</c> and each patient summary of a
/// <c>GET /agenda</c> draw on it. Each of those is already bounded - <c>AgentOptions.TurnDeadline</c>, the
/// orchestrator's tool-round limit and the model's output-token ceiling - so capping how many a session runs per
/// window bounds what one session can spend in that window.
/// </summary>
public sealed class ConversationBudgetOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "ConversationBudget";

    /// <summary>
    /// LLM turns one session may run per <see cref="Window"/>. Sized for a full clinic day with headroom: 25
    /// patients, the agenda reloaded after each visit (about 25 x 12.5 remaining summaries), a brief and four
    /// follow-ups each and two evidence asks each come to about 490.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxTurnsPerWindow { get; init; } = 800;

    /// <summary>
    /// How long a session's budget lasts, from its first charge; after it the session starts a fresh budget. A
    /// clinic day, because a re-launch in the same browser keeps the same session.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromHours(12);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Window <= TimeSpan.Zero)
        {
            yield return new ValidationResult($"{nameof(Window)} must be positive.", [nameof(Window)]);
        }
    }
}
