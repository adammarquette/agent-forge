using AgentForge.Api.Observability;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using FakeItEasy;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The one server-side signal an expired SMART session produces. A separate change correctly stopped
/// expiry being described as an authorization outcome and removed the two wrong signals that stood
/// in for it, and nothing replaced them - so the one-hour wall exists to measure had no rate
/// of its own. These pin what the replacement may and may not say.
/// </summary>
public sealed class ExpiredSessionSignalTests
{
    private const string SessionId = "session-xyz-the-server-side-key";

    private readonly IAgentForgeMetrics _metrics = A.Fake<IAgentForgeMetrics>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly CapturingLogger<ExpiredSessionSignal> _logger = new();
    private readonly ExpiredSessionSignal _sut;

    public ExpiredSessionSignalTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        _sut = new ExpiredSessionSignal(_metrics, _correlationIdAccessor, _logger);
    }

    [Fact]
    public void Record_AnyExpiredSessionRefusal_CountsItOnItsOwnSeriesAndNeverAsAnAuthorizationDecision()
    {
        // The distinction bought and a separate change must not spend: an aged-out token answers no
        // entitlement question, so re-routing it onto agentforge_authorization_decisions_total (or
        // onto any other existing series) would re-describe expiry as a refusal against the
        // clinician's own patient - the exact falsehood !472 removed.
        _sut.Record(ExpiredSessionSurface.ChatTurn, SessionId);

        A.CallTo(() => _metrics.RecordExpiredSessionRefusal(ExpiredSessionSurface.ChatTurn))
            .MustHaveHappenedOnceExactly();
        A.CallTo(_metrics)
            .Where(call => call.Method.Name != nameof(IAgentForgeMetrics.RecordExpiredSessionRefusal))
            .MustNotHaveHappened();
    }

    [Fact]
    public void Record_WithTheSessionThatDied_CarriesTheCorrelationIdAndAPseudonymRatherThanTheSessionKey()
    {
        // FR-OBS-1 via the own accessor, not a parallel id. And the second half of a separate change
        // acceptance criteria: the session is expired, so the record of its expiry must not become
        // the interesting artefact - the server-side session key is credential-shaped and reaches
        // no log line, exactly as ChatSessionCoordinator's own scope handles it.
        _sut.Record(ExpiredSessionSurface.ChatPreTurn, SessionId);

        var line = _logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain("CorrelationId=corr-1");
        line.Should().Contain($"ConversationId={ConversationId.From(SessionId)}");
        line.Should().NotContain(SessionId);
    }

    [Fact]
    public void Record_WithNoSessionKeyToDeriveFrom_StillCarriesTheCorrelationIdAndNamesTheSurface()
    {
        // ConversationId.From throws on a blank id by design. A telemetry helper that threw on the
        // refusal path would turn a clean 401 into the 500 a separate change was reported as, so the signal
        // degrades to correlation id + surface instead of taking the request down with it.
        _sut.Record(ExpiredSessionSurface.Agenda);

        var line = _logger.Lines.Should().ContainSingle().Subject;
        line.Should().Contain("CorrelationId=corr-1");
        line.Should().Contain(ExpiredSessionSurface.Agenda);
        line.Should().NotContain("ConversationId=");
    }

    [Fact]
    public void Record_AtEverySurfaceTheAppRefusesOn_EmitsALabelFromTheClosedSet()
    {
        // The cardinality claim, made checkable the way AuthorizationDecisionReason.All makes its
        // own: `surface` is an exported label, so it multiplies the series and leaves the process.
        // No patient, site, session or correlation id may ever reach it.
        foreach (var surface in ExpiredSessionSurface.All)
        {
            _sut.Record(surface);
        }

        ExpiredSessionSurface.All.Should().OnlyHaveUniqueItems();
        ExpiredSessionSurface.All.Should().AllSatisfy(s => s.Should().MatchRegex("^[a-z][a-z-]*[a-z]$"));
        _logger.Lines.Should().HaveCount(ExpiredSessionSurface.All.Count);
    }
}
