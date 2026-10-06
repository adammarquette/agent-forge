using System.Net;
using System.Net.Sockets;
using AgentForge.Api.Health;
using AgentForge.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The second half of the ruling: a host that booted with its store down reaches <c>/ready</c> 200 once the
/// store comes up, without a restart. Boots the real host; only the two things that would need a live Postgres -
/// the startup work and the catalog probe - are replaced by one switch standing for "the store is up".
/// </summary>
public sealed class StoreRecoversAfterBootTests : IDisposable
{
    private readonly SwitchableStore _store = new();
    private readonly WebApplicationFactory<Program> _factory;

    public StoreRecoversAfterBootTests()
    {
        var closedPort = ClosedLoopbackPort();
        var nowhere = $"http://127.0.0.1:{closedPort}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["AgentForgeData:ConnectionString"] =
                    $"Host=127.0.0.1;Port={closedPort};Database=agentforge;Username=agentforge;Password=unused;Timeout=3",
                ["DataStoreStartup:InitialRetryDelay"] = "00:00:00.020",
                ["DataStoreStartup:MaxRetryDelay"] = "00:00:00.050",
                ["OpenEmr:BaseUrl"] = nowhere,
                // Loopback only, so plain http is the local case the option exists for.
                ["OpenEmr:AllowInsecureHttpForLocalDevelopment"] = "true",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "store-recovers-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.store-recovers-test.invalid",
                ["Llm:BaseUrl"] = nowhere,
                ["Llm:ApiKey"] = "store-recovers-test",
                ["Llm:Model"] = "store-recovers-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                ["Cohere:ApiKey"] = string.Empty,
            })
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IDataStoreStartupWork>(_store);
                services.AddSingleton<IVectorIndexProbe>(_store);
                // Only the store is under test here; the other checks would 503 on the unreachable fakes anyway.
                services.Configure<HealthCheckServiceOptions>(options =>
                {
                    foreach (var other in options.Registrations.Where(r => r.Name != "vector-index").ToList())
                    {
                        options.Registrations.Remove(other);
                    }
                });
            });
        });
    }

    [Fact]
    public async Task Ready_StoreComesUpAfterBoot_Returns200WithoutARestart()
    {
        // Given a host that booted with its store down and is reporting 503,
        using var client = _factory.CreateClient();
        using (var before = await client.GetAsync(new Uri("/ready", UriKind.Relative)))
        {
            before.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        // when the store becomes reachable,
        _store.Up = true;

        // then the retried startup work completes and the same process reports ready.
        var status = HttpStatusCode.ServiceUnavailable;
        for (var i = 0; i < 100 && status != HttpStatusCode.OK; i++)
        {
            await Task.Delay(50);
            using var after = await client.GetAsync(new Uri("/ready", UriKind.Relative));
            status = after.StatusCode;
        }

        status.Should().Be(HttpStatusCode.OK);
    }

    public void Dispose() => _factory.Dispose();

    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A store that refuses everything until <see cref="Up"/>, then has the schema and index.</summary>
    private sealed class SwitchableStore : IDataStoreStartupWork, IVectorIndexProbe
    {
        private volatile bool _up;

        public bool Up
        {
            get => _up;
            set => _up = value;
        }

        public Task MigrateAsync(CancellationToken cancellationToken) => Refused();

        public Task SeedAsync(CancellationToken cancellationToken) => Refused();

        public Task<VectorIndexProbeResult> InspectAsync(TimeSpan budget, CancellationToken cancellationToken) =>
            Task.FromResult(new VectorIndexProbeResult(Up ? VectorIndexStatus.Available : VectorIndexStatus.Unreachable));

        private Task Refused() =>
            Up ? Task.CompletedTask : Task.FromException(new InvalidOperationException("connection refused"));
    }
}
