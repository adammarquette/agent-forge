using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Runs the real BFF host (Epic 10, REQUIREMENTS.md §13.1) under caller-supplied configuration - one factory
/// per test, since each <c>/ready</c> dependency case needs a different combination of the four
/// checks to be healthy or broken (<see cref="HealthEndpointQaFixture"/> fixes OpenEMR broken for
/// every test in its class, which only <see cref="HealthEndpointReadinessTests"/>'s original two
/// cases need). Nothing here is mocked (CONVENTIONS.md §8.2): every check still makes its real call: an
/// HTTP round trip or an actual Postgres connection attempt, just against whatever peer the config
/// under test names.
/// </summary>
/// <remarks>
/// <c>UseSetting</c>, not <c>ConfigureAppConfiguration</c>: <c>Program.cs</c> decides whether Week 2
/// is wired (<c>weekTwoEnabled</c>, which gates <c>AgentForgeDbContext</c>'s DI registration) by
/// reading <c>builder.Configuration</c> synchronously, before <c>Build()</c> - which is also before
/// <c>WebApplicationFactory</c> applies a <c>ConfigureAppConfiguration</c> callback, so an override
/// added that way is invisible to that read. <c>UseSetting</c> writes directly into the same
/// configuration <c>Program.cs</c> reads at that point (confirmed: it wins over even an ambient
/// <c>AgentForgeData__ConnectionString</c> environment variable in this process). Matches the pattern
/// <c>StoreUnreachableAtBootTests</c>/<c>StoreRecoversAfterBootTests</c> use for the same reason
/// </remarks>
public sealed class ReadyProbeFactory(IReadOnlyDictionary<string, string?> configuration) : WebApplicationFactory<global::Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in configuration)
        {
            builder.UseSetting(key, value);
        }
    }
}
