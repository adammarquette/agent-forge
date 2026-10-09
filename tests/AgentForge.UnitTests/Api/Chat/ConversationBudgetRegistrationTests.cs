using AgentForge.Api.Chat;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Chat;

/// <summary>
/// Pins the one registration both <c>Program.cs</c> and the hand-built <c>ChatHubHost</c> use for the turn
/// budget, so a test host cannot wire it differently from the host that ships. Named failure mode
/// (regression): a hand-built host missing the budget let every hub connection close before its first invoke,
/// seen only after merge.
/// </summary>
public sealed class ConversationBudgetRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        return new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddConversationTurnBudget(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void AddConversationTurnBudget_SectionConfigured_TheBudgetEnforcesTheConfiguredCap()
    {
        using var provider = Build(("ConversationBudget:MaxTurnsPerWindow", "2"));

        var budget = provider.GetRequiredService<IConversationTurnBudget>();

        budget.TryConsume("session-1").Should().BeTrue();
        budget.TryConsume("session-1").Should().BeTrue();
        budget.TryConsume("session-1").Should().BeFalse("the section was bound, so the third turn is over the cap");
    }

    [Fact]
    public void AddConversationTurnBudget_SectionAbsent_UsesTheBuiltInDefaults()
    {
        using var provider = Build();

        provider.GetRequiredService<IOptions<ConversationBudgetOptions>>().Value.MaxTurnsPerWindow
            .Should().Be(new ConversationBudgetOptions().MaxTurnsPerWindow);
    }

    [Fact]
    public void AddConversationTurnBudget_CapNotPositive_FailsTheStartupValidator()
    {
        // ValidateOnStart registers the check with IStartupValidator; the host runs it before serving.
        using var provider = Build(("ConversationBudget:MaxTurnsPerWindow", "0"));

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Where(e => e.Message.Contains(nameof(ConversationBudgetOptions.MaxTurnsPerWindow), StringComparison.Ordinal));
    }

    [Fact]
    public void AddConversationTurnBudget_WindowNotPositive_FailsTheStartupValidator()
    {
        using var provider = Build(("ConversationBudget:Window", "00:00:00"));

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>()
            .Where(e => e.Message.Contains(nameof(ConversationBudgetOptions.Window), StringComparison.Ordinal));
    }
}
