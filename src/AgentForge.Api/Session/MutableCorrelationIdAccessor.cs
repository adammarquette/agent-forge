using AgentForge.Integration.OpenEmr.Http;

namespace AgentForge.Api.Session;

/// <summary>
/// Holds the correlation id minted at BFF ingress (ARCHITECTURE.md §11) for the lifetime of one
/// request, hub invocation or agenda fan-out branch, so every log line, tool call and outbound
/// call made while it is in flight carries the same id (FR-OBS-1, NFR-TRACE-1).
/// </summary>
/// <remarks>
/// Backed by a <c>static</c> <see cref="AsyncLocal{T}"/> rather than a per-instance field, for the
/// same reason <see cref="ScopedAccessTokenProvider"/> is:
/// <see cref="CorrelationIdHandler"/> is constructed by <c>IHttpClientFactory</c> in its own
/// handler-building scope and then pooled for the handler's lifetime, never in the calling
/// request's DI scope - so with per-instance storage the handler read a different, never-set
/// accessor and stamped <c>X-Correlation-Id</c> with an id that appeared in no log line anywhere.
/// Ambient storage flows through the real async call chain regardless of which DI scope
/// constructed which object, and stays isolated per logical flow, so concurrent requests cannot
/// observe each other's id.
/// </remarks>
public sealed class MutableCorrelationIdAccessor : ICorrelationIdAccessor
{
    private static readonly AsyncLocal<string?> AmbientCorrelationId = new();

    /// <inheritdoc />
    /// <remarks>
    /// Reading mints one when nothing has been established yet, so a flow that never passed
    /// through ingress still produces a usable trace rather than an empty header.
    /// </remarks>
    public string CorrelationId
    {
        get => AmbientCorrelationId.Value ??= NewCorrelationId();
        set => AmbientCorrelationId.Value = value;
    }

    /// <summary>Mints a fresh correlation id. Ingress sets one explicitly rather than relying on the lazy read.</summary>
    public static string NewCorrelationId() => Guid.NewGuid().ToString("n");
}
