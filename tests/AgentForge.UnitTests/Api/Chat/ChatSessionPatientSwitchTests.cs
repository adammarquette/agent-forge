using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;
using AgentForge.Verification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Chat;

/// <summary>
/// A patient switch inside one browser session (the agenda drill-down, or a second SMART launch on the
/// same cookie) keeps the session id but changes the launch context's patient. The conversation state is
/// keyed by session id, so these tests compose the real coordinator, orchestrator and state store and fake
/// only the model and the tool dispatcher: the assertion is on the patient id the tool call actually
/// carries.
/// </summary>
public sealed class ChatSessionPatientSwitchTests
{
    private const string SessionId = "session-switch";
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddHours(1);

    private readonly ILlmProvider _llmProvider = A.Fake<ILlmProvider>();
    private readonly IMcpToolDispatcher _toolDispatcher = A.Fake<IMcpToolDispatcher>();
    private readonly IClinicalResponseVerifier _verifier = A.Fake<IClinicalResponseVerifier>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly List<string> _dispatchedPatientIds = [];
    private readonly AgentOrchestrator _orchestrator;
    private readonly ChatSessionCoordinator _sut;

    public ChatSessionPatientSwitchTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-switch");
        A.CallTo(() => _verifier.Verify(A<string>._, A<IReadOnlyCollection<string>>._))
            .ReturnsLazily((string answer, IReadOnlyCollection<string> _) => new VerificationResult(true, answer, [], []));

        // Every turn: one tool call, then a final answer.
        var toolCall = new LlmToolCall("call_1", "get_labs", "{}");
        var toolRound = true;
        A.CallTo(() => _llmProvider.CompleteAsync(A<LlmRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                var response = toolRound
                    ? new LlmResponse(string.Empty, [toolCall], LlmStopReason.ToolUse, new LlmUsage(1, 1, 0m))
                    : new LlmResponse("answer", [], LlmStopReason.EndTurn, new LlmUsage(1, 1, 0m));
                toolRound = !toolRound;
                return Task.FromResult(response);
            });
        A.CallTo(() => _toolDispatcher.DispatchAsync(A<string>._, A<string>._, A<LlmToolCall>._, A<CancellationToken>._))
            .ReturnsLazily((string _, string patientId, LlmToolCall call, CancellationToken _) =>
            {
                _dispatchedPatientIds.Add(patientId);
                return Task.FromResult(new LlmToolResultContent(call.Id, "{}"));
            });

        _orchestrator = new AgentOrchestrator(
            _llmProvider, _toolDispatcher, _verifier, A.Fake<IAgentForgeMetrics>(),
            Options.Create(new AgentOptions { TurnDeadline = TimeSpan.FromSeconds(60) }),
            NullLogger<AgentOrchestrator>.Instance);
        _sut = Coordinator(new ConversationBudgetOptions());
    }

    private ChatSessionCoordinator Coordinator(ConversationBudgetOptions budget) => new(
        _orchestrator, new InMemoryConversationStateStore(), new InMemoryChatMessageOutbox(),
        A.Fake<IScopedAccessTokenProvider>(), A.Fake<IScopedClinicianIdentityAccessor>(), _correlationIdAccessor,
        new CapturingLogger<ChatSessionCoordinator>(), new CapturingLogger<AccessAudit>(),
        new InMemoryConversationTurnBudget(Options.Create(budget), TimeProvider.System));

    private static PatientSessionContext SessionFor(string patientId, string site = "default") =>
        new("token-synthetic", site, patientId, "dr-synthetic", Expiry);

    [Fact]
    public async Task AskFollowUpAsync_PatientSwitchedAfterTheBrief_NextToolCallTargetsTheNewPatient()
    {
        await _sut.RequestBriefAsync(SessionId, SessionFor("pt-A"), CancellationToken.None);
        _dispatchedPatientIds.Should().Equal("pt-A");
        _dispatchedPatientIds.Clear();

        // Drill-down to another patient: same session id, new launch context, no fresh brief first.
        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-B"), "Latest potassium?", CancellationToken.None);

        _dispatchedPatientIds.Should().Equal("pt-B");
    }

    [Fact]
    public async Task AskFollowUpAsync_PatientSwitchedAfterAFollowUp_NextToolCallTargetsTheNewPatient()
    {
        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A"), "Latest potassium?", CancellationToken.None);
        _dispatchedPatientIds.Clear();

        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-B"), "And the creatinine?", CancellationToken.None);

        _dispatchedPatientIds.Should().Equal("pt-B");
    }

    [Fact]
    public async Task AskFollowUpAsync_SamePatientSwitchedBackTo_StillTargetsTheCurrentPatient()
    {
        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A"), "Latest potassium?", CancellationToken.None);
        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-B"), "Latest potassium?", CancellationToken.None);
        _dispatchedPatientIds.Clear();

        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A"), "And the creatinine?", CancellationToken.None);

        _dispatchedPatientIds.Should().Equal("pt-A");
    }

    [Fact]
    public async Task AskFollowUpAsync_SiteSwitchedForTheSamePatientId_NextToolCallCarriesTheNewSite()
    {
        // Patient ids are only unique within an OpenEMR site, so the site is part of the patient's identity.
        var dispatchedSites = new List<string>();
        A.CallTo(() => _toolDispatcher.DispatchAsync(A<string>._, A<string>._, A<LlmToolCall>._, A<CancellationToken>._))
            .ReturnsLazily((string site, string _, LlmToolCall call, CancellationToken _) =>
            {
                dispatchedSites.Add(site);
                return Task.FromResult(new LlmToolResultContent(call.Id, "{}"));
            });
        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A", "site-one"), "Latest potassium?", CancellationToken.None);
        dispatchedSites.Clear();

        await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A", "site-two"), "Latest potassium?", CancellationToken.None);

        dispatchedSites.Should().Equal("site-two");
    }

    [Fact]
    public async Task Resume_PatientSwitchedThenReconnectFromSequenceZero_ReplaysNothingDeliveredForThePreviousPatient()
    {
        // A freshly loaded page reconnects before its first message and asks for everything since 0. The outbox is
        // keyed by the session id, which the switch keeps.
        var briefA = await _sut.RequestBriefAsync(SessionId, SessionFor("pt-A"), CancellationToken.None);
        var answerA = await _sut.AskFollowUpAsync(SessionId, SessionFor("pt-A"), "Latest potassium?", CancellationToken.None);
        var briefB = await _sut.RequestBriefAsync(SessionId, SessionFor("pt-B"), CancellationToken.None);

        var replayedForB = _sut.Resume(SessionId, SessionFor("pt-B"), 0);

        replayedForB.Should().Equal(briefB);
        // The other direction: the filter is by patient, not "latest only" - switching back replays pt-A's own.
        _sut.Resume(SessionId, SessionFor("pt-A"), 0).Should().Equal(briefA, answerA);
    }

    [Fact]
    public async Task Resume_SiteSwitchedForTheSamePatientId_ReplaysNothingFromTheOtherSite()
    {
        await _sut.RequestBriefAsync(SessionId, SessionFor("pt-A", "site-one"), CancellationToken.None);

        _sut.Resume(SessionId, SessionFor("pt-A", "site-two"), 0).Should().BeEmpty();
    }

    [Fact]
    public async Task AskFollowUpAsync_BudgetSpentAfterAPatientSwitch_RefusesWithoutDispatchingForEitherPatient()
    {
        // the resume check and the budget both guard this turn: the budget refuses first, so neither the
        // stale patient's state nor the new patient's gets a tool call billed.
        var sut = Coordinator(new ConversationBudgetOptions { MaxTurnsPerWindow = 2 });
        await sut.RequestBriefAsync(SessionId, SessionFor("pt-A"), CancellationToken.None);
        await sut.AskFollowUpAsync(SessionId, SessionFor("pt-A"), "first?", CancellationToken.None);
        _dispatchedPatientIds.Clear();

        var act = () => sut.AskFollowUpAsync(SessionId, SessionFor("pt-B"), "after the switch?", CancellationToken.None);

        await act.Should().ThrowAsync<ConversationTurnLimitExceededException>();
        _dispatchedPatientIds.Should().BeEmpty();
    }
}
