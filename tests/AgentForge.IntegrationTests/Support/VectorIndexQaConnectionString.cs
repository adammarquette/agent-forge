using Microsoft.Extensions.Configuration;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Resolves the real, reachable, migratable Postgres/pgvector connection string the vector-index
/// <c>/ready</c> healthy case needs (<c>CONVENTIONS.md</c> §8.2 - nothing mocked). Unlike the OpenEMR,
/// LLM-provider and observability checks, a non-empty <c>AgentForgeData:ConnectionString</c> also
/// wires <c>Program.cs</c>'s Week 2 data tier (<c>weekTwoEnabled</c>) and its background migration
/// (<c>DataStoreStartupService</c>), both of which run for real once
/// <see cref="Api.ReadyProbeFactory"/> supplies the connection string via <c>UseSetting</c> - so the
/// healthy case needs a database that migration can actually succeed against.
/// </summary>
public static class VectorIndexQaConnectionString
{
    /// <summary>Environment variable prefix: AgentForgeDataQa__ConnectionString.</summary>
    public const string SectionName = "AgentForgeDataQa";

    /// <summary>
    /// The QA Postgres connection string, or an exception naming the environment variable this test
    /// needs - there is no mock fallback to fail over to.
    /// </summary>
    public static string Resolve()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = configuration.GetSection(SectionName)["ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"The vector-index readiness healthy case requires {SectionName}__ConnectionString, a real " +
                "reachable Postgres/pgvector instance this build's migrations can run against " +
                "(ARCHITECTURE.md D17) - there is no mock fallback to fail over to (CONVENTIONS.md §8.2).");
        }

        return connectionString;
    }
}
