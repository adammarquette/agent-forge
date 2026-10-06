using AgentForge.Data;
using AgentForge.Retrieval;

namespace AgentForge.Api.Health;

/// <summary>
/// The startup work that needs the sidecar's Postgres store: this build's migrations (W2-D14), then the
/// guideline corpus seed. Run by <see cref="DataStoreStartupService"/> off the boot path, so a store that is
/// down at boot delays readiness instead of aborting startup.
/// </summary>
public interface IDataStoreStartupWork
{
    /// <summary>Brings the schema to this build's latest migration; throws if the store refused it.</summary>
    /// <param name="cancellationToken">Signalled when the host is stopping.</param>
    Task MigrateAsync(CancellationToken cancellationToken);

    /// <summary>Seeds the guideline corpus and backfills its embeddings; throws if the store or the embedding
    /// provider refused it.</summary>
    /// <param name="cancellationToken">Signalled when the host is stopping.</param>
    Task SeedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IDataStoreStartupWork"/> over the real store. Both steps are idempotent, so an attempt that died
/// part-way is simply run again.
/// </summary>
internal sealed class MigrateAndSeedStartupWork(IServiceProvider services) : IDataStoreStartupWork
{
    /// <inheritdoc />
    public Task MigrateAsync(CancellationToken cancellationToken) =>
        services.MigrateAgentForgeDataAsync(cancellationToken);

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GuidelineCorpusSeeder>()
            .SeedAsync(cancellationToken).ConfigureAwait(false);
    }
}
