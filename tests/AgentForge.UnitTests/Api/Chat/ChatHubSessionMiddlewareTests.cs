using AgentForge.Api.Chat;
using AgentForge.Api.Session;
using AgentForge.UnitTests.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentForge.UnitTests.Api.Chat;

/// <summary>
/// The hub's identity source: session state is resolved once per hub request, by
/// middleware that runs after <c>UseSession()</c>, into <see cref="HttpContext.Items"/> - the one
/// part of the request SignalR copies onto the context it hands a long-polling hub.
/// </summary>
public sealed class ChatHubSessionMiddlewareTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly PatientSessionContext LiveSession =
        new("token-abc", "default", "123", "dr-jones", Now.AddHours(1));

    private readonly FixedTimeProvider _timeProvider = new(Now);

    [Theory]
    [InlineData("/hubs/chat")]
    [InlineData("/hubs/chat/negotiate")]
    public async Task InvokeAsync_HubRequestWithALivePatientSession_ResolvesTheSessionAndItsIdIntoItems(string path)
    {
        var httpContext = BuildHttpContext(path, SessionHolding(LiveSession, "session-xyz"));

        await InvokeAsync(httpContext);

        ChatHubSessionItems.TryGet(httpContext, out var sessionId, out var session).Should().BeTrue();
        sessionId.Should().Be("session-xyz");
        session.Should().Be(LiveSession);
    }

    [Fact]
    public async Task InvokeAsync_HubRequestFromABrowserThatNeverLaunched_ResolvesTheSessionIdButNoPatientContext()
    {
        // The session middleware attaches an empty session to every request; that is an un-launched
        // browser, and the hub must still be able to tell it apart from a missing resolution.
        var httpContext = BuildHttpContext("/hubs/chat", SessionHolding(session: null, "session-xyz"));

        await InvokeAsync(httpContext);

        ChatHubSessionItems.TryGet(httpContext, out var sessionId, out var session).Should().BeTrue();
        sessionId.Should().Be("session-xyz");
        session.Should().BeNull();
    }

    [Fact]
    public async Task InvokeAsync_HubRequestWhoseTokenHasAlreadyExpired_ResolvesNoPatientContext()
    {
        // Expiry is decided by the same TryGetPatientSession every HTTP surface uses, so an aged-out
        // session reaches the hub exactly as it did when the hub read the session itself. A separate change
        var expired = LiveSession with { ExpiresAt = Now.AddMinutes(-1) };
        var httpContext = BuildHttpContext("/hubs/chat", SessionHolding(expired, "session-xyz"));

        await InvokeAsync(httpContext);

        ChatHubSessionItems.TryGet(httpContext, out _, out var session).Should().BeTrue();
        session.Should().BeNull();
    }

    [Theory]
    [InlineData("/patient")]
    [InlineData("/hubs/chatter")]
    [InlineData("/")]
    public async Task InvokeAsync_RequestOutsideTheHubRoute_NeitherTouchesTheSessionNorResolvesAnything(string path)
    {
        // No ISessionFeature at all: reading HttpContext.Session here would throw, so passing proves
        // the middleware stays out of every other surface's way.
        var httpContext = BuildHttpContext(path, session: null);

        await InvokeAsync(httpContext);

        ChatHubSessionItems.TryGet(httpContext, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("/hubs/chat")]
    [InlineData("/patient")]
    public async Task InvokeAsync_AnyRequest_HandsTheRequestOnToTheRestOfThePipeline(string path)
    {
        var httpContext = BuildHttpContext(path, SessionHolding(LiveSession, "session-xyz"));
        var nextRan = false;

        await new ChatHubSessionMiddleware(_ => { nextRan = true; return Task.CompletedTask; }, _timeProvider)
            .InvokeAsync(httpContext);

        nextRan.Should().BeTrue();
    }

    [Fact]
    public void TryGet_ItemsCarryLookalikeStringKeys_ResolvesNothing()
    {
        // Only this type can write the entries the hub trusts: anything else putting a session-shaped
        // value into Items under a guessable name must not authenticate a connection.
        var httpContext = new DefaultHttpContext();
        httpContext.Items["patient-session.id"] = "session-xyz";
        httpContext.Items["patient-session.context"] = LiveSession;

        ChatHubSessionItems.TryGet(httpContext, out _, out _).Should().BeFalse();
    }

    private Task InvokeAsync(HttpContext httpContext) =>
        new ChatHubSessionMiddleware(_ => Task.CompletedTask, _timeProvider).InvokeAsync(httpContext);

    private static InMemoryTestSession SessionHolding(PatientSessionContext? session, string sessionId)
    {
        var inMemorySession = new InMemoryTestSession(sessionId);
        if (session is not null)
        {
            inMemorySession.SavePatientSession(session);
        }

        return inMemorySession;
    }

    private static DefaultHttpContext BuildHttpContext(string path, ISession? session)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (session is not null)
        {
            httpContext.Features.Set<ISessionFeature>(new TestSessionFeature(session));
        }

        return httpContext;
    }

    private sealed class TestSessionFeature(ISession session) : ISessionFeature
    {
        public ISession Session { get; set; } = session;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
