using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Observability;

namespace AgentForge.Evals.Authorization;

/// <summary>The requester the case names, as the dispatcher and the audit trail see it.</summary>
internal sealed class FixedClinicianIdentity(string? identity) : IClinicianIdentityAccessor
{
    /// <inheritdoc />
    public string? ClinicianIdentity { get; } = identity;
}

/// <summary>A pinned correlation id — a real one per run would put a fresh GUID in every audit line and make
/// the log assertions non-reproducible.</summary>
internal sealed class FixedCorrelationId : ICorrelationIdAccessor
{
    /// <inheritdoc />
    public string CorrelationId => "eval-correlation-0000";
}

/// <summary>Time the cases never depend on: the seeded clinic day is the fixture, not the calendar.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => now;
}
