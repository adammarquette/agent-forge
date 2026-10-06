using AgentForge.Api.Chat;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Chat;

/// <summary>
/// Pins Program.cs's wiring of the per-conversation turn cap, not just the types in isolation:
/// <see cref="ConversationTurnBudgetTests"/> proves the budget refuses, nothing else proves the host registers
/// it, binds its section or validates it on start.
/// </summary>
public sealed class ConversationBudgetWiringTests
{
    private static WebApplicationFactory<Program> Host(params (string Key, string Value)[] extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // UseSetting, not ConfigureAppConfiguration: ValidateOnStart runs while Program builds the host.
            var settings = new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = "https://openemr.budget-wiring-test.invalid",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "budget-wiring-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.budget-wiring-test.invalid",
                ["Llm:ApiKey"] = "budget-wiring-test",
                ["Llm:Model"] = "budget-wiring-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
            };
            foreach (var (key, value) in extra)
            {
                settings[key] = value;
            }

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public void HostStartup_TurnCapNotPositive_FailsFastRatherThanRefusingEveryTurn()
    {
        using var factory = Host(("ConversationBudget:MaxTurnsPerWindow", "0"));

        var act = () => factory.Server;

        act.Should().Throw<OptionsValidationException>()
            .Where(e => e.Message.Contains(nameof(ConversationBudgetOptions.MaxTurnsPerWindow), StringComparison.Ordinal));
    }

    [Fact]
    public void HostStartup_TurnCapConfigured_TheRegisteredBudgetEnforcesTheConfiguredValue()
    {
        using var factory = Host(("ConversationBudget:MaxTurnsPerWindow", "2"));

        var budget = factory.Services.GetRequiredService<IConversationTurnBudget>();

        budget.TryConsume("session-1").Should().BeTrue();
        budget.TryConsume("session-1").Should().BeTrue();
        budget.TryConsume("session-1").Should().BeFalse("the host bound the section, so the third turn is over the cap");
    }

    [Fact]
    public void HostStartup_ChatSessionCoordinator_ResolvesWithItsBudget()
    {
        // The coordinator is resolved only when a hub method runs; a missing registration would surface as a
        // failed chat turn in the demo, not at start-up.
        using var factory = Host();
        using var scope = factory.Services.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ChatSessionCoordinator>();

        act.Should().NotThrow();
    }
}
