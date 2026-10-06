using System.Collections.Concurrent;
using System.Net;
using AgentForge.Api.Launch;
using AgentForge.Integration.OpenEmr.Auth;
using AgentForge.Mcp.Authorization;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Launch;

/// <summary>
/// Pins through the host's own <c>Program</c> wiring: the SMART launch routes are unauthenticated, so they
/// must write nothing to the session store that established sessions live in. The store is held at a tight
/// cap here so that, if a launch ever writes to it again, a flood fills it and the established session's next
/// growing write is the one the cache drops.
/// </summary>
public sealed class LaunchSessionStoreFloodTests
{
    // Room for one established session and its growth, and for about 40 pending launches if they were stored.
    private const long TightCapBytes = 8 * 1024;
    private const int FloodSize = 300;

    [Fact]
    public async Task CookielessLaunchFlood_WritesNothing_AndAnEstablishedSessionSurvivesAGrowingWrite()
    {
        using var host = new FloodHost();
        using var clinician = host.Browser();

        host.Auth.TokenLength = 400;
        await FloodHost.LaunchAndCompleteAsync(clinician);
        var sessionKey = host.Cache.WrittenKeys.Should().ContainSingle("only the completed callback wrote a session").Subject;
        var before = host.Cache.LiveLength(sessionKey);

        using var attacker = host.Browser(cookies: false);
        for (var i = 0; i < FloodSize; i++)
        {
            var route = (i % 4) switch
            {
                0 => "/launch",
                1 => "/agenda/launch",
                2 => "/callback?code=x&state=never-issued",
                _ => "/agenda/callback?code=x&state=never-issued",
            };
            using var response = await attacker.GetAsync(route);
            response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.BadRequest);
        }

        host.Cache.WriteCount.Should().Be(1, "no unauthenticated request wrote to the session store");

        // A re-launch on the same browser keeps the session and grows it: the write the cache drops at its cap.
        host.Auth.TokenLength = 3000;
        await FloodHost.LaunchAndCompleteAsync(clinician);

        host.Cache.WrittenKeys.Should().ContainSingle();
        host.Cache.LiveLength(sessionKey).Should().BeGreaterThan(before!.Value, "the grown session is still held");
    }

    [Fact]
    public async Task DoubleLaunch_EitherLaunchCompletes_TheLoginPageRelaunchCase()
    {
        using var host = new FloodHost();
        using var browser = host.Browser();

        var first = await FloodHost.LaunchAsync(browser, "/launch");
        var second = await FloodHost.LaunchAsync(browser, "/launch");

        (await FloodHost.CallbackAsync(browser, "/callback", first)).Should().Be(HttpStatusCode.Redirect);
        (await FloodHost.CallbackAsync(browser, "/callback", second)).Should().Be(HttpStatusCode.Redirect);
        (await FloodHost.CallbackAsync(browser, "/callback", second)).Should().Be(HttpStatusCode.BadRequest, "a pending launch is redeemed once");
    }

    [Theory]
    [InlineData("/launch", "/callback")]
    [InlineData("/agenda/launch", "/agenda/callback")]
    public async Task PendingLaunches_AreBoundedPerBrowser_AndExpire(string launchRoute, string callbackRoute)
    {
        using var host = new FloodHost();
        using var browser = host.Browser();

        var states = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            states.Add(await FloodHost.LaunchAsync(browser, launchRoute));
        }

        var cookie = host.PendingCookie(browser, launchRoute);
        cookie.Length.Should().BeLessThan(1500, "the cookie holds only the most recent launches");
        (await FloodHost.CallbackAsync(browser, callbackRoute, states[^(PendingLaunchCookie.MaxPending + 1)]))
            .Should().Be(HttpStatusCode.BadRequest, "the oldest launches were dropped");
        (await FloodHost.CallbackAsync(browser, callbackRoute, states[^PendingLaunchCookie.MaxPending]))
            .Should().Be(HttpStatusCode.Redirect);

        host.Clock.Advance(PendingLaunchCookie.Lifetime + TimeSpan.FromSeconds(1));
        (await FloodHost.CallbackAsync(browser, callbackRoute, states[^1]))
            .Should().Be(HttpStatusCode.BadRequest, "a pending launch expires");
        host.Cache.WriteCount.Should().Be(1, "only the one completed callback wrote to the store");
    }

    [Fact]
    public async Task PendingLaunchCookie_TamperedOrFromTheOtherFlow_IsRefused()
    {
        using var host = new FloodHost();
        using var browser = host.Browser();
        var state = await FloodHost.LaunchAsync(browser, "/launch");
        var chart = host.PendingCookie(browser, "/launch");
        using var forger = host.Browser(cookies: false);

        // Mid-payload, so the flip lands in the authenticated ciphertext rather than base64 padding bits.
        var at = chart.Length / 2;
        var tampered = string.Concat(chart.AsSpan(0, at), chart[at] == 'A' ? "B" : "A", chart.AsSpan(at + 1));
        (await FloodHost.CallbackAsync(forger, "/callback", state, $"{PendingLaunchCookie.CookieName("chart")}={tampered}"))
            .Should().Be(HttpStatusCode.BadRequest, "a flipped byte fails authentication");
        (await FloodHost.CallbackAsync(forger, "/agenda/callback", state, $"{PendingLaunchCookie.CookieName("agenda")}={chart}"))
            .Should().Be(HttpStatusCode.BadRequest, "a chart cookie is protected for the chart flow only");
        (await FloodHost.CallbackAsync(forger, "/callback", state, $"{PendingLaunchCookie.CookieName("chart")}={chart}"))
            .Should().Be(HttpStatusCode.Redirect, "the untouched cookie still redeems, so the refusals above are not vacuous");
    }

    private sealed class FloodHost : IDisposable
    {
        private readonly WebApplicationFactory<Program> factory;
        private readonly ConcurrentDictionary<HttpClient, CookieContainer> jars = new();

        public FloodHost()
        {
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                var settings = new Dictionary<string, string>
                {
                    ["OpenEmr:BaseUrl"] = "https://openemr.launch-flood-test.invalid",
                    ["OpenEmr:Site"] = "default",
                    ["OpenEmr:ClientId"] = "launch-flood-test",
                    ["OpenEmr:Scopes:0"] = "launch",
                    ["OpenEmrAgenda:ClientId"] = "launch-flood-test-agenda",
                    ["OpenEmrAgenda:Scopes:0"] = "launch",
                    ["Bff:PublicBaseUrl"] = "https://bff.launch-flood-test.invalid",
                    ["Llm:ApiKey"] = "launch-flood-test",
                    ["Llm:Model"] = "launch-flood-test",
                    ["Llm:InputPricePerMillionTokensUsd"] = "0",
                    ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                };
                foreach (var (key, value) in settings)
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureTestServices(services =>
                {
                    services.Configure<MemoryDistributedCacheOptions>(o => o.SizeLimit = TightCapBytes);
                    services.AddSingleton<IDistributedCache>(sp => Cache.Wrap(new MemoryDistributedCache(
                        sp.GetRequiredService<IOptions<MemoryDistributedCacheOptions>>())));
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddScoped<IOpenEmrAuthClient>(_ => Auth.Client);
                    services.AddScoped<IPatientRelationshipAuthorizer>(_ => Related());
                });
            });
        }

        public RecordingCache Cache { get; } = new();

        public FakeAuth Auth { get; } = new();

        public SettableClock Clock { get; } = new();

        public HttpClient Browser(bool cookies = true)
        {
            var jar = new CookieContainer();
            var client = factory.CreateDefaultClient(
                new Uri("https://localhost"),
                cookies ? new CookieJarHandler(jar) : new CookieJarHandler(null));
            jars[client] = jar;
            return client;
        }

        public string PendingCookie(HttpClient browser, string launchRoute)
        {
            var flow = launchRoute.StartsWith("/agenda", StringComparison.Ordinal) ? "agenda" : "chart";
            return jars[browser].GetCookies(new Uri("https://localhost/"))[PendingLaunchCookie.CookieName(flow)]?.Value
                ?? string.Empty;
        }

        public static async Task<string> LaunchAsync(HttpClient browser, string route)
        {
            using var response = await browser.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        }

        public static async Task<HttpStatusCode> CallbackAsync(HttpClient browser, string route, string state, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{route}?code=synthetic-code&state={state}");
            if (cookie is not null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            using var response = await browser.SendAsync(request);
            return response.StatusCode;
        }

        public static async Task LaunchAndCompleteAsync(HttpClient browser)
        {
            var state = await LaunchAsync(browser, "/launch");
            (await CallbackAsync(browser, "/callback", state)).Should().Be(HttpStatusCode.Redirect);
        }

        public void Dispose() => factory.Dispose();

        private static IPatientRelationshipAuthorizer Related()
        {
            var authorizer = A.Fake<IPatientRelationshipAuthorizer>();
            A.CallTo(() => authorizer.AuthorizeAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._))
                .Returns(new PatientRelationshipDecision(IsRelated: true, ClinicDayAppointmentsConsidered: 1));
            return authorizer;
        }
    }

    /// <summary>Synthetic OpenEMR token endpoint; the access token's length sets how large the session grows.</summary>
    private sealed class FakeAuth
    {
        public FakeAuth()
        {
            A.CallTo(() => Client.ExchangeAuthorizationCodeAsync(
                    A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
                .ReturnsLazily(() => new TokenResponse(
                    new string('t', TokenLength), "Bearer", 3600, "launch", null, "synthetic-patient-1", null));
            A.CallTo(() => Client.IntrospectAsync(A<string>._, A<string>._, A<string>._, A<string?>._, A<CancellationToken>._))
                .Returns(new IntrospectionResponse(
                    true, "launch", "launch-flood-test", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    "synthetic-clinician", "synthetic-patient-1"));
        }

        public IOpenEmrAuthClient Client { get; } = A.Fake<IOpenEmrAuthClient>();

        public int TokenLength { get; set; } = 400;
    }

    private sealed class SettableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    /// <summary>Counts every write and asks the real cache which written keys it still holds.</summary>
    private sealed class RecordingCache
    {
        private readonly ConcurrentDictionary<string, byte> keys = new(StringComparer.Ordinal);
        private IDistributedCache? inner;
        private int writes;

        public int WriteCount => writes;

        public IReadOnlyCollection<string> WrittenKeys => keys.Keys.ToList();

        public int? LiveLength(string key) => inner!.Get(key)?.Length;

        public IDistributedCache Wrap(IDistributedCache cache)
        {
            inner = cache;
            return new Recorder(this, cache);
        }

        private void Record(string key)
        {
            keys.TryAdd(key, 0);
            Interlocked.Increment(ref writes);
        }

        private sealed class Recorder(RecordingCache owner, IDistributedCache cache) : IDistributedCache
        {
            public byte[]? Get(string key) => cache.Get(key);

            public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => cache.GetAsync(key, token);

            public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            {
                owner.Record(key);
                cache.Set(key, value, options);
            }

            public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            {
                owner.Record(key);
                return cache.SetAsync(key, value, options, token);
            }

            public void Refresh(string key) => cache.Refresh(key);

            public Task RefreshAsync(string key, CancellationToken token = default) => cache.RefreshAsync(key, token);

            public void Remove(string key) => cache.Remove(key);

            public Task RemoveAsync(string key, CancellationToken token = default) => cache.RemoveAsync(key, token);
        }
    }

    /// <summary>A browser's cookie jar over the in-memory test server; null keeps no cookies at all.</summary>
    private sealed class CookieJarHandler(CookieContainer? jar) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (jar is not null && jar.GetCookieHeader(request.RequestUri!) is { Length: > 0 } header)
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (jar is not null && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    jar.SetCookies(request.RequestUri!, setCookie);
                }
            }

            return response;
        }
    }
}
