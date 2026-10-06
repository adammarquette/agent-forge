namespace AgentForge.Data;

/// <summary>
/// Inspects the sidecar's Postgres for the three things a dense retrieval actually needs: the server, the
/// <c>vector</c> extension, and the HNSW index the retriever's cosine search is planned against
/// (<c>GuidelineChunkConfiguration</c>, W2-D14). Separate from <see cref="AgentForgeDbContext"/> on purpose -
/// readiness must be answerable in a deployment that never wired Week 2 at all, and must not queue behind
/// the DbContext pool it exists to report on.
/// </summary>
public interface IVectorIndexProbe
{
    /// <summary>
    /// Reports what the store is missing, if anything, within <paramref name="budget"/>.
    /// </summary>
    /// <param name="budget">Wall-clock bound on the whole inspection, connect included.</param>
    /// <param name="cancellationToken">Cancellation token of the caller's own request.</param>
    Task<VectorIndexProbeResult> InspectAsync(TimeSpan budget, CancellationToken cancellationToken);
}

/// <summary>What a single inspection found. Every value but <see cref="VectorIndexStatus.Available"/> means
/// guideline retrieval is broken or degraded for every request.</summary>
public enum VectorIndexStatus
{
    /// <summary>Server reachable, <c>vector</c> installed, <c>guideline_chunks</c> present and HNSW-indexed.</summary>
    Available,

    /// <summary>The server refused the connection, failed the handshake, or never answered inside the budget.</summary>
    Unreachable,

    /// <summary>Reachable, but the <c>vector</c> extension is not installed - every embedding query fails.</summary>
    ExtensionMissing,

    /// <summary>Reachable, but <c>guideline_chunks</c> does not exist - the migrations never ran here.</summary>
    SchemaMissing,

    /// <summary>The table exists without its HNSW index - dense search falls back to a sequential scan.</summary>
    IndexMissing,
}

/// <summary>
/// The outcome of one bounded inspection.
/// </summary>
/// <param name="Status">What the store is missing, if anything.</param>
/// <param name="Failure">The failure that stopped the probe, when there was one and it is safe to surface.</param>
/// <param name="TimedOut">True when <paramref name="Failure"/> is the budget expiring rather than a refusal.</param>
public sealed record VectorIndexProbeResult(
    VectorIndexStatus Status, Exception? Failure = null, bool TimedOut = false);
