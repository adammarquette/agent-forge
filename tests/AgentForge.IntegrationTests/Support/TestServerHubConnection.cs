using System.Net;
using AgentForge.Api.Session;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Builds the <see cref="HubConnection"/> every in-process hub test connects with. One place,
/// because the transport is not a detail of any one test: ASP.NET Core Session is a per-HTTP-request
/// abstraction, so whether the hub can read it at all is decided entirely here.
/// <c>TestServerHubSessionTransportTests</c> is the guard on that.
/// </summary>
public static class TestServerHubConnection
{
    /// <summary>
    /// Connects to <paramref name="route"/> on <paramref name="server"/>, carrying
    /// <paramref name="cookies"/> so the hub sees whatever session they hold.
    /// </summary>
    /// <remarks>
    /// WebSockets, not long-polling, and that is load-bearing rather than a preference. A
    /// long-polling connection is a succession of separate HTTP requests, and SignalR hands the hub
    /// a <c>CloneHttpContext</c> of the one that established it - a copy carrying the request,
    /// response, connection and auth features plus <c>Items</c>, but NOT <c>ISessionFeature</c>. So
    /// <c>HttpContext.Session</c> throws "Session has not been configured for this application or
    /// request" inside <c>OnConnectedAsync</c>, which is what skipped five tests here. A WebSocket
    /// is one request that outlives the handshake, so the context is the real one and the session
    /// feature is still on it. The premise that ruled this out - that the in-memory TestServer has
    /// no sockets to negotiate over - is wrong: <see cref="TestServer.CreateWebSocketClient"/>
    /// exists for exactly this, and SignalR takes it through <c>WebSocketFactory</c>.
    /// </remarks>
    public static HubConnection Build(TestServer server, CookieContainer cookies, string route, string? contextKey = null) =>
        Build(server, cookies, route, HttpTransportType.WebSockets, contextKey);

    /// <summary>
    /// Connects over exactly <paramref name="transport"/>, for tests whose subject is the transport
    /// itself. ServerSentEvents and LongPolling are plain HTTP, so the cookie-carrying handler
    /// covers every request they make.
    /// </summary>
    /// <param name="contextKey">
    /// The page key the chat hub binds the connection to (<c>PatientContextBinding</c>), sent as the
    /// browser sends it; omitted, the chat hub refuses every method as a page for another patient.
    /// </param>
    public static HubConnection Build(
        TestServer server, CookieContainer cookies, string route, HttpTransportType transport, string? contextKey = null) =>
        new HubConnectionBuilder()
            .WithUrl(WithContextKey(new Uri(server.BaseAddress, route), contextKey), transport, options =>
            {
                // Negotiate is still ordinary HTTP over the TestServer's handler; only the transport
                // itself is a socket.
                options.HttpMessageHandlerFactory = _ =>
                    new CookieContainerHandler(cookies) { InnerHandler = server.CreateHandler() };

                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();

                    // The upgrade request never passes through the handler above, and
                    // WebSocketClient has no cookie jar - so the session cookie has to be put on
                    // by hand or the hub sees an anonymous connection.
                    client.ConfigureRequest = request =>
                    {
                        var cookieHeader = cookies.GetCookieHeader(server.BaseAddress);
                        if (!string.IsNullOrEmpty(cookieHeader))
                        {
                            request.Headers["Cookie"] = cookieHeader;
                        }
                    };

                    // SignalR hands over a ws:// uri; TestServer routes on http.
                    var uri = new UriBuilder(context.Uri) { Scheme = Uri.UriSchemeHttp }.Uri;
                    return await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                };
            })
            .Build();

    private static Uri WithContextKey(Uri hubUri, string? contextKey) =>
        contextKey is null
            ? hubUri
            : new Uri(QueryHelpers.AddQueryString(hubUri.ToString(), PatientContextBinding.QueryParameter, contextKey));
}
