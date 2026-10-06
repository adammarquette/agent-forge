using AgentForge.Integration.OpenEmr.Http;

namespace AgentForge.IntegrationTests.Support;

/// <summary>A constant clinician identity, so audit entries in these tests are attributable.</summary>
internal sealed class FixedClinicianIdentityAccessor(string clinicianIdentity) : IClinicianIdentityAccessor
{
    /// <inheritdoc />
    public string? ClinicianIdentity => clinicianIdentity;
}
