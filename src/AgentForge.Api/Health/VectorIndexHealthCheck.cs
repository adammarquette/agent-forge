using AgentForge.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Health;

/// <summary>
/// Readiness check for the vector index RAG retrieves through - the sidecar's Postgres, the <c>vector</c>
/// extension and the <c>guideline_chunks</c> HNSW index (NFR-HEALTH-W2-1, UC-5). Bounded by
/// <see cref="ReadinessOptions.ProbeTimeout"/> like every other <c>/ready</c> probe.
/// </summary>
/// <remarks>
/// <b>This is D17's existing line applied to RAG, not a new one.</b> <i>Configured</i> - Week 2 is wired,
/// because <c>AgentForgeData:ConnectionString</c> is set (<c>Program.cs</c>) - and not usable is
/// <see cref="HealthStatus.Unhealthy"/>, exactly like OpenEMR, the LLM provider and a configured
/// Prometheus. The argument is a fortiori: the maintainer ruled that an unreachable *observability*
/// backend fails readiness, and the vector index is the more product-blocking of the two - a dashboard
/// nobody is watching versus every evidence-grounded answer the Week 2 agent gives. <i>Unconfigured</i>
/// stays <see cref="HealthStatus.Degraded"/>: a Week 1 deployment legitimately has no vector index, the
/// host boots and serves without one by design, and 503-ing there would pull a working sidecar out of
/// rotation over a store it was never given - D17's load-shedding argument, unchanged.
/// <para>
/// <b>Reachable is deliberately not the test.</b> A database that answers but has no <c>vector</c>
/// extension, or no <c>guideline_chunks</c> because the migrations never ran here, fails every retrieval
/// exactly as an unreachable one does. A missing HNSW index is the softer case and still fails readiness:
/// the schema is not at the migration this build expects, and dense search silently falls back to a
/// sequential scan of the corpus, which is an NFR-SLO-W2 breach rather than an error anyone would see.
/// </para>
/// <para>
/// The reranker is checked separately by <see cref="RerankerHealthCheck"/>.
/// </para>
/// <para>
/// <b>Configured also means this build's migrations have run.</b> They run after the host is listening and
/// retry until the store answers (<see cref="DataStoreStartupService"/>), so a store that was down at boot reads
/// here exactly like one that went down later - 503, naming the store - and the check stays 503 until they
/// complete, even over an index an earlier deploy left in place. The guideline seed after them does not gate it.
/// </para>
/// </remarks>
public sealed class VectorIndexHealthCheck : IHealthCheck
{
    private readonly IVectorIndexProbe _probe;
    private readonly IOptions<AgentForgeDataOptions> _dataOptions;
    private readonly IOptions<ReadinessOptions> _readinessOptions;
    private readonly DataStoreStartupState _startup;

    // Explicit, not primary: with that header semgrep 1.174.0 cannot parse this file at all.
    /// <summary>Creates the check over the probe, the data and readiness options, and the startup state.</summary>
    public VectorIndexHealthCheck(
        IVectorIndexProbe probe,
        IOptions<AgentForgeDataOptions> dataOptions,
        IOptions<ReadinessOptions> readinessOptions,
        DataStoreStartupState startup)
    {
        _probe = probe;
        _dataOptions = dataOptions;
        _readinessOptions = readinessOptions;
        _startup = startup;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_dataOptions.Value.ConnectionString))
        {
            return HealthCheckResult.Degraded(
                $"{AgentForgeDataOptions.SectionName}:ConnectionString not configured - guideline retrieval is not wired in this deployment, so the vector index was never contacted.");
        }

        var probeTimeout = _readinessOptions.Value.ProbeTimeout;
        var result = await _probe.InspectAsync(probeTimeout, cancellationToken).ConfigureAwait(false);
        var reading = Read(result, probeTimeout);
        if (_startup.IsComplete)
        {
            return reading;
        }

        // Even an index an earlier deploy left behind is not ready until THIS build has migrated.
        return HealthCheckResult.Unhealthy($"{reading.Description} {_startup.DescribePending()}", reading.Exception);
    }

    private static HealthCheckResult Read(VectorIndexProbeResult result, TimeSpan probeTimeout) =>
        result.Status switch
        {
            VectorIndexStatus.Available =>
                HealthCheckResult.Healthy("Vector index available (pgvector extension and the guideline_chunks HNSW index both present)."),
            VectorIndexStatus.ExtensionMissing =>
                HealthCheckResult.Unhealthy("Vector index unusable: Postgres answered but the pgvector extension is not installed - every guideline retrieval fails."),
            VectorIndexStatus.SchemaMissing =>
                HealthCheckResult.Unhealthy("Vector index unusable: Postgres answered but the guideline_chunks table does not exist - the migrations have not been applied here."),
            VectorIndexStatus.IndexMissing =>
                HealthCheckResult.Unhealthy("Vector index unusable: guideline_chunks has no HNSW index - dense retrieval degrades to a sequential scan of the corpus."),
            _ => HealthCheckResult.Unhealthy(
                $"Vector index (Postgres/pgvector) {ReadinessProbe.DescribeUnreachable(result.TimedOut, probeTimeout)}.",
                result.Failure),
        };
}
