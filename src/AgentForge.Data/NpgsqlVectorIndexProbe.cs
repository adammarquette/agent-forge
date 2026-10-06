using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AgentForge.Data;

/// <summary>
/// <see cref="IVectorIndexProbe"/> over a short-lived Npgsql connection: one round trip that asks the
/// catalog for all three facts at once, under the caller's budget.
/// </summary>
public sealed class NpgsqlVectorIndexProbe(IOptions<AgentForgeDataOptions> options) : IVectorIndexProbe
{
    /// <summary>
    /// Catalog-only, so it costs nothing and cannot touch PHI. The HNSW index is matched by access method
    /// and table rather than by name: EF names it, and a hand-built or reindexed one would not match.
    /// </summary>
    private const string InspectSql = """
        SELECT
            EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector'),
            to_regclass('guideline_chunks') IS NOT NULL,
            EXISTS (
                SELECT 1
                FROM pg_index i
                JOIN pg_class idx ON idx.oid = i.indexrelid
                JOIN pg_am am ON am.oid = idx.relam
                WHERE i.indrelid = to_regclass('guideline_chunks') AND am.amname = 'hnsw'
            )
        """;

    /// <inheritdoc />
    public async Task<VectorIndexProbeResult> InspectAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);

        NpgsqlConnection connection;
        try
        {
            connection = new NpgsqlConnection(options.Value.ConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A malformed connection string: the message can echo the string, password included, so this
            // one is reported without a failure to attach.
            return new VectorIndexProbeResult(VectorIndexStatus.Unreachable);
        }

        var inspection = ReadCatalogAsync(connection, deadline.Token);
        var finished = await Task.WhenAny(inspection, Task.Delay(Timeout.Infinite, deadline.Token))
            .ConfigureAwait(false);

        if (finished != inspection)
        {
            // The budget alone does not stop Npgsql: a Postgres that accepts the socket and never speaks
            // held this probe for the full 10s of its test with the token already cancelled. Closing the
            // connection is what ends it - which is the failure mode in its database form.
            AbandonAsync(inspection, connection);
            return new VectorIndexProbeResult(
                VectorIndexStatus.Unreachable, null, TimedOut: !cancellationToken.IsCancellationRequested);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            return await inspection.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or SocketException or OperationCanceledException)
        {
            // A store being down is an expected readiness outcome, not a 500 out of /ready. Only our own
            // budget counts as a timeout; a caller that cancelled says nothing about the store.
            var timedOut = deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            return new VectorIndexProbeResult(VectorIndexStatus.Unreachable, ex, timedOut);
        }
    }

    /// <summary>
    /// Closes the socket out from under an inspection that outlived its budget, then observes whatever it
    /// throws on the way out so an abandoned probe cannot surface as an unobserved task exception.
    /// </summary>
    private static void AbandonAsync(Task<VectorIndexProbeResult> inspection, NpgsqlConnection connection) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or SocketException or ObjectDisposedException)
            {
                // Tearing down a connection mid-operation is expected here; nothing is waiting on it.
            }

            try
            {
                await inspection.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or SocketException or OperationCanceledException
                or ObjectDisposedException or InvalidOperationException)
            {
                // Same: the result was already reported as unreachable.
            }
        });

    private static async Task<VectorIndexProbeResult> ReadCatalogAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(InspectSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new VectorIndexProbeResult(VectorIndexStatus.Unreachable);
        }

        return (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2)) switch
        {
            (false, _, _) => new VectorIndexProbeResult(VectorIndexStatus.ExtensionMissing),
            (_, false, _) => new VectorIndexProbeResult(VectorIndexStatus.SchemaMissing),
            (_, _, false) => new VectorIndexProbeResult(VectorIndexStatus.IndexMissing),
            _ => new VectorIndexProbeResult(VectorIndexStatus.Available),
        };
    }
}
