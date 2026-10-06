using FakeItEasy;
using FluentAssertions;
using AgentForge.Api.Launch;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Auth;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Mcp.Authorization;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Launch;

public sealed class SmartLaunchServiceTests
{
    // The instant every launch in this suite happens; OpenEMR's access tokens live an hour
    // from here.
    private static readonly DateTimeOffset LaunchInstant = new(2026, 9, 18, 15, 5, 36, TimeSpan.Zero);

    private readonly IOpenEmrAuthClient _authClient = A.Fake<IOpenEmrAuthClient>();
    private readonly IPatientRelationshipAuthorizer _relationshipAuthorizer = A.Fake<IPatientRelationshipAuthorizer>();
    private readonly ScopedAccessTokenProvider _tokenProvider = new();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly ILogger<SmartLaunchService> _logger = A.Fake<ILogger<SmartLaunchService>>();
    private readonly ILogger<AccessAudit> _auditLogger = A.Fake<ILogger<AccessAudit>>();
    private readonly FixedTimeProvider _timeProvider = new(LaunchInstant);
    private readonly SmartLaunchService _sut;

    public SmartLaunchServiceTests()
    {
        // The default for every pre-existing test below: the launching clinician is the patient's
        // provider of record, which is what those tests were written against before FR-AUTH-2 was
        // enforced here. The relationship cases set it explicitly.
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 3)));
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        var openEmrOptions = Options.Create(new OpenEmrOptions
        {
            BaseUrl = "https://openemr.example.org",
            Site = "default",
            ClientId = "sidecar-client",
            Scopes = ["patient/patient.read", "launch/patient"],
        });
        var bffOptions = Options.Create(new BffOptions { PublicBaseUrl = "https://sidecar.example.org" });
        _sut = new SmartLaunchService(
            _authClient, _relationshipAuthorizer, _tokenProvider, _correlationIdAccessor,
            openEmrOptions, bffOptions, _timeProvider, _logger, _auditLogger);
    }

    [Fact]
    public void BeginLaunch_ValidLaunchToken_BuildsAuthorizeUrlAgainstTheConfiguredSite()
    {
        var (authorizeUrl, _) = _sut.BeginLaunch("launch-token-abc");

        authorizeUrl.ToString().Should().StartWith("https://openemr.example.org/oauth2/default/authorize?");
        authorizeUrl.ToString().Should().Contain("client_id=sidecar-client");
        authorizeUrl.ToString().Should().Contain("launch=launch-token-abc");
        authorizeUrl.ToString().Should().Contain(Uri.EscapeDataString("https://sidecar.example.org/callback"));
    }

    [Fact]
    public void BeginLaunch_ValidLaunchToken_SendsTheFhirBaseAsAudNotTheBareServerBaseUrl()
    {
        // OpenEMR rejects a bare-server-base aud with "invalid_request - Aud parameter did not
        // match authorized server" (confirmed live) - it expects the FHIR base
        // ({BaseUrl}/apis/{Site}/fhir), the same value every other real client in this repo
        // (tools/MintQaIdentityToken, the QA test fixtures) already sends successfully.
        var (authorizeUrl, _) = _sut.BeginLaunch("launch-token-abc");

        authorizeUrl.ToString().Should().Contain(
            $"aud={Uri.EscapeDataString("https://openemr.example.org/apis/default/fhir")}");
    }

    [Fact]
    public void BeginLaunch_CalledTwice_MintsADifferentStateAndCodeVerifierEachTime()
    {
        // Guards CSRF protection and PKCE: a reused state/verifier across launches would let one
        // launch's callback be replayed against another.
        var (_, first) = _sut.BeginLaunch("launch-token-abc");
        var (_, second) = _sut.BeginLaunch("launch-token-abc");

        first.State.Should().NotBe(second.State);
        first.CodeVerifier.Should().NotBe(second.CodeVerifier);
    }

    [Fact]
    public async Task CompleteLaunchAsync_StateDoesNotMatchThePendingLaunch_ThrowsRatherThanExchangingTheCode()
    {
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");

        var act = () => _sut.CompleteLaunchAsync("auth-code", "a-different-state", pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
        A.CallTo(_authClient).MustNotHaveHappened();
    }

    [Fact]
    public async Task CompleteLaunchAsync_MatchingStateAndValidCode_ExchangesUsingThePkceVerifierFromBeginLaunch()
    {
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                "default", "auth-code", "https://sidecar.example.org/callback", "sidecar-client",
                pending.CodeVerifier, null, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                "default", "access-token-abc", "sidecar-client", A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, "patient/patient.read", "sidecar-client", null, "dr-jones", "patient-123")));

        var result = await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        result.AccessToken.Should().Be("access-token-abc");
        result.Site.Should().Be("default");
        result.PatientId.Should().Be("patient-123");
        result.ClinicianIdentity.Should().Be("dr-jones");
    }

    [Fact]
    public async Task CompleteLaunchAsync_TokenResponseCarriesNoPatientClaim_ThrowsRatherThanStartingAnUnscopedSession()
    {
        // This product is single-patient-scoped for its entire session lifetime (FR-CHAT-3) - a
        // token with no launch patient context can never be turned into a valid PatientSessionContext.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, Patient: null, null)));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
    }

    [Fact]
    public async Task CompleteLaunchAsync_IntrospectionReturnsNoSubject_ThrowsRatherThanStartingAnUnauditableSession()
    {
        // FR-AUTH-4: every patient-data access must be attributable to who accessed it - a session
        // with no clinician identity could never be audited, so it must not be allowed to start.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, "patient/patient.read", "sidecar-client", null, Subject: null, "patient-123")));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
    }

    [Fact]
    public async Task CompleteLaunchAsync_IntrospectionReturnsInactiveWithASubject_ThrowsRatherThanStartingARevokedSession()
    {
        // FR-AUTH-4: a token that OpenEMR reports as no-longer-active (revoked or expired) must not
        // be allowed to start a session, even if introspection still carries a subject claim - this
        // fork's introspection endpoint has a history of not behaving per RFC 7662 (#44, #47), so
        // "subject present but active:false" is a plausible real state, not just a spec nicety.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(false, "patient/patient.read", "sidecar-client", null, "dr-jones", "patient-123")));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
    }

    [Fact]
    public async Task CompleteLaunchAsync_IntrospectionReturnsNoSubject_LogsActiveAndClientIdForDiagnosis()
    {
        // A missing subject is otherwise indistinguishable in the thrown exception between "OpenEMR
        // rejected our client" (active:false) and "active but no subject for some other reason" -
        // this warning is what lets an operator tell the two apart from logs alone, with no PHI.
        A.CallTo(() => _logger.IsEnabled(LogLevel.Warning)).Returns(true);
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(false, "patient/patient.read", "sidecar-client", null, Subject: null, "patient-123")));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
        A.CallTo(_logger).Where(call => call.Method.Name == "Log")
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CompleteLaunchAsync_LaunchingUserHasNoRelationshipToTheLaunchPatient_RefusesTheSession()
    {
        // FR-AUTH-2: OpenEMR's own ACLs let `admin` open any chart, so the SMART launch
        // patient context alone is not an entitlement - the sidecar must add the relationship
        // boundary it cannot inherit (ARCHITECTURE.md §5.3).
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                "default", "admin", "patient-123", A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: false, ClinicDayAppointmentsConsidered: 3)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, "patient/patient.read", "sidecar-client", null, "admin", "patient-123")));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchAuthorizationException>()
            .WithMessage(PatientAccessRefusal.UserFacingMessage);
    }

    [Fact]
    public async Task CompleteLaunchAsync_LaunchingClinicianIsThePatientsProviderOfRecord_StartsTheSession()
    {
        // The permit half of the FR-AUTH-2 AC at this choke point: the rule must let the real
        // clinician through, not merely deny the unrelated one.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                "default", "dr-jones", "patient-123", A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 3)));

        var result = await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        result.PatientId.Should().Be("patient-123");
        result.ClinicianIdentity.Should().Be("dr-jones");
    }

    [Fact]
    public async Task CompleteLaunchAsync_ValidLaunch_PutsTheLaunchTokenInScopeBeforeCheckingTheRelationship()
    {
        // The relationship lookup is a FHIR read, and it must run as the launching user's own
        // token - not unauthenticated, and never as some ambient service identity.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();
        string? tokenSeenByTheLookup = null;
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Invokes(() => tokenSeenByTheLookup = _tokenProvider.AccessToken)
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 3)));

        await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        tokenSeenByTheLookup.Should().Be("access-token-abc");
    }

    [Fact]
    public async Task CompleteLaunchAsync_RefusedForNoRelationship_RecordsTheRefusalInTheAccessAuditTrail()
    {
        // FR-AUTH-4: the refusal is itself an auditable access attempt.
        A.CallTo(() => _auditLogger.IsEnabled(A<LogLevel>._)).Returns(true);
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: false, ClinicDayAppointmentsConsidered: 3)));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchAuthorizationException>();
        A.CallTo(_auditLogger).Where(call => call.Method.Name == "Log").MustHaveHappenedOnceExactly();
    }

    private void GiveAValidTokenAndIntrospection()
    {
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, "patient/patient.read", "sidecar-client", null, "dr-jones", "patient-123")));
    }

    [Fact]
    public async Task CompleteLaunchAsync_IntrospectionCarriesAnExpiry_RecordsItOnTheSession()
    {
        // Introspection already reports `exp` and the launch already reads the response -
        // it was simply thrown away, so nothing downstream could tell a live token from a dead one.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        var expiry = LaunchInstant.AddHours(1);
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(
                true, "patient/patient.read", "sidecar-client", expiry.ToUnixTimeSeconds(), "dr-jones", "patient-123")));

        var result = await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        result.ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public async Task CompleteLaunchAsync_IntrospectionOmitsExpiry_DerivesItFromTheTokenResponseLifetime()
    {
        // RFC 7662 makes `exp` optional and this fork's introspection has a history of not
        // following the RFC, so `expires_in` from the token response is the fallback.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();

        var result = await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        result.ExpiresAt.Should().Be(LaunchInstant.AddSeconds(3600));
    }

    [Fact]
    public async Task CompleteLaunchAsync_NoLifetimeAdvertisedAnywhere_ThrowsRatherThanStartingAnUnboundedSession()
    {
        // Fails closed, like every other unknown on this path: a session whose token expiry is
        // unknown is the exact state a separate change was - authenticated-looking, and 401 on every read.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", ExpiresIn: null, "patient/patient.read", null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, "patient/patient.read", "sidecar-client", null, "dr-jones", "patient-123")));

        var act = () => _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchException>();
    }

    [Fact]
    public async Task CompleteLaunchAsync_BeforeTheRelationshipCheck_AdoptsTheTokenWithItsExpiry()
    {
        // The FR-AUTH-2 lookup is itself a FHIR read made with this token, so it goes through
        // AuthHandler's expiry guard like every other - adopting the token without its expiry
        // would leave that one call unguarded.
        var (_, pending) = _sut.BeginLaunch("launch-token-abc");
        GiveAValidTokenAndIntrospection();
        string? tokenDuringLookup = null;
        DateTimeOffset? expiryDuringLookup = null;
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                tokenDuringLookup = _tokenProvider.AccessToken;
                expiryDuringLookup = _tokenProvider.AccessTokenExpiresAt;
            })
            .Returns(Task.FromResult(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 3)));

        await _sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        tokenDuringLookup.Should().Be("access-token-abc");
        expiryDuringLookup.Should().Be(LaunchInstant.AddSeconds(3600));
    }

    [Fact]
    public async Task CompleteLaunchAsync_TokenGrantedWithoutTheGateScope_NamesTheDroppedScopeAndStillRefuses()
    {
        // the client registration lacked patient/Appointment.read, OpenEMR's finalizeScopes
        // dropped it without an error, and the only sidecar-side trace was an unresolved gate. The
        // refusal must stand (FR-AUTH-2 fails closed); what changes is that the log names the cause.
        var logger = new CapturingLogger<SmartLaunchService>();
        var sut = BuildSutRequesting(logger, "launch/patient", "patient/Patient.read", "patient/Appointment.read");
        var (_, pending) = sut.BeginLaunch("launch-token-abc");
        GiveATokenGranting("patient/Patient.read");
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PatientRelationshipDecision.Unresolved));

        var act = () => sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        await act.Should().ThrowAsync<SmartLaunchAuthorizationException>();
        logger.Lines.Should().ContainSingle(line =>
            line.Contains("patient/Appointment.read", StringComparison.Ordinal) &&
            line.Contains("sidecar-client", StringComparison.Ordinal) &&
            !line.Contains("patient-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteLaunchAsync_TokenGrantedWithoutARequestedScope_StillAsksTheRelationshipGate()
    {
        // The diagnostic must not become a second authorization rule: the gate decides, so a
        // clinician it permits is not refused because of a scope it did not need.
        var sut = BuildSutRequesting(new CapturingLogger<SmartLaunchService>(), "patient/Patient.read", "patient/Binary.read");
        var (_, pending) = sut.BeginLaunch("launch-token-abc");
        GiveATokenGranting("patient/Patient.read");

        var result = await sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        result.PatientId.Should().Be("patient-123");
        A.CallTo(() => _relationshipAuthorizer.AuthorizeAsync(
                A<string>._, A<string?>._, A<string>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CompleteLaunchAsync_EveryRequestedResourceScopeGranted_LogsNoDroppedScope()
    {
        // Non-resource scopes (launch/patient, openid) are compared nowhere: whether OpenEMR echoes
        // them back is not part of what a FHIR read needs, and warning on it every launch is noise.
        var logger = new CapturingLogger<SmartLaunchService>();
        var sut = BuildSutRequesting(logger, "openid", "launch/patient", "patient/Patient.read", "patient/Appointment.read");
        var (_, pending) = sut.BeginLaunch("launch-token-abc");
        GiveATokenGranting("patient/Appointment.read patient/Patient.read");

        await sut.CompleteLaunchAsync("auth-code", pending.State, pending, CancellationToken.None);

        logger.Lines.Should().BeEmpty();
    }

    private SmartLaunchService BuildSutRequesting(ILogger<SmartLaunchService> logger, params string[] scopes) => new(
        _authClient, _relationshipAuthorizer, _tokenProvider, _correlationIdAccessor,
        Options.Create(new OpenEmrOptions
        {
            BaseUrl = "https://openemr.example.org",
            Site = "default",
            ClientId = "sidecar-client",
            Scopes = scopes,
        }),
        Options.Create(new BffOptions { PublicBaseUrl = "https://sidecar.example.org" }),
        _timeProvider, logger, _auditLogger);

    private void GiveATokenGranting(string grantedScope)
    {
        A.CallTo(() => _authClient.ExchangeAuthorizationCodeAsync(
                A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new TokenResponse("access-token-abc", "Bearer", 3600, grantedScope, null, "patient-123", null)));
        A.CallTo(() => _authClient.IntrospectAsync(
                A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new IntrospectionResponse(true, grantedScope, "sidecar-client", null, "dr-jones", "patient-123")));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
