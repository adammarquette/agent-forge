using System.Net;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.UnitTests.TestSupport;

namespace AgentForge.UnitTests.Integration.OpenEmr.Http;

/// <summary>
/// Regression suite for the production defect: the SMART access token's lifetime was
/// checked exactly once, at launch, and never again. OpenEMR issues a one-hour access token; the
/// BFF session that holds it slides on every request, so an active session outlived its own token
/// and every FHIR read from minute 61 on came back 401 while the session still presented as
/// authenticated. Live evidence: 24 of 30 Week 1 tool calls failed, and every successful token use
/// OpenEMR recorded fell within seconds of a token being minted.
/// </summary>
public sealed class AuthHandlerExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 15, 05, 36, TimeSpan.Zero);

    [Fact]
    public async Task SendAsync_TokenAlreadyExpired_ThrowsRatherThanSendingACallOpenEmrCanOnly401()
    {
        // The reproduction. Before the fix this request went out with a dead bearer token and came
        // back 401, which NFR-REL-1 degradation then turned into a brief written from no chart.
        var tokenProvider = A.Fake<IAccessTokenProvider>();
        A.CallTo(() => tokenProvider.GetAccessTokenAsync(A<CancellationToken>._))
            .ReturnsLazily(() => ValueTask.FromResult<string?>("expired-token"));
        A.CallTo(() => tokenProvider.AccessTokenExpiresAt).Returns(Now.AddSeconds(-1));
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new AuthHandler(tokenProvider, new FixedTimeProvider(Now)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Observation"),
            CancellationToken.None);

        await act.Should().ThrowAsync<AccessTokenExpiredException>();
        capturing.LastRequest.Should().BeNull("a token OpenEMR will reject must not be spent on a round trip");
    }

    [Fact]
    public async Task SendAsync_TokenExpiresExactlyNow_ThrowsRatherThanRacingTheAuthorizationServer()
    {
        // The boundary belongs on the refusing side: at the expiry instant OpenEMR has already
        // stopped accepting the token, and network latency only widens the gap.
        var tokenProvider = A.Fake<IAccessTokenProvider>();
        A.CallTo(() => tokenProvider.GetAccessTokenAsync(A<CancellationToken>._))
            .ReturnsLazily(() => ValueTask.FromResult<string?>("boundary-token"));
        A.CallTo(() => tokenProvider.AccessTokenExpiresAt).Returns(Now);
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new AuthHandler(tokenProvider, new FixedTimeProvider(Now)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Observation"),
            CancellationToken.None);

        await act.Should().ThrowAsync<AccessTokenExpiredException>();
        capturing.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_TokenStillWithinItsLifetime_AttachesBearerAuthorizationHeader()
    {
        var tokenProvider = A.Fake<IAccessTokenProvider>();
        A.CallTo(() => tokenProvider.GetAccessTokenAsync(A<CancellationToken>._))
            .ReturnsLazily(() => ValueTask.FromResult<string?>("live-token"));
        A.CallTo(() => tokenProvider.AccessTokenExpiresAt).Returns(Now.AddSeconds(1));
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new AuthHandler(tokenProvider, new FixedTimeProvider(Now)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Observation"),
            CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization!.Parameter.Should().Be("live-token");
    }

    [Fact]
    public async Task SendAsync_ProviderAdvertisesNoExpiry_SendsRatherThanRefusingOnAnUnknown()
    {
        // The interface contract: a null expiry means "this authorization server advertised no
        // lifetime", not "expired". ScopedAccessTokenProvider never produces that pairing - it
        // adopts a token and its expiry together - but the refusal above must key off a *known*
        // expiry, or an unrelated IAccessTokenProvider implementation would be locked out entirely.
        var tokenProvider = A.Fake<IAccessTokenProvider>();
        A.CallTo(() => tokenProvider.GetAccessTokenAsync(A<CancellationToken>._))
            .ReturnsLazily(() => ValueTask.FromResult<string?>("unbounded-token"));
        A.CallTo(() => tokenProvider.AccessTokenExpiresAt).Returns(null);
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new AuthHandler(tokenProvider, new FixedTimeProvider(Now)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Observation"),
            CancellationToken.None);

        capturing.LastRequest!.Headers.Authorization!.Parameter.Should().Be("unbounded-token");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
