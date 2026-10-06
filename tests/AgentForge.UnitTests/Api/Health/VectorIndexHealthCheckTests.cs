using AgentForge.Api.Health;
using AgentForge.Data;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The readiness policy for the vector index - `ARCHITECTURE.md` D17 applied to RAG's own store
/// . Both directions are pinned deliberately: a check only ever seen passing is
/// not a tested check, and the failing direction is the one exists for.
/// </summary>
public sealed class VectorIndexHealthCheckTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    private static IOptions<ReadinessOptions> Readiness =>
        Options.Create(new ReadinessOptions { ProbeTimeout = ProbeTimeout });

    private static IOptions<AgentForgeDataOptions> Configured =>
        Options.Create(new AgentForgeDataOptions
        {
            ConnectionString = "Host=agentforge-db;Database=agentforge;Username=agentforge",
        });

    // The store's startup work (migrations, seed) has finished - every case below that is not about it.
    private static DataStoreStartupState Started
    {
        get
        {
            var state = new DataStoreStartupState();
            state.MarkComplete();
            return state;
        }
    }

    private static IVectorIndexProbe ProbeReturning(VectorIndexProbeResult result)
    {
        var probe = A.Fake<IVectorIndexProbe>();
        A.CallTo(() => probe.InspectAsync(A<TimeSpan>._, A<CancellationToken>._)).Returns(result);
        return probe;
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionStringNotConfigured_ReturnsDegradedWithoutProbing()
    {
        // D17's unconfigured shape: Week 2 is wired only where AgentForgeData:ConnectionString is set
        // (Program.cs), so "never contacted" is the honest answer in a Week 1 deployment - not a 503
        // that would pull a working sidecar out of rotation over a store it was never given.
        var probe = ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Available));
        var sut = new VectorIndexHealthCheck(probe, Options.Create(new AgentForgeDataOptions()), Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("not configured");
        A.CallTo(() => probe.InspectAsync(A<TimeSpan>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task CheckHealthAsync_VectorIndexAvailable_ReturnsHealthy()
    {
        // The paired direction. Without it "fails readiness" could be satisfied by a check that fails
        // unconditionally, which would take every environment out of rotation.
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Available)), Configured, Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_PostgresUnreachable_ReturnsUnhealthyNamingTheDependency()
    {
        // the headline: RAG down while /ready is green. A configured store that does not answer
        // is a 503, exactly like OpenEMR, the LLM provider and a configured Prometheus (D17).
        var failure = new InvalidOperationException("connection refused");
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Unreachable, failure)),
            Configured,
            Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Vector index").And.Contain("unreachable");
        result.Exception.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task CheckHealthAsync_ProbeExceedsTheBudget_ReturnsUnhealthyNamingTheBudget()
    {
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Unreachable, null, TimedOut: true)),
            Configured,
            Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("no answer within");
    }

    [Fact]
    public async Task CheckHealthAsync_VectorExtensionMissing_ReturnsUnhealthyRatherThanTreatingReachableAsReady()
    {
        // The acceptance criterion's specific point: a reachable database with no `vector` extension
        // fails every retrieval exactly as an unreachable one does, so "connected" is not "ready".
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.ExtensionMissing)), Configured, Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("vector").And.Contain("extension");
    }

    [Fact]
    public async Task CheckHealthAsync_GuidelineChunksTableMissing_ReturnsUnhealthy()
    {
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.SchemaMissing)), Configured, Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("guideline_chunks");
    }

    [Fact]
    public async Task CheckHealthAsync_HnswIndexMissing_ReturnsUnhealthy()
    {
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.IndexMissing)), Configured, Readiness, Started);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("HNSW");
    }

    [Fact]
    public async Task CheckHealthAsync_Configured_BoundsTheProbeByTheReadinessBudget()
    {
        // Same bound as every other /ready probe - readiness is always read under
        // someone else's deadline, and a database that accepts the socket and stalls is the exact
        // failure mode that took staging's /ready to 100.33s.
        var probe = ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Available));
        var sut = new VectorIndexHealthCheck(probe, Configured, Readiness, Started);

        await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        A.CallTo(() => probe.InspectAsync(ProbeTimeout, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CheckHealthAsync_StoreUnreachableWhileStartupWorkPending_ReturnsUnhealthyNamingTheStoreAndTheRetry()
    {
        // the store was down at boot, so the migrations are still retrying. The 503 names the store
        // and says the host is waiting on it, which is what tells a cold start apart from a dead sidecar.
        var pending = new DataStoreStartupState();
        pending.RecordFailure(new InvalidOperationException("refused"));
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Unreachable)), Configured, Readiness, pending);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Postgres").And.Contain("unreachable")
            .And.Contain("1 failed attempt").And.Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task CheckHealthAsync_IndexAvailableButStartupWorkPending_ReturnsUnhealthy()
    {
        // The index from an earlier deploy can already be there while THIS build's migrations have not run:
        // before a separate change the host did not serve until they had, and /ready must not claim otherwise.
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Available)), Configured, Readiness,
            new DataStoreStartupState());

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("migrations");
    }

    [Fact]
    public async Task CheckHealthAsync_NotConfiguredAndStartupWorkNeverRan_StaysDegraded()
    {
        // A Week 1 deployment has no store and so no startup work: pending forever must not turn its
        // Degraded into a 503 (D17's unconfigured carve-out).
        var sut = new VectorIndexHealthCheck(
            ProbeReturning(new VectorIndexProbeResult(VectorIndexStatus.Available)),
            Options.Create(new AgentForgeDataOptions()),
            Readiness,
            new DataStoreStartupState());

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
    }
}
