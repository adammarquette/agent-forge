using Microsoft.Extensions.Configuration;

namespace AgentForge.Api.Chat;

/// <summary>Registers the per-session LLM turn budget (<see cref="ConversationBudgetOptions"/>).</summary>
public static class ConversationBudgetRegistration
{
    /// <summary>
    /// Binds <see cref="ConversationBudgetOptions"/> from its optional section, validated on start because a
    /// non-positive cap or window would refuse every turn, and adds the single-instance
    /// <see cref="IConversationTurnBudget"/>. Requires a <see cref="TimeProvider"/>. The one registration both the
    /// shipping host and the hand-built test hosts use, so the two cannot drift.
    /// </summary>
    public static IServiceCollection AddConversationTurnBudget(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConversationBudgetOptions>()
            .Bind(configuration.GetSection(ConversationBudgetOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IConversationTurnBudget, InMemoryConversationTurnBudget>();
        return services;
    }
}
