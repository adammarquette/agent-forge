using FakeItEasy;
using FluentAssertions;
using AgentForge.Api.Patient;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.UnitTests.Api.Patient;

/// <summary>
/// <c>GET /patient</c> is what the chat page renders its banner from, so it is also where the page learns the
/// key its hub connection must present. Synthetic ids only. A separate change
/// </summary>
public sealed class PatientEndpointsTests
{
    private const string SessionId = "session-synthetic-1";

    private readonly IOpenEmrFhirClient _fhirClient = A.Fake<IOpenEmrFhirClient>();
    private readonly PatientContextService _service;

    public PatientEndpointsTests()
    {
        A.CallTo(() => _fhirClient.GetPatientAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<PatientRecord?>(null));
        A.CallTo(() => _fhirClient.GetConditionsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<ConditionRecord>>([]));
        A.CallTo(() => _fhirClient.GetMedicationRequestsAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<MedicationRecord>>([]));
        A.CallTo(() => _fhirClient.GetAllergiesAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<AllergyRecord>>([]));
        _service = new PatientContextService(
            _fhirClient, A.Fake<IScopedAccessTokenProvider>(), NullLogger<PatientContextService>.Instance,
            new CapturingLogger<AccessAudit>(), A.Fake<ICorrelationIdAccessor>());
    }

    [Fact]
    public async Task HandleGetPatientAsync_LiveSession_ReturnsTheContextKeyForTheSessionsCurrentPatient()
    {
        var session = new PatientSessionContext("token-synthetic", "default", "pt-A", "dr-synthetic", DateTimeOffset.UtcNow.AddHours(1));

        var payload = await GetPayloadAsync(session);

        payload.ContextKey.Should().Be(PatientContextBinding.KeyFor(SessionId, session));
    }

    [Fact]
    public async Task HandleGetPatientAsync_SessionSwitchedPatient_ReturnsAKeyThePreviousPagesConnectionCannotPresent()
    {
        var before = new PatientSessionContext("token-synthetic", "default", "pt-A", "dr-synthetic", DateTimeOffset.UtcNow.AddHours(1));

        var payload = await GetPayloadAsync(before with { PatientId = "pt-B" });

        payload.ContextKey.Should().NotBe(PatientContextBinding.KeyFor(SessionId, before));
    }

    private async Task<PatientContextResponsePayload> GetPayloadAsync(PatientSessionContext session)
    {
        var testSession = new InMemoryTestSession(SessionId);
        testSession.SavePatientSession(session);
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<ISessionFeature>(new TestSessionFeature(testSession));

        var result = await PatientEndpoints.HandleGetPatientAsync(httpContext, _service, TimeProvider.System);

        return result.Should().BeAssignableTo<IValueHttpResult>().Which.Value
            .Should().BeOfType<PatientContextResponsePayload>().Subject;
    }

    private sealed class TestSessionFeature(ISession session) : ISessionFeature
    {
        public ISession Session { get; set; } = session;
    }
}
