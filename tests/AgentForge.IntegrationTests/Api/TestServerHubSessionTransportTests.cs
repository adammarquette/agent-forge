using System.Globalization;
using System.Net;
using AgentForge.Agent;
using AgentForge.Api.Chat;
using AgentForge.Api.Observability;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.IntegrationTests.Support;
using AgentForge.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static AgentForge.IntegrationTests.Support.TaskWaitHelper;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Guards how a hub on the in-process TestServer learns who is connected, over every transport.
/// The probe test guards the property the in-process hub tests were first built on: that under
/// WebSockets the <c>HttpContext</c> SignalR hands a hub still carries the ASP.NET Core session -
/// its absence put <see cref="CorrelationIdTraceReconstructionTests"/> and four siblings behind a
/// <c>Skip</c>. The per-transport tests guard what replaced that dependency in
/// <c>ChatHub</c>: its identity comes from <see cref="ChatHubSessionMiddleware"/> through
/// <c>HttpContext.Items</c>, which every transport carries.
/// Deliberately runs its own minimal hosts rather than <see cref="BffQaFixture"/>: the property
/// under test is the transport, so this must stay runnable with no QA deployment, no OpenEMR and
/// no LLM key - the exact conditions under which the original defect went uninvestigated for
/// weeks.
/// </summary>
// Selects this class into the merge-request job integration-tests-no-deployment. A separate change
[Trait("Deployment", "None")]
public sealed class TestServerHubSessionTransportTests
{
    private const string ProbeRoute = "/hubs/session-probe";
    private const string SeedRoute = "/test-only/seed-probe-session";
    private const string SessionKey = "session-transport-probe";
    private const string SeededValue = "value-the-hub-must-still-see";

    [Fact]
    public async Task Build_ConnectedToAHubOnTheTestServer_HandsTheHubAnHttpContextThatStillCarriesTheSession()
    {
        using var host = await StartProbeHostAsync();
        var server = host.GetTestServer();
        var probe = host.Services.GetRequiredService<SessionProbe>();
        var cookies = await SeedSessionAsync(server);

        await using var connection = TestServerHubConnection.Build(server, cookies, ProbeRoute);
        await connection.StartAsync(CancellationToken.None);

        var observed = await WaitForAsync(probe.Observed.Task, TimeSpan.FromSeconds(30));

        observed.Should().Be(
            SeededValue,
            "a hub that cannot read the session it was connected with cannot authenticate anyone, and every " +
            "test that drives a real turn through the hub is dark until it can");
    }

    // The real ChatHub behind the real ChatHubSessionMiddleware, once per transport the SignalR
    // client can negotiate. Named failure mode (regression): under LongPolling the hub read
    // HttpContext.Session off a cloned context with no ISessionFeature and threw
    // InvalidOperationException, so the connection never started and the clinician got an opaque
    // failure instead of the refusal the #expired panel matches on. Separate changes
    public static TheoryData<HttpTransportType> EveryTransport =>
        new() { HttpTransportType.WebSockets, HttpTransportType.ServerSentEvents, HttpTransportType.LongPolling };

    [Theory]
    [MemberData(nameof(EveryTransport))]
    public async Task Resume_LaunchedSessionOverThisTransport_ReplaysThatSessionsOwnOutbox(HttpTransportType transport)
    {
        await using var chat = await ChatHubHost.StartAsync();
        var (cookies, sessionId) = await chat.SeedPatientSessionAsync(expiresIn: TimeSpan.FromHours(1));
        chat.Outbox.Append(sessionId, ChatHubHost.SeededPatient, "brief", "{}");
        chat.Outbox.Append("some-other-session", ChatHubHost.SeededPatient, "brief", "{}");

        await using var connection = TestServerHubConnection.Build(
            chat.Server, cookies, ChatHub.Route, transport, ChatHubHost.ContextKeyFor(sessionId));
        await connection.StartAsync(CancellationToken.None);
        var replayed = await connection.InvokeAsync<IReadOnlyList<ChatMessage>>("Resume", 0L, CancellationToken.None);

        replayed.Should().ContainSingle(
            "the hub must identify the connection as the seeded session - and only that one - over {0}", transport);
    }

    [Theory]
    [MemberData(nameof(EveryTransport))]
    public async Task Resume_BrowserThatNeverLaunchedOverThisTransport_IsRefusedWithTheMessageTheClientMatchesOn(
        HttpTransportType transport)
    {
        await using var chat = await ChatHubHost.StartAsync();

        await using var connection = TestServerHubConnection.Build(chat.Server, new CookieContainer(), ChatHub.Route, transport);
        await connection.StartAsync(CancellationToken.None);
        var act = () => connection.InvokeAsync<IReadOnlyList<ChatMessage>>("Resume", 0L, CancellationToken.None);

        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("No authenticated session");
    }

    [Theory]
    [MemberData(nameof(EveryTransport))]
    public async Task RequestBrief_TokenAgedOutAfterConnectingOverThisTransport_IsRefusedWithTheSessionExpiredMessage(
        HttpTransportType transport)
    {
        // the pre-turn refusal: the connection outlives the token, and the clinician must get
        // the re-launch message, not a generic transport error, whichever transport carried it.
        await using var chat = await ChatHubHost.StartAsync();
        var (cookies, sessionId) = await chat.SeedPatientSessionAsync(expiresIn: TimeSpan.FromMinutes(5));

        await using var connection = TestServerHubConnection.Build(
            chat.Server, cookies, ChatHub.Route, transport, ChatHubHost.ContextKeyFor(sessionId));
        await connection.StartAsync(CancellationToken.None);
        chat.Clock.Advance(TimeSpan.FromMinutes(6));
        var act = () => connection.InvokeAsync("RequestBrief", CancellationToken.None);

        (await act.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain(ChatHub.SessionExpiredMessage);
    }

    private static async Task<IHost> StartProbeHostAsync()
    {
        var host = new HostBuilder()
            // As Program.cs does in every environment: a missing registration fails here, naming the type,
            // rather than as a hub connection that closes before its first invoke. A separate change
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<SessionProbe>();
                    services.AddDistributedMemoryCache();
                    // The TestServer serves http, and RFC 6265 forbids replaying a Secure cookie
                    // over it - the same relaxation BffQaFixture makes against the real Program.
                    services.AddSession(options => options.Cookie.SecurePolicy = CookieSecurePolicy.None);
                    services.AddSignalR();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseSession();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<SessionProbeHub>(ProbeRoute);
                        endpoints.MapPost(SeedRoute, async context =>
                        {
                            context.Session.SetString(SessionKey, SeededValue);
                            await context.Session.CommitAsync(context.RequestAborted);
                            context.Response.StatusCode = StatusCodes.Status204NoContent;
                        });
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<CookieContainer> SeedSessionAsync(TestServer server)
    {
        var cookies = new CookieContainer();
        using var client = new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = server.CreateHandler() })
        {
            BaseAddress = server.BaseAddress,
        };

        using var response = await client.PostAsync(SeedRoute, content: null, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        cookies.GetCookieHeader(server.BaseAddress).Should().NotBeEmpty("the probe host must have issued a session cookie");

        return cookies;
    }
}

/// <summary>What <see cref="SessionProbeHub"/> saw, handed back to the test that is waiting on it.</summary>
internal sealed class SessionProbe
{
    public TaskCompletionSource<string> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Reads the session the way <c>ChatHub.OnConnectedAsync</c> did until a separate change - the
/// connection-establishing request's context, loaded once - and reports what it found instead of
/// throwing into SignalR's generic transport error, so a failure names the real cause.
/// </summary>
internal sealed class SessionProbeHub(SessionProbe probe) : Hub
{
    private const string SessionKey = "session-transport-probe";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        try
        {
            var httpContext = Context.GetHttpContext()
                ?? throw new InvalidOperationException("SignalR gave the hub no HttpContext at all.");

            await httpContext.Session.LoadAsync(Context.ConnectionAborted).ConfigureAwait(false);
            probe.Observed.TrySetResult(httpContext.Session.GetString(SessionKey) ?? "(the session carried no value)");
        }
        catch (Exception ex)
        {
            probe.Observed.TrySetException(ex);
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The production <see cref="ChatHub"/>, its <see cref="ChatSessionCoordinator"/> and the
/// <see cref="ChatHubSessionMiddleware"/> on a minimal in-process host, wired in the order
/// <c>Program.cs</c> uses (<c>UseSession()</c>, then the middleware, then the hub). Only the model
/// is absent: no test here reaches a turn, and <see cref="UnreachableOrchestrator"/> fails loudly
/// if one ever does. Synthetic session values only.
/// </summary>
internal sealed class ChatHubHost : IAsyncDisposable
{
    private const string SeedRoute = "/test-only/seed-chat-session";

    /// <summary>The site and patient every seeded session is launched for.</summary>
    public static readonly ChatPatientScope SeededPatient = new("default", "synthetic-patient");

    private readonly IHost _host;

    private ChatHubHost(IHost host)
    {
        _host = host;
    }

    public TestServer Server => _host.GetTestServer();

    public AdjustableClock Clock => _host.Services.GetRequiredService<AdjustableClock>();

    public IChatMessageOutbox Outbox => _host.Services.GetRequiredService<IChatMessageOutbox>();

    public static async Task<ChatHubHost> StartAsync()
    {
        var host = new HostBuilder()
            // As Program.cs does in every environment: a missing registration fails here, naming the type,
            // rather than as a hub connection that closes before its first invoke. A separate change
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            })
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<AdjustableClock>();
                    services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<AdjustableClock>());
                    services.AddDistributedMemoryCache();
                    // Same relaxation as the probe host above: the TestServer serves http.
                    services.AddSession(options => options.Cookie.SecurePolicy = CookieSecurePolicy.None);
                    services.AddSignalR();
                    services.AddRouting();
                    services.AddLogging();

                    services.AddSingleton<IAgentOrchestrator, UnreachableOrchestrator>();
                    services.AddSingleton<IConversationStateStore, InMemoryConversationStateStore>();
                    services.AddSingleton<IChatMessageOutbox, InMemoryChatMessageOutbox>();
                    services.AddScoped<IScopedAccessTokenProvider, ScopedAccessTokenProvider>();
                    services.AddScoped<IScopedClinicianIdentityAccessor, ScopedClinicianIdentityAccessor>();
                    services.AddScoped<MutableCorrelationIdAccessor>();
                    services.AddScoped<ICorrelationIdAccessor>(sp => sp.GetRequiredService<MutableCorrelationIdAccessor>());
                    services.AddSingleton<AgentForgeMetrics>();
                    services.AddSingleton<IAgentForgeMetrics>(sp => sp.GetRequiredService<AgentForgeMetrics>());
                    services.AddScoped<ExpiredSessionSignal>();
                    // The same extension Program.cs calls, so the budget cannot be wired differently here.
                    services.AddConversationTurnBudget(context.Configuration);
                    services.AddScoped<ChatSessionCoordinator>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseSession();
                    app.UseChatHubSession();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<ChatHub>(ChatHub.Route);
                        endpoints.MapPost(SeedRoute, async (HttpContext context, TimeProvider clock) =>
                        {
                            await context.Session.LoadAsync(context.RequestAborted);
                            var minutes = int.Parse(context.Request.Query["expiresInMinutes"].ToString(), CultureInfo.InvariantCulture);
                            context.Session.SavePatientSession(SeededSession(clock.GetUtcNow().AddMinutes(minutes)));
                            await context.Session.CommitAsync(context.RequestAborted);
                            await context.Response.WriteAsync(context.Session.Id, context.RequestAborted);
                        });
                    });
                }))
            .Build();

        await host.StartAsync();
        return new ChatHubHost(host);
    }

    /// <summary>
    /// Stands in for the SMART launch: saves a patient session expiring <paramref name="expiresIn"/>
    /// from the host clock, and returns the cookie jar carrying it plus its server-side id.
    /// </summary>
    public async Task<(CookieContainer Cookies, string SessionId)> SeedPatientSessionAsync(TimeSpan expiresIn)
    {
        var cookies = new CookieContainer();
        using var client = new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() })
        {
            BaseAddress = Server.BaseAddress,
        };

        var minutes = ((int)expiresIn.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        using var response = await client.PostAsync($"{SeedRoute}?expiresInMinutes={minutes}", content: null, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        var sessionId = await response.Content.ReadAsStringAsync(CancellationToken.None);

        return (cookies, sessionId);
    }

    /// <summary>The page key a page rendered for <paramref name="sessionId"/>'s seeded patient presents. A separate change</summary>
    public static string ContextKeyFor(string sessionId) =>
        PatientContextBinding.KeyFor(sessionId, SeededSession(DateTimeOffset.UnixEpoch));

    private static PatientSessionContext SeededSession(DateTimeOffset expiresAt) => new(
        "synthetic-token", SeededPatient.Site, SeededPatient.PatientId, "synthetic-clinician", expiresAt);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>A clock the test moves, so a token can age out while a connection stays open.</summary>
internal sealed class AdjustableClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>No transport test runs a turn; one that did would be testing something else.</summary>
internal sealed class UnreachableOrchestrator : IAgentOrchestrator
{
    public Task<AgentTurnResult> StartBriefAsync(
        string site, string patientId, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        throw new NotSupportedException("A transport test reached the model.");

    public Task<AgentTurnResult> AskFollowUpAsync(
        ConversationState state, string question, CancellationToken cancellationToken, IProgress<string>? progress = null) =>
        throw new NotSupportedException("A transport test reached the model.");

    public Task<AgentTurnResult> StartAgendaSummaryAsync(string site, string patientId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A transport test reached the model.");
}
