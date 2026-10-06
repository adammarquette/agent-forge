using System.ComponentModel.DataAnnotations;
using AgentForge.Api.Chat;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Chat;

// The per-session LLM budget: one session must not be able to bill LLM turns without bound - chat
// turns, evidence asks and agenda summaries all draw on it.
public sealed class ConversationTurnBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private readonly SettableTimeProvider _clock = new(Start);

    private InMemoryConversationTurnBudget Budget(int maxTurns, TimeSpan? window = null) =>
        new(Options.Create(new ConversationBudgetOptions
        {
            MaxTurnsPerWindow = maxTurns,
            Window = window ?? TimeSpan.FromHours(12),
        }), _clock);

    [Fact]
    public void TryConsume_UpToTheCap_AllowsEveryTurnThenRefusesTheNext()
    {
        var sut = Budget(3);

        var granted = Enumerable.Range(0, 3).Select(_ => sut.TryConsume("session-1")).ToList();

        granted.Should().OnlyContain(allowed => allowed);
        sut.TryConsume("session-1").Should().BeFalse("the fourth turn is over a cap of three");
    }

    [Fact]
    public void TryConsume_OnceRefused_StaysRefusedForTheRestOfTheWindow()
    {
        // A refusal must not reset or roll the counter over - otherwise waiting one call re-opens the tap.
        var sut = Budget(1);
        sut.TryConsume("session-1");
        _clock.Advance(TimeSpan.FromHours(11));

        sut.TryConsume("session-1").Should().BeFalse();
        sut.TryConsume("session-1").Should().BeFalse();
    }

    [Fact]
    public void TryConsume_WindowHasElapsed_TheSessionGetsAFreshBudget()
    {
        // A clinic day's session is not locked out for good: the budget is per session per window.
        var sut = Budget(1, TimeSpan.FromHours(12));
        sut.TryConsume("session-1");

        _clock.Advance(TimeSpan.FromHours(12));

        sut.TryConsume("session-1").Should().BeTrue();
    }

    [Fact]
    public void TryConsume_AnotherSessionExhaustedItsBudget_ThisSessionIsUnaffected()
    {
        // The cap is per session, not global: one noisy session must not lock every clinician out.
        var sut = Budget(1);
        sut.TryConsume("session-1");

        sut.TryConsume("session-2").Should().BeTrue();
    }

    [Fact]
    public async Task TryConsume_ConcurrentTurnsOnOneSession_NeverGrantsMoreThanTheCap()
    {
        // The agenda fans out in parallel on one session; a check-then-increment race would over-grant.
        var sut = Budget(50);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => Task.Run(() => sut.TryConsume("session-1"))));

        results.Count(allowed => allowed).Should().Be(50);
    }

    [Fact]
    public void TryConsume_ManySessionsWhoseWindowsEnded_AreEvictedRatherThanKeptUntilRestart()
    {
        // Every session that ever spent must not stay in memory for the life of the process.
        var sut = Budget(5, TimeSpan.FromHours(1));
        for (var i = 0; i < 1000; i++)
        {
            sut.TryConsume($"old-session-{i}");
        }

        _clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 1000; i++)
        {
            sut.TryConsume("current-session");
        }

        sut.TrackedSessionCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveCap_IsRejected(int maxTurns)
    {
        // A zero cap would refuse every turn, including the brief; fail at start-up rather than in the demo.
        var options = new ConversationBudgetOptions { MaxTurnsPerWindow = maxTurns };

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Validate_NonPositiveWindow_IsRejected(int seconds)
    {
        var options = new ConversationBudgetOptions { Window = TimeSpan.FromSeconds(seconds) };

        options.Validate(new ValidationContext(options)).Should().NotBeEmpty();
    }

    [Fact]
    public void Default_FitsAClinicDay()
    {
        // 25 patients: the agenda reloaded after each visit (~25 x 12.5 remaining summaries), a brief and four
        // follow-ups each, two evidence asks each - about 490 turns. The default must leave headroom over that.
        var options = new ConversationBudgetOptions();

        options.MaxTurnsPerWindow.Should().BeGreaterThanOrEqualTo(600);
        options.Window.Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(10));
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
