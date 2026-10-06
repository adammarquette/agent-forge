using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// A real, in-process HTTP/1.1 server bound to loopback, answering every request with one fixed
/// status code. Used to give a <c>/ready</c> dependency check (<c>LlmProviderHealthCheck</c>,
/// <c>ObservabilityHealthCheck</c>) a peer this test owns instead of the live third party - the
/// health check under test still makes a genuine network round trip (CONVENTIONS.md §8.2's "nothing
/// mocked" is about the check under test, not about who is on the other end of the socket), so this
/// is a stub endpoint, not a mocked <c>HttpClient</c>.
/// </summary>
/// <remarks>
/// A raw <see cref="TcpListener"/> bound to port 0, not <see cref="HttpListener"/>: HttpListener's
/// URL-prefix API has no port-0 form, so using it means reserving a free port with one socket and
/// hoping nothing else claims it before HttpListener binds - a real, if narrow, race. Binding the one
/// socket this server ever uses to port 0 and reading back its assigned port has none.
/// </remarks>
public sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    /// <summary>Loopback base URL every request lands on, e.g. <c>http://127.0.0.1:54321/</c>.</summary>
    public string BaseUrl { get; }

    public StubHttpServer(int statusCode)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}/";

        _acceptLoop = Task.Run(() => AcceptLoopAsync(statusCode, _cts.Token));
    }

    private async Task AcceptLoopAsync(int statusCode, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or SocketException)
            {
                // The listener was stopped from Dispose() - not a failure of the stub itself.
                return;
            }

            _ = HandleClientAsync(client, statusCode, cancellationToken);
        }
    }

    private static async Task HandleClientAsync(TcpClient client, int statusCode, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();

                // Drain the request up to the blank line that ends the headers - enough to let the
                // client finish sending before this reads the response, no more (a probe's request
                // has no body this stub needs).
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(line));

                var reason = ReasonPhrase(statusCode);
                var body = "{}"u8.ToArray();
                var response =
                    $"HTTP/1.1 {statusCode} {reason}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(response);

                await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client (the health check's HttpClient) gave up before the response was fully
                // written - readiness probes are bounded and expected to do exactly that sometimes.
            }
        }
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        StatusCodes.Status200OK => "OK",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        _ => "Stub",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            // Best-effort shutdown - the loop already observed cancellation above.
        }

        _cts.Dispose();
    }
}
