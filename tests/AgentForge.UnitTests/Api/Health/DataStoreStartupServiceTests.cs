using AgentForge.Api.Health;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The startup work that needs the store (migrations, then the guideline seed) runs off the boot path and is
/// retried rather than fatal - the maintainer's ruling, and the retry a separate change asked for. Each failure
/// mode below is one the old inline call turned into a host that never listened.
/// </summary>
public sealed class DataStoreStartupServiceTests
{
    private static readonly DataStoreStartupOptions Fast = new()
    {
        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(4),
    };

    private static DataStoreStartupService Service(IDataStoreStartupWork work, DataStoreStartupState state) =>
        new(work, state, Options.Create(Fast), TimeProvider.System, NullLogger<DataStoreStartupService>.Instance);

    [Fact]
    public async Task ExecuteAsync_StoreUnreachableThenReachable_RetriesUntilTheMigrationsComplete()
    {
        // Given a store that refuses the first two attempts, when the service runs, then it keeps trying and
        // records completion on the third - a store that comes up late is a late start, not a dead host.
        var work = A.Fake<IDataStoreStartupWork>();
        A.CallTo(() => work.MigrateAsync(A<CancellationToken>._))
            .Throws(new InvalidOperationException("refused")).Twice()
            .Then.Returns(Task.CompletedTask);
        var state = new DataStoreStartupState();
        var sut = Service(work, state);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.IsComplete.Should().BeTrue();
        state.FailedAttempts.Should().Be(2);
        A.CallTo(() => work.MigrateAsync(A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
        A.CallTo(() => work.SeedAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_MigrationsFail_DoesNotFaultTheHostedServiceOrSeed()
    {
        // A faulted BackgroundService stops the host by default - which is the abort-on-boot this replaces
        // arriving by another route. And nothing may be seeded into a schema that was never migrated.
        var work = A.Fake<IDataStoreStartupWork>();
        A.CallTo(() => work.MigrateAsync(A<CancellationToken>._)).Throws(new InvalidOperationException("refused"));
        var state = new DataStoreStartupState();
        var sut = Service(work, state);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => state.FailedAttempts >= 3);
        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
        state.IsComplete.Should().BeFalse("the migrations never succeeded, so readiness must not be told they did");
        state.LastFailure.Should().Be(nameof(InvalidOperationException));
        A.CallTo(() => work.SeedAsync(A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_SeedFails_MigrationsStillCountAsCompleteAndTheSeedIsRetried()
    {
        // With the store up, the seed's other dependency is the embedding provider, whose outage degrades
        // retrieval rather than failing it - so a failing seed must not hold /ready at 503.
        var work = A.Fake<IDataStoreStartupWork>();
        A.CallTo(() => work.SeedAsync(A<CancellationToken>._))
            .Throws(new HttpRequestException("embedding provider down")).Twice()
            .Then.Returns(Task.CompletedTask);
        var state = new DataStoreStartupState();
        var sut = Service(work, state);

        await sut.StartAsync(CancellationToken.None);
        await sut.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        state.IsComplete.Should().BeTrue();
        state.FailedAttempts.Should().Be(0, "seed failures are not migration failures");
        A.CallTo(() => work.SeedAsync(A<CancellationToken>._)).MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task StartAsync_StoreNeverAnswers_ReturnsWithoutWaitingForIt()
    {
        // The boot path must not wait on the store at all: a connect that hangs is the same outage as one
        // that is refused, and it must not hold the host off its port either.
        var work = A.Fake<IDataStoreStartupWork>();
        A.CallTo(() => work.MigrateAsync(A<CancellationToken>._))
            .ReturnsLazily(call => Task.Delay(Timeout.Infinite, call.GetArgument<CancellationToken>(0)));
        var sut = Service(work, new DataStoreStartupState());

        var start = sut.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue();
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void NextRetryDelay_DoublesUpToTheCap()
    {
        var options = new DataStoreStartupOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(30),
        };

        DataStoreStartupService.NextRetryDelay(TimeSpan.FromSeconds(1), options).Should().Be(TimeSpan.FromSeconds(2));
        DataStoreStartupService.NextRetryDelay(TimeSpan.FromSeconds(16), options).Should().Be(TimeSpan.FromSeconds(30));
        DataStoreStartupService.NextRetryDelay(TimeSpan.FromSeconds(30), options).Should().Be(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }
}
