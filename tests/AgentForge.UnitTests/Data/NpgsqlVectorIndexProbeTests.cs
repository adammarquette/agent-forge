using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgentForge.Data;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Data;

/// <summary>
/// The real probe against a real socket, not a fake: <see cref="VectorIndexHealthCheckTests"/> pins the
/// policy, this pins that the implementation translates a dead store into a value and gives up inside the
/// budget rather than throwing or waiting. No database and no network - a
/// loopback port nothing listens on, and a loopback listener that accepts and never speaks, mirroring
/// <c>StallingHttpMessageHandler</c>.
/// </summary>
public sealed class NpgsqlVectorIndexProbeTests
{
    /// <summary>
    /// Generous next to <see cref="ProbeTimeout"/>: the assertion is "bounded", not "fast", so a loaded
    /// CI agent must not redden it. Npgsql's own default would wait 15s, and a stalled TLS/startup
    /// handshake waits indefinitely.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    private static NpgsqlVectorIndexProbe ProbeAgainst(int port) =>
        new(Options.Create(new AgentForgeDataOptions
        {
            // Pooling=false so one test cannot hand another a cached broken connection.
            ConnectionString =
                $"Host=127.0.0.1;Port={port};Database=agentforge;Username=agentforge;Password=x;Pooling=false",
        }));

    [Fact]
    public async Task InspectAsync_NothingListening_ReportsUnreachableRatherThanThrowing()
    {
        // A dependency being down is an expected readiness outcome, not a 500 out of /ready.
        var probe = ProbeAgainst(ClosedLoopbackPort());

        var result = await probe.InspectAsync(ProbeTimeout, CancellationToken.None);

        result.Status.Should().Be(VectorIndexStatus.Unreachable);
    }

    [Fact]
    public async Task InspectAsync_PostgresAcceptsThenGoesSilent_GivesUpWithinTheProbeBudget()
    {
        // The 100-second failure mode, in its Postgres form: the TCP connect succeeds, so nothing ever
        // refuses the probe - only the budget ends it.
        using var blackhole = new TcpListener(IPAddress.Loopback, 0);
        blackhole.Start();
        using var serving = new CancellationTokenSource();
        var accepting = AcceptAndStayQuietAsync(blackhole, serving.Token);
        var probe = ProbeAgainst(((IPEndPoint)blackhole.LocalEndpoint).Port);

        var stopwatch = Stopwatch.StartNew();
        var result = await probe.InspectAsync(ProbeTimeout, CancellationToken.None);
        stopwatch.Stop();

        result.Status.Should().Be(VectorIndexStatus.Unreachable);
        result.TimedOut.Should().BeTrue();
        stopwatch.Elapsed.Should().BeLessThan(Deadline);
        await serving.CancelAsync();
        await accepting;
    }

    /// <summary>Accepts one connection and holds it open without ever answering the startup packet.</summary>
    private static async Task AcceptAndStayQuietAsync(TcpListener listener, CancellationToken serving)
    {
        try
        {
            using var accepted = await listener.AcceptTcpClientAsync(serving);
            await Task.Delay(Timeout.Infinite, serving);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
            or SocketException or InvalidOperationException)
        {
            // The test finished and cancelled the listener; nothing here is under assertion.
        }
    }

    /// <summary>A loopback port that was bound and released, so a connect to it is refused rather than hung.</summary>
    private static int ClosedLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
