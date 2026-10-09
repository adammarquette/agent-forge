using FluentAssertions;
using AgentForge.Api.Agenda;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.UnitTests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// FR-OBS-1 / NFR-TRACE-1: every request gets one correlation id, present on every log line
/// written while it is handled, so a full trace is reconstructable from logs alone. Before this
/// middleware existed, <c>ChatSessionCoordinator</c> held the only <c>BeginScope</c> in the
/// repository and the non-chat endpoints (<c>/agenda</c>, <c>/agenda/select-patient</c>,
/// <c>/patient</c>) logged uncorrelated.
/// <para>
/// Every assertion about the id itself reads it <em>inside</em> the pipeline: the id is ambient to
/// the request's logical flow and deliberately does not leak back out to whatever invoked it.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddlewareTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public CorrelationIdMiddlewareTests() =>
        _loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(_logs));

    [Fact]
    public async Task InvokeAsync_RequestWithoutAnInboundHeader_MintsAnIdAndScopesDownstreamLogging()
    {
        // Given a request carrying no correlation id of its own,
        var context = RequestFor("/agenda");
        string? idDownstream = null;

        // when the middleware handles it,
        await InvokeAsync(context, accessor =>
        {
            idDownstream = accessor.CorrelationId;
            LogProbe();
        });

        // then an id is minted and the line written downstream carries it.
        idDownstream.Should().NotBeNullOrWhiteSpace();
        _logs.Lines.Should().ContainSingle(line =>
            line.Contains("probe", StringComparison.Ordinal)
            && line.Contains($"CorrelationId={idDownstream}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_RequestWithAWellFormedInboundHeader_AdoptsItSoTheCallersTraceContinues()
    {
        // Given a caller that already started a trace and passed its id in,
        var context = RequestFor("/patient");
        context.Request.Headers[CorrelationIdHandler.HeaderName] = "caller-trace-id_9.8-7";
        string? idDownstream = null;

        // when the middleware handles the request,
        await InvokeAsync(context, accessor =>
        {
            idDownstream = accessor.CorrelationId;
            LogProbe();
        });

        // then that id is adopted rather than a fresh trace started.
        idDownstream.Should().Be("caller-trace-id_9.8-7");
        _logs.Lines.Should().ContainSingle(line =>
            line.Contains("CorrelationId=caller-trace-id_9.8-7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("semi;colon")]
    [InlineData("{braces}")]
    public async Task InvokeAsync_RequestWithAMalformedInboundHeader_IgnoresItAndMintsInstead(string inbound)
    {
        // Given an inbound value that is not a well-formed correlation id - whitespace and control
        // characters would forge a second record inside one log line, and the value is caller-supplied.
        var context = RequestFor("/agenda");
        context.Request.Headers[CorrelationIdHandler.HeaderName] = inbound;
        string? idDownstream = null;

        // when the middleware handles the request,
        await InvokeAsync(context, accessor =>
        {
            idDownstream = accessor.CorrelationId;
            LogProbe();
        });

        // then the value is discarded and an id of our own is minted.
        idDownstream.Should().NotBe(inbound);
        idDownstream.Should().NotBeNullOrWhiteSpace();
        _logs.Lines.Should().ContainSingle(line =>
            line.Contains($"CorrelationId={idDownstream}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_InboundHeaderLongerThanTheAllowedLength_IgnoresItAndMintsInstead()
    {
        // An unbounded inbound id is a log-cardinality lever the caller should not have.
        var overlong = new string('a', CorrelationIdMiddleware.MaxInboundLength + 1);
        var context = RequestFor("/agenda");
        context.Request.Headers[CorrelationIdHandler.HeaderName] = overlong;
        string? idDownstream = null;

        await InvokeAsync(context, accessor => idDownstream = accessor.CorrelationId);

        idDownstream.Should().NotBe(overlong);
        idDownstream.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvokeAsync_InboundHeaderExactlyAtTheAllowedLength_IsStillAdopted()
    {
        var atLimit = new string('a', CorrelationIdMiddleware.MaxInboundLength);
        var context = RequestFor("/agenda");
        context.Request.Headers[CorrelationIdHandler.HeaderName] = atLimit;
        string? idDownstream = null;

        await InvokeAsync(context, accessor => idDownstream = accessor.CorrelationId);

        idDownstream.Should().Be(atLimit);
    }

    [Fact]
    public async Task InvokeAsync_RosterRejectionOnSelectPatient_TheEndpointsOwnLogEntryCarriesTheId()
    {
        // UC-6 fans one orchestrator turn out per rostered patient, so the agenda endpoints are
        // where an uncorrelated line costs most: AgendaEndpointsLog.PatientNotInRoster is an
        // FR-AUTH-3 refusal that previously could not be tied to the request that caused it.
        var context = RequestFor("/agenda/select-patient");
        string? idDownstream = null;

        await InvokeAsync(context, accessor =>
        {
            idDownstream = accessor.CorrelationId;
            AgendaEndpointsLog.PatientNotInRoster(
                _loggerFactory.CreateLogger(nameof(AgendaEndpoints)), "dr-jones");
        });

        _logs.Lines.Should().ContainSingle(line =>
            line.Contains("not part of their own agenda roster", StringComparison.Ordinal)
            && line.Contains($"CorrelationId={idDownstream}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_ChatHubRequest_OpensNoScopeSoTheCoordinatorsPerTurnScopeIsNotDoubled()
    {
        // A hub connection is one long-lived request whose execution context every later
        // invocation inherits, while ChatSessionCoordinator opens a scope per turn. A scope here
        // would nest a second, connection-wide CorrelationId under every turn's own id.
        var context = RequestFor(ChatHub.Route);

        await InvokeAsync(context, _ => LogProbe());

        _logs.Lines.Should().ContainSingle(line =>
            line.Contains("probe", StringComparison.Ordinal)
            && !line.Contains("CorrelationId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_ChatHubNegotiateRequest_AlsoLeavesTheScopeToTheCoordinator()
    {
        var context = RequestFor(ChatHub.Route + "/negotiate");

        await InvokeAsync(context, _ => LogProbe());

        _logs.Lines.Should().ContainSingle(line => !line.Contains("CorrelationId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_RequestInFlight_TheIdIsReadableByCollaboratorsTheMiddlewareNeverTouched()
    {
        // The id has to be established for the whole logical flow, not just handed to one object:
        // CorrelationIdHandler is built by IHttpClientFactory in its own scope and still has to
        // stamp X-Correlation-Id with this request's id (NFR-TRACE-1).
        var context = RequestFor("/agenda");
        string? idFromTheRequestsAccessor = null;
        string? idFromAnUnrelatedInstance = null;

        await InvokeAsync(context, accessor =>
        {
            idFromTheRequestsAccessor = accessor.CorrelationId;
            idFromAnUnrelatedInstance = new MutableCorrelationIdAccessor().CorrelationId;
        });

        idFromAnUnrelatedInstance.Should().Be(idFromTheRequestsAccessor);
    }

    [Fact]
    public async Task InvokeAsync_TwoRequestsInARow_EachGetsItsOwnId()
    {
        // Guards the mint itself: two requests handed the same value would mean a constant rather
        // than a fresh id per invocation. (It does *not* guard inheritance - an id set inside one
        // InvokeAsync never escapes back to this flow; the test below is the one that does.)
        string? first = null;
        string? second = null;

        await InvokeAsync(RequestFor("/agenda"), accessor => first = accessor.CorrelationId);
        await InvokeAsync(RequestFor("/agenda"), accessor => second = accessor.CorrelationId);

        second.Should().NotBe(first);
    }

    [Fact]
    public async Task InvokeAsync_TheFlowAlreadyCarriesAnId_MintsAFreshOneRatherThanContinuingIt()
    {
        // Ingress mints explicitly instead of leaning on the accessor's lazy read: an id already
        // ambient on the execution context this request is served from would otherwise be adopted
        // silently, and two requests would share one trace.
        _ = new MutableCorrelationIdAccessor { CorrelationId = "left-over-from-another-request" };
        string? idDownstream = null;

        await InvokeAsync(RequestFor("/agenda"), accessor => idDownstream = accessor.CorrelationId);

        idDownstream.Should().NotBe("left-over-from-another-request");
        idDownstream.Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose() => _loggerFactory.Dispose();

    private static DefaultHttpContext RequestFor(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    private Task InvokeAsync(HttpContext context, Action<MutableCorrelationIdAccessor> downstream)
    {
        var accessor = new MutableCorrelationIdAccessor();
        return new CorrelationIdMiddleware(
            _ =>
            {
                downstream(accessor);
                return Task.CompletedTask;
            },
            _loggerFactory.CreateLogger<CorrelationIdMiddleware>())
            .InvokeAsync(context, accessor);
    }

    private void LogProbe() =>
        _loggerFactory.CreateLogger("Downstream")
            .Log(LogLevel.Information, new EventId(0), "probe", null, (state, _) => state);
}
