using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Playwright;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Guards the environment-fault classification of a QA fixture's HTTP token mint: a transport, TLS or
/// timeout failure is reported as <see cref="QaEnvironmentUnavailableException"/>; an answer the server gave, a
/// caller's cancellation and a fixture's own configuration fault are not. Browser-free.
/// </summary>
// Selects this class into the merge-request job integration-tests-no-deployment.
[Trait("Deployment", "None")]
public sealed class QaEnvironmentFaultTests
{
    private const string BaseUrl = "https://qa.invalid";

    public static TheoryData<Exception> EnvironmentFaults() => new()
    {
        // The shape job 127982 failed with.
        new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")),
        new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"),
        new IOException("Unable to read data from the transport connection."),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        new TimeoutException("Timeout 15000ms exceeded."),
        new PlaywrightException("net::ERR_CONNECTION_REFUSED"),
    };

    public static TheoryData<Exception> OtherFailures() => new()
    {
        new HttpRequestException("Response status code does not indicate success: 502 (Bad Gateway).", null, HttpStatusCode.BadGateway),
        new TaskCanceledException("A task was canceled."),
        new OperationCanceledException(),
        new FileNotFoundException("private key PEM not found"),
        new DirectoryNotFoundException("private key directory not found"),
        new InvalidOperationException("OpenEMR client_credentials token mint failed: 401 Unauthorized"),
    };

    [Theory]
    [MemberData(nameof(EnvironmentFaults))]
    public async Task WrapAsync_TransportOrTimeoutFailure_IsWrappedAsEnvironmentFault(Exception fault)
    {
        var act = () => QaEnvironmentFault.WrapAsync<int>(() => throw fault, "the token mint", BaseUrl);

        var thrown = await act.Should().ThrowAsync<QaEnvironmentUnavailableException>();
        thrown.Which.InnerException.Should().BeSameAs(fault);
        thrown.Which.Message.Should().Contain("QA environment fault").And.Contain("the token mint")
            .And.Contain(BaseUrl).And.Contain(fault.GetType().Name);
    }

    [Theory]
    [MemberData(nameof(OtherFailures))]
    public async Task WrapAsync_AnswerCancellationOrConfigurationFailure_PropagatesUnchanged(Exception fault)
    {
        var act = () => QaEnvironmentFault.WrapAsync<int>(() => throw fault, "the token mint", BaseUrl);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(fault);
    }

    [Fact]
    public async Task MintAccessTokenAsync_TlsHandshakeDroppedByTheServer_ThrowsEnvironmentFault()
    {
        // A loopback peer that accepts and closes: the client's TLS handshake reads EOF, as against staging.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var dropper = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Stopped before a connection arrived; the assertion above has already reported why.
            }
        });

        var keyPath = Path.GetTempFileName();
        try
        {
            using (var rsa = RSA.Create(2048))
            {
                await File.WriteAllTextAsync(keyPath, rsa.ExportRSAPrivateKeyPem());
            }

            var baseUrl = $"https://127.0.0.1:{port}";
            var act = () => OpenEmrSystemTokenClient.MintAccessTokenAsync(
                baseUrl, "default", "qa-client", keyPath, "qa-kid", "system/Patient.read");

            var thrown = await act.Should().ThrowAsync<QaEnvironmentUnavailableException>();
            thrown.Which.InnerException.Should().BeOfType<HttpRequestException>();
            thrown.Which.Message.Should().Contain("client_credentials token mint").And.Contain(baseUrl);
        }
        finally
        {
            File.Delete(keyPath);
            listener.Stop();
            await dropper.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MintAccessTokenAsync_MissingPrivateKeyFile_PropagatesAsConfigurationFault()
    {
        var act = () => OpenEmrSystemTokenClient.MintAccessTokenAsync(
            "https://127.0.0.1:1", "default", "qa-client", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pem"),
            "qa-kid", "system/Patient.read");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }
}
