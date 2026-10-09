using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.UnitTests.TestSupport;
using AgentForge.Verification;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Api.Chat;

public sealed class ChatSessionCoordinatorTests
{
    private readonly IAgentOrchestrator _orchestrator = A.Fake<IAgentOrchestrator>();
    private readonly IConversationStateStore _conversationStore = A.Fake<IConversationStateStore>();
    private readonly IChatMessageOutbox _outbox = A.Fake<IChatMessageOutbox>();
    private readonly IScopedAccessTokenProvider _tokenProvider = A.Fake<IScopedAccessTokenProvider>();
    private readonly IScopedClinicianIdentityAccessor _clinicianIdentityAccessor = A.Fake<IScopedClinicianIdentityAccessor>();
    private readonly ICorrelationIdAccessor _correlationIdAccessor = A.Fake<ICorrelationIdAccessor>();
    private readonly IDerivedFactStore _factStore = A.Fake<IDerivedFactStore>();
    private readonly CapturingLogger<ChatSessionCoordinator> _logger = new();
    private readonly CapturingLogger<AccessAudit> _auditLogger = new();
    private readonly IConversationTurnBudget _turnBudget = A.Fake<IConversationTurnBudget>();
    private readonly ChatSessionCoordinator _sut;
    private readonly PatientSessionContext _session = new("token-abc", "default", "123", "dr-jones", DateTimeOffset.UtcNow.AddHours(1));

    public ChatSessionCoordinatorTests()
    {
        A.CallTo(() => _correlationIdAccessor.CorrelationId).Returns("corr-1");
        A.CallTo(() => _turnBudget.TryConsume(A<string>._)).Returns(true);
        _sut = new ChatSessionCoordinator(
            _orchestrator, _conversationStore, _outbox, _tokenProvider, _clinicianIdentityAccessor, _correlationIdAccessor, _logger, _auditLogger, _turnBudget, _factStore);
    }

    // ILogger.Log directly rather than the LogInformation extension, which CA1848 forbids under
    // warnings-as-errors. The point is only to emit a line while the turn's scope is open.
    private void WriteLine(string message) =>
        _logger.Log(LogLevel.Information, default, message, null, static (state, _) => state);

    [Fact]
    public async Task RequestBriefAsync_WhileTheTurnRuns_LogLinesCarryTheConversationId()
    {
        // The correlation id spans one hub invocation. Without a second key on the same scope,
        // nothing in a log line says which conversation the turn belonged to - a real gap
        // once. Asserted through a line written *inside* the turn, because a
        // scope that is opened but never inherited would satisfy a weaker test.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Invokes(() => WriteLine("inside the turn"))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        _logger.Lines.Should().ContainSingle(line => line.Contains("inside the turn"))
            .Which.Should().Contain($"ConversationId={ConversationId.From("session-1")}");
    }

    [Fact]
    public async Task AskFollowUpAsync_TurnsOnOneSession_ShareAConversationIdWhileCorrelationIdsDiffer()
    {
        // This is the finding itself: two turns of one chat must be joinable, and the id that
        // joins them must not be the correlation id, which is minted per invocation. A change
        // that reused one correlation id across turns would pass a same-id assertion while
        // breaking per-turn tracing, so both halves are asserted together.
        A.CallTo(() => _correlationIdAccessor.CorrelationId).ReturnsNextFromSequence("corr-1", "corr-2");
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>?>._))
            .Invokes(() => WriteLine("turn ran"))
            .Returns(Task.FromResult(new AgentTurnResult("answer", ConversationState.Start("default", "123"), [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "first?", CancellationToken.None);
        await _sut.AskFollowUpAsync("session-1", _session, "second?", CancellationToken.None);

        var turns = _logger.Lines.Where(line => line.Contains("turn ran")).ToList();
        turns.Should().HaveCount(2);
        var expected = $"ConversationId={ConversationId.From("session-1")}";
        turns.Should().OnlyContain(line => line.Contains(expected));
        turns[0].Should().Contain("CorrelationId=corr-1");
        turns[1].Should().Contain("CorrelationId=corr-2");
    }

    [Fact]
    public async Task RequestBriefAsync_AnySession_NeverLogsTheRawSessionId()
    {
        // Session.Id is the server-side session key; logging it would put a credential-shaped
        // value in the same index as everything else, and no_phi_in_logs scores PHI tokens, not
        // secrets. The conversation id is derived from it precisely so this stays true.
        const string sessionId = "hqGmZ7nQ4VdKpL2rXtY8wBfE";
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Invokes(() => WriteLine("inside the turn"))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync(sessionId, _session, CancellationToken.None);

        _logger.Lines.Should().NotBeEmpty();
        _logger.Lines.Should().NotContain(line => line.Contains(sessionId));
    }

    [Fact]
    public async Task RequestBriefAsync_ValidSession_SetsScopedAccessTokenBeforeCallingTheOrchestrator()
    {
        // The token must be in place before the orchestrator (and everything it calls
        // transitively down to the FHIR client) runs, not after - this is the mechanism that
        // gets the session's token onto outbound OpenEMR calls without ever touching the browser.
        var tokenAdoptedBeforeOrchestratorCall = false;
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Invokes(() => tokenAdoptedBeforeOrchestratorCall = Fake.GetCalls(_tokenProvider)
                .Any(call => call.Method.Name == nameof(IScopedAccessTokenProvider.Adopt)))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        tokenAdoptedBeforeOrchestratorCall.Should().BeTrue();
        // With its expiry, not alone: the orchestrator's FHIR reads are exactly the calls that went
        // on being made with a dead token for the rest of a session.
        A.CallTo(() => _tokenProvider.Adopt("token-abc", _session.ExpiresAt)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RequestBriefAsync_ValidSession_ScopesDownstreamLoggingWithTheCorrelationIdBeforeCallingTheOrchestrator()
    {
        // CONVENTIONS.md Sec.7: correlation id flows as a logging scope on every
        // downstream call - the orchestrator and everything it calls transitively (tool dispatch,
        // LLM call, verification) must be able to pick it up without it being threaded as an
        // explicit parameter through every one of those layers.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Invokes(() => _logger.Log(LogLevel.Information, new EventId(0), "probe", null, (state, _) => state))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        _logger.Lines.Should().ContainSingle(line => line.Contains("probe", StringComparison.Ordinal) && line.Contains("CorrelationId=corr-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestBriefAsync_ValidSession_SetsScopedClinicianIdentityBeforeCallingTheOrchestrator()
    {
        // FR-AUTH-4: the clinician identity must be in scope before any tool call runs, so the
        // MCP audit log can attribute every access this turn makes to who actually made it.
        string? identityDuringOrchestratorCall = null;
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Invokes(() => identityDuringOrchestratorCall = _clinicianIdentityAccessor.ClinicianIdentity)
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        identityDuringOrchestratorCall.Should().Be("dr-jones");
    }

    [Fact]
    public async Task RequestBriefAsync_OrchestratorReturnsAResult_SavesItsStateKeyedBySessionId()
    {
        var finalState = ConversationState.Start("default", "123") with
        {
            Messages = [LlmMessage.FromText(LlmRole.Assistant, "brief text")],
        };
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", finalState, [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        A.CallTo(() => _conversationStore.Save("session-1", finalState)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RequestBriefAsync_OrchestratorReturnsAResult_AppendsABriefMessageToTheOutboxAndReturnsIt()
    {
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        var appended = new ChatMessage(1, "brief", """{"answer":"brief text"}""");
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>.That.Contains("brief text")))
            .Returns(appended);

        var result = await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        result.Should().Be(appended);
    }

    [Fact]
    public async Task AskFollowUpAsync_NoPriorStateSaved_StartsAFreshConversationScopedToTheSessionsPatient()
    {
        A.CallTo(() => _conversationStore.TryGet("session-1")).Returns(null);
        A.CallTo(() => _orchestrator.AskFollowUpAsync(A<ConversationState>._, "Is her INR therapeutic?", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("Yes.", ConversationState.Start("default", "123"), [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "Is her INR therapeutic?", CancellationToken.None);

        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>.That.Matches(s => s.Site == "default" && s.PatientId == "123" && s.Messages.Count == 0),
                "Is her INR therapeutic?", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AskFollowUpAsync_PriorStateSaved_ResumesFromIt()
    {
        var priorState = ConversationState.Start("default", "123") with
        {
            Messages = [LlmMessage.FromText(LlmRole.User, "Is her INR therapeutic?")],
        };
        A.CallTo(() => _conversationStore.TryGet("session-1")).Returns(priorState);
        A.CallTo(() => _orchestrator.AskFollowUpAsync(priorState, "When was it drawn?", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("Last week.", priorState, [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "When was it drawn?", CancellationToken.None);

        A.CallTo(() => _orchestrator.AskFollowUpAsync(priorState, "When was it drawn?", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AskFollowUpAsync_PriorStateSavedForAnotherPatient_StartsAFreshConversationWithoutItsHistory()
    {
        // Re-binding the old state to the new patient would still hand the model the previous patient's
        // tool results as history, so a patient switch must start over, not just swap the id.
        var otherPatientsState = ConversationState.Start("default", "999") with
        {
            Messages = [LlmMessage.FromText(LlmRole.User, "Is his INR therapeutic?")],
        };
        A.CallTo(() => _conversationStore.TryGet("session-1")).Returns(otherPatientsState);
        A.CallTo(() => _orchestrator.AskFollowUpAsync(A<ConversationState>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("Yes.", ConversationState.Start("default", "123"), [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "Is her INR therapeutic?", CancellationToken.None);

        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>.That.Matches(s => s.Site == "default" && s.PatientId == "123" && s.Messages.Count == 0),
                "Is her INR therapeutic?", A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task AskFollowUpAsync_OrchestratorReturnsAResult_AppendsAnAnswerMessageToTheOutbox()
    {
        A.CallTo(() => _conversationStore.TryGet("session-1")).Returns(null);
        A.CallTo(() => _orchestrator.AskFollowUpAsync(A<ConversationState>._, A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("Yes.", ConversationState.Start("default", "123"), [], [])));
        var appended = new ChatMessage(2, "answer", """{"answer":"Yes."}""");
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "answer", A<string>._)).Returns(appended);

        var result = await _sut.AskFollowUpAsync("session-1", _session, "Is her INR therapeutic?", CancellationToken.None);

        result.Should().Be(appended);
    }

    [Fact]
    public async Task RequestBriefAsync_OrchestratorReturnsSafetyFlags_IncludesThemInTheOutboxPayload()
    {
        // ARCHITECTURE.md's request-flow sequence diagram sends safety flags to the BFF alongside
        // the brief, not just into a log line - this proves they actually reach the wire payload.
        var flag = new DomainConstraintFlag("inr-therapeutic-range", "INR out of range", [new ClinicalSourceRef("Observation", "1")]);
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [flag], [])));
        string? capturedPayload = null;
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>._))
            .Invokes((string _, ChatPatientScope _, string _, string payload) => capturedPayload = payload)
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        capturedPayload.Should().Contain("inr-therapeutic-range");
        capturedPayload.Should().Contain("Observation/1");
    }

    [Fact]
    public async Task RequestBriefAsync_OrchestratorReturnsSuppressedClaims_IncludesThemInTheOutboxPayload()
    {
        // REQUIREMENTS.md Sec.13.1's "Claim can't be grounded" row: "suppressed items noted" - this proves a
        // suppressed claim actually reaches the wire payload alongside the (shorter) verified
        // answer, not just a log line, the same way safety flags do above.
        var suppressed = new SuppressedClaim("Her INR is 9.0.", "no citation");
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [suppressed])));
        string? capturedPayload = null;
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>._))
            .Invokes((string _, ChatPatientScope _, string _, string payload) => capturedPayload = payload)
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        capturedPayload.Should().Contain("Her INR is 9.0.");
        capturedPayload.Should().Contain("no citation");
    }

    [Fact]
    public async Task RequestBriefAsync_OrchestratorReturnsADeterministicFallback_IncludesTheFlagInTheOutboxPayload()
    {
        // REQUIREMENTS.md §13.1: the BFF/UI must be able to tell a graceful-degradation answer apart from
        // a normal synthesized one, so it can show "Summary unavailable right now - here is the
        // source data" instead of presenting fallback text as if the model actually said it.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult(
                "raw data", ConversationState.Start("default", "123"), [], [], IsDeterministicFallback: true)));
        string? capturedPayload = null;
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>._))
            .Invokes((string _, ChatPatientScope _, string _, string payload) => capturedPayload = payload)
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        capturedPayload.Should().Contain("\"IsDeterministicFallback\":true");
    }

    [Fact]
    public void Resume_Always_DelegatesToTheOutboxForTheSessionsCurrentSiteAndPatient()
    {
        var messages = new List<ChatMessage> { new(3, "answer", "{}") };
        A.CallTo(() => _outbox.GetSince("session-1", new ChatPatientScope("default", "123"), 2)).Returns(messages);

        var result = _sut.Resume("session-1", _session, 2);

        result.Should().BeSameAs(messages);
    }

    [Fact]
    public async Task RequestBriefAsync_PatientHasIngestedDocumentFacts_IncludesTheirClickToSourceCitationsInThePayload()
    {
        // UC-9/FR-CITE-2: the brief surfaces facts ingested before the visit, and the client needs each
        // fact's source-document id + region on the wire to open the PDF and highlight it.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        var fact = new DerivedFact
        {
            Id = Guid.Parse("abcd1234-0000-0000-0000-000000000000"),
            FactType = "lab.result",
            PayloadJson = "{}",
            Citation = new Citation { SourceId = "cite-1", QuoteOrValue = "Potassium 5.9 (H) mmol/L", PageOrSection = "2", BoundingBox = [0.1, 0.2, 0.3, 0.05] },
            Document = new IngestedDocument { PatientId = "123", ContentHash = "hash-1", OpenEmrDocumentReferenceId = "docref-1" },
        };
        A.CallTo(() => _factStore.GetByPatientAsync("123", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<DerivedFact>>([fact]));
        string? capturedPayload = null;
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>._))
            .Invokes((string _, ChatPatientScope _, string _, string payload) => capturedPayload = payload)
            .Returns(new ChatMessage(1, "brief", "{}"));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        capturedPayload.Should().Contain("DocumentCitations").And.Contain("docref-1").And.Contain("abcd1234");
    }

    [Fact]
    public async Task RequestBriefAsync_NoDocumentStoreWired_StillProducesABriefWithEmptyDocumentCitations()
    {
        // Week 2 is optional (Program.cs gates the store on a configured database); the Week 1 chat must still
        // boot and run without it. Guards the DI regression where the coordinator required IDerivedFactStore
        // and broke the no-database (integration/Week-1) host. A separate change.
        var sut = new ChatSessionCoordinator(
            _orchestrator, _conversationStore, _outbox, _tokenProvider, _clinicianIdentityAccessor, _correlationIdAccessor, _logger, _auditLogger, _turnBudget);
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        string? capturedPayload = null;
        A.CallTo(() => _outbox.Append("session-1", A<ChatPatientScope>._, "brief", A<string>._))
            .Invokes((string _, ChatPatientScope _, string _, string payload) => capturedPayload = payload)
            .Returns(new ChatMessage(1, "brief", "{}"));

        await sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        capturedPayload.Should().Contain("brief text").And.Contain("\"DocumentCitations\":[]");
    }

    [Fact]
    public async Task RequestBriefAsync_DocumentStoreWired_RecordsOneCitationReadInTheAccessAuditTrail()
    {
        // FR-AUTH-4: the citation load reads the session patient's derived document facts and sends
        // their values to the client, and no tool call in the turn is guaranteed to have audited that read.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        _auditLogger.Lines.Should().ContainSingle().Which.Should().Contain(
            "clinician=dr-jones accessed patient=123 via tool=chat_document_citations correlation=corr-1");
    }

    [Fact]
    public async Task AskFollowUpAsync_DocumentStoreWired_RecordsOneCitationReadInTheAccessAuditTrail()
    {
        // Every follow-up reloads the citations, so every follow-up is its own audited read.
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>?>._))
            .Returns(Task.FromResult(new AgentTurnResult("answer", ConversationState.Start("default", "123"), [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "potassium?", CancellationToken.None);

        _auditLogger.Lines.Should().ContainSingle().Which.Should().Contain(
            "clinician=dr-jones accessed patient=123 via tool=chat_document_citations correlation=corr-1");
    }

    [Fact]
    public async Task RequestBriefAsync_DocumentStoreReadFails_HasAlreadyRecordedTheAttempt()
    {
        // Audited before the read, as DocumentFactsTool is: a failed read is still an access attempt.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        A.CallTo(() => _factStore.GetByPatientAsync("123", A<CancellationToken>._))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var act = () => _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _auditLogger.Lines.Should().ContainSingle().Which.Should().Contain("via tool=chat_document_citations");
    }

    [Fact]
    public async Task RequestBriefAsync_PatientHasIngestedDocumentFacts_NeverWritesADocumentIdOrValueToTheAuditTrail()
    {
        // Ids only: the audit record names clinician, patient, tool and correlation, never the facts it read.
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));
        var fact = new DerivedFact
        {
            Id = Guid.Parse("abcd1234-0000-0000-0000-000000000000"),
            FactType = "lab.result",
            PayloadJson = "{}",
            Citation = new Citation { SourceId = "cite-1", QuoteOrValue = "Potassium 5.9 (H) mmol/L", PageOrSection = "2", BoundingBox = [0.1, 0.2, 0.3, 0.05] },
            Document = new IngestedDocument { PatientId = "123", ContentHash = "hash-1", OpenEmrDocumentReferenceId = "docref-1" },
        };
        A.CallTo(() => _factStore.GetByPatientAsync("123", A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<DerivedFact>>([fact]));

        await _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        _auditLogger.Lines.Should().ContainSingle().Which.Should()
            .NotContain("docref-1").And.NotContain("cite-1").And.NotContain("Potassium").And.NotContain("abcd1234");
    }

    [Fact]
    public async Task RequestBriefAsync_NoDocumentStoreWired_RecordsNoCitationRead()
    {
        // Nothing is read without a store, so there is nothing to audit; an unconditional line would claim a
        // read that never happened.
        var sut = new ChatSessionCoordinator(
            _orchestrator, _conversationStore, _outbox, _tokenProvider, _clinicianIdentityAccessor, _correlationIdAccessor, _logger, _auditLogger, _turnBudget);
        A.CallTo(() => _orchestrator.StartBriefAsync("default", "123", A<CancellationToken>._))
            .Returns(Task.FromResult(new AgentTurnResult("brief text", ConversationState.Start("default", "123"), [], [])));

        await sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        _auditLogger.Lines.Should().BeEmpty();
    }
    [Fact]
    public async Task AskFollowUpAsync_ConversationBudgetExhausted_RefusesWithoutBillingAnLlmTurn()
    {
        // The cost cap: the refusal has to land before the orchestrator, or the capped turn is still paid for.
        A.CallTo(() => _turnBudget.TryConsume("session-1")).Returns(false);

        var act = () => _sut.AskFollowUpAsync("session-1", _session, "again?", CancellationToken.None);

        await act.Should().ThrowAsync<ConversationTurnLimitExceededException>();
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>?>._))
            .MustNotHaveHappened();
        A.CallTo(() => _outbox.Append(A<string>._, A<ChatPatientScope>._, A<string>._, A<string>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RequestBriefAsync_ConversationBudgetExhausted_RefusesWithoutBillingAnLlmTurn()
    {
        // The brief is a turn too; re-requesting it must not be a way around the cap.
        A.CallTo(() => _turnBudget.TryConsume("session-1")).Returns(false);

        var act = () => _sut.RequestBriefAsync("session-1", _session, CancellationToken.None);

        await act.Should().ThrowAsync<ConversationTurnLimitExceededException>();
        A.CallTo(() => _orchestrator.StartBriefAsync(A<string>._, A<string>._, A<CancellationToken>._, A<IProgress<string>?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task AskFollowUpAsync_EachTurn_ChargesTheSessionsBudgetOnce()
    {
        // Charged against the session id, the key the conversation state and outbox use too.
        A.CallTo(() => _orchestrator.AskFollowUpAsync(
                A<ConversationState>._, A<string>._, A<CancellationToken>._, A<IProgress<string>?>._))
            .Returns(Task.FromResult(new AgentTurnResult("answer", ConversationState.Start("default", "123"), [], [])));

        await _sut.AskFollowUpAsync("session-1", _session, "first?", CancellationToken.None);

        A.CallTo(() => _turnBudget.TryConsume("session-1")).MustHaveHappenedOnceExactly();
    }
}
