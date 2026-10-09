using AgentForge.Api.Health;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The result cache in front of the <c>/ready</c> checks that call out of the process: <c>/ready</c> is
/// public, so without it every call became one request to the model provider, OpenEMR or Cohere
/// </summary>
public sealed class CachedReadinessCheckTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    // Guards every await on a shared probe, so a slot that never clears fails the test instead of hanging it.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly SettableTimeProvider _clock = new(Start);
    private readonly IHealthCheck _inner = A.Fake<IHealthCheck>();

    private CachedReadinessCheck<IHealthCheck> CreateSut() =>
        new(_inner, new ReadinessResultCache<IHealthCheck>(
            _clock, Options.Create(new ReadinessOptions { ResultCacheTtl = Ttl })));

    private void InnerReturns(params HealthCheckResult[] results)
    {
        var queue = new Queue<HealthCheckResult>(results);
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek()));
    }

    private void InnerProbedTimes(int times) =>
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .MustHaveHappened(times, Times.Exactly);

    [Fact]
    public async Task CheckHealthAsync_CalledRepeatedlyWithinTheTtl_ProbesTheDependencyOnce()
    {
        // The amplification removes: N /ready calls inside the TTL make one dependency request.
        InnerReturns(HealthCheckResult.Healthy("first"), HealthCheckResult.Unhealthy("second"));
        var sut = CreateSut();

        var results = new List<HealthCheckResult>();
        for (var i = 0; i < 5; i++)
        {
            results.Add(await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None));
            _clock.Advance(TimeSpan.FromSeconds(5));
        }

        InnerProbedTimes(1);
        results.Should().OnlyContain(r => r.Status == HealthStatus.Healthy && r.Description == "first");
    }

    [Fact]
    public async Task CheckHealthAsync_TtlElapsed_ProbesAgainAndReportsTheNewAnswer()
    {
        // Boundary: one tick short of the TTL is still the cached answer; at the TTL the dependency is
        // asked again, so an outage shows on /ready within one TTL of the last probe.
        InnerReturns(HealthCheckResult.Healthy("before"), HealthCheckResult.Unhealthy("outage"));
        var sut = CreateSut();

        await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        _clock.Advance(Ttl - TimeSpan.FromTicks(1));
        var justBefore = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        _clock.Advance(TimeSpan.FromTicks(1));
        var atTtl = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        justBefore.Status.Should().Be(HealthStatus.Healthy);
        atTtl.Status.Should().Be(HealthStatus.Unhealthy);
        atTtl.Description.Should().Be("outage");
        InnerProbedTimes(2);
    }

    [Fact]
    public async Task CheckHealthAsync_DependencyUnhealthy_CachesTheFailureAndReportsRecoveryAfterTheTtl()
    {
        // A failing answer is cached too: a key the provider rejects would otherwise turn every /ready
        // call back into a provider request for as long as the outage lasts.
        InnerReturns(HealthCheckResult.Unhealthy("down"), HealthCheckResult.Healthy("recovered"));
        var sut = CreateSut();

        var first = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var cached = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        _clock.Advance(Ttl);
        var recovered = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        first.Status.Should().Be(HealthStatus.Unhealthy);
        cached.Status.Should().Be(HealthStatus.Unhealthy);
        recovered.Status.Should().Be(HealthStatus.Healthy);
        InnerProbedTimes(2);
    }

    [Fact]
    public async Task CheckHealthAsync_DependencyDegraded_ReturnsTheDegradedResultUnchanged()
    {
        // D17's mapping belongs to the check, not the cache: a provider 429 stays Degraded, with the
        // check's own description, whether it is served fresh or from the cache.
        var degraded = HealthCheckResult.Degraded("rate-limited (429)");
        InnerReturns(degraded);
        var sut = CreateSut();

        var fresh = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var cached = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        fresh.Should().BeEquivalentTo(degraded);
        cached.Should().BeEquivalentTo(degraded);
    }

    [Fact]
    public async Task CheckHealthAsync_ConcurrentCallsDuringAProbe_ShareThatProbe()
    {
        // /ready requests arrive concurrently; a cache that only helps once the first probe has
        // finished would still let a burst through as one dependency request each.
        var gate = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .Returns(gate.Task);
        var sut = CreateSut();

        var calls = Enumerable.Range(0, 5)
            .Select(_ => sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None))
            .ToList();
        gate.SetResult(HealthCheckResult.Healthy("shared"));
        var results = await Task.WhenAll(calls);

        InnerProbedTimes(1);
        results.Should().OnlyContain(r => r.Description == "shared");
    }

    [Fact]
    public async Task CheckHealthAsync_CallerAbortsMidProbeThenCallsAgainWithinTheTtl_ProbesOnce()
    {
        // Regression guard from a review: the probe used to run on the caller's token and its answer
        // was dropped when that caller aborted, so a client looping `curl --max-time 0.2 /ready` kept the
        // cache empty and made one dependency request per call. The probe must outlive its caller.
        var gate = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .ReturnsLazily((HealthCheckContext _, CancellationToken token) =>
                token.CanBeCanceled ? Task.FromResult(HealthCheckResult.Unhealthy("unreachable")) : gate.Task);
        var sut = CreateSut();
        using var aborted = new CancellationTokenSource();

        var first = sut.CheckHealthAsync(new HealthCheckContext(), aborted.Token);
        await aborted.CancelAsync();
        await first.Invoking(t => t.WaitAsync(Deadline)).Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult(HealthCheckResult.Healthy("reachable"));
        var second = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).WaitAsync(Deadline);

        second.Description.Should().Be("reachable");
        InnerProbedTimes(1);
    }

    [Fact]
    public async Task CheckHealthAsync_FirstCallerCancels_OtherCallersStillGetTheAnswer()
    {
        var gate = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .Returns(gate.Task);
        var sut = CreateSut();
        using var aborted = new CancellationTokenSource();

        var leader = sut.CheckHealthAsync(new HealthCheckContext(), aborted.Token);
        var follower = sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        await aborted.CancelAsync();
        gate.SetResult(HealthCheckResult.Healthy("shared"));

        (await follower.WaitAsync(Deadline)).Description.Should().Be("shared");
        await leader.Invoking(t => t.WaitAsync(Deadline)).Should().ThrowAsync<OperationCanceledException>();
        InnerProbedTimes(1);
    }

    [Fact]
    public async Task CheckHealthAsync_WaiterCancels_ThrowsToThatWaiterAloneAndAtOnce()
    {
        // A waiter's cancellation ends its own wait immediately, before the probe answers, and reaches no
        // other caller.
        var gate = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .Returns(gate.Task);
        var sut = CreateSut();
        using var aborted = new CancellationTokenSource();

        var leader = sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        var waiter = sut.CheckHealthAsync(new HealthCheckContext(), aborted.Token);
        await aborted.CancelAsync();

        await waiter.Invoking(t => t.WaitAsync(Deadline)).Should().ThrowAsync<OperationCanceledException>();
        leader.IsCompleted.Should().BeFalse("the probe has not answered yet");
        gate.SetResult(HealthCheckResult.Healthy("shared"));
        (await leader.WaitAsync(Deadline)).Description.Should().Be("shared");
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeThrows_CachesNothingAndTheNextCallProbesAgain()
    {
        // A throwing probe must not poison the slot: neither a cached fault nor an in-flight task that
        // never clears.
        var calls = 0;
        A.CallTo(() => _inner.CheckHealthAsync(A<HealthCheckContext>._, A<CancellationToken>._))
            .ReturnsLazily(() => Interlocked.Increment(ref calls) == 1
                ? Task.FromException<HealthCheckResult>(new InvalidOperationException("probe blew up"))
                : Task.FromResult(HealthCheckResult.Healthy("recovered")));
        var sut = CreateSut();

        await sut.Invoking(s => s.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).WaitAsync(Deadline))
            .Should().ThrowAsync<InvalidOperationException>();
        var next = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).WaitAsync(Deadline);

        next.Description.Should().Be("recovered");
        InnerProbedTimes(2);
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
