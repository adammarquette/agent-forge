using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentForge.Data.Entities;
using AgentForge.Documents;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// Boots the REAL host with its Postgres store configured but unreachable, and pins the maintainer's ruling on
/// a store that is down at boot does not abort startup - the host comes up, liveness answers, and
/// <c>/ready</c> is a 503 that names the store, exactly as for a store that goes down after boot. Before it,
/// the startup migration threw <c>NpgsqlException</c> out of <c>Program</c> and the host never listened, so the
/// readiness check written for this case could not answer it.
/// </summary>
/// <remarks>
/// In-memory TestServer. The store (Postgres) and OpenEMR point at a closed loopback port, so each is refused
/// locally: no database exists and nothing leaves the machine. <see cref="IDocumentExtractor"/> is replaced
/// with a fake (see <see cref="Ingest_StoreUnreachableAtBoot_IsNotAccepted"/>) rather than pointed at the LLM
/// at all - a fake proves the extractor is never reached, which a reachable-but-unanswering port cannot: that
/// still lets the extractor's own retries run to a slow timeout and the assertion pass for the wrong reason
/// </remarks>
public sealed class StoreUnreachableAtBootTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    // A.Fake, not a stub with a canned result: IngestionAsync must never reach it while the store is down, and
    // MustNotHaveHappened is what proves that rather than assuming it from the call order in source.
    private readonly IDocumentExtractor _extractor = A.Fake<IDocumentExtractor>();

    public StoreUnreachableAtBootTests()
    {
        var closedPort = ClosedLoopbackPort();
        var nowhere = $"http://127.0.0.1:{closedPort}";
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // UseSetting, not ConfigureAppConfiguration: Program decides whether Week 2 is wired before Build().
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["AgentForgeData:ConnectionString"] =
                    $"Host=127.0.0.1;Port={closedPort};Database=agentforge;Username=agentforge;Password=unused;Timeout=3",
                ["OpenEmr:BaseUrl"] = nowhere,
                // Loopback only, so plain http is the local case the option exists for.
                ["OpenEmr:AllowInsecureHttpForLocalDevelopment"] = "true",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "store-down-at-boot-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.store-down-at-boot-test.invalid",
                ["Llm:BaseUrl"] = nowhere,
                ["Llm:ApiKey"] = "store-down-at-boot-test",
                ["Llm:Model"] = "store-down-at-boot-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                ["Cohere:ApiKey"] = string.Empty,
            })
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentExtractor>();
                services.AddScoped(_ => _extractor);
            });
        });
    }

    [Fact]
    public async Task Boot_StoreUnreachable_HostStartsAndLivenessAnswers()
    {
        // Given a configured store that refuses every connection, when the host boots, then it serves.
        using var client = _factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative));

        health.StatusCode.Should().Be(HttpStatusCode.OK, "liveness says the process is up, and it is");
    }

    [Fact]
    public async Task Ready_StoreUnreachableAtBoot_Returns503NamingTheStore()
    {
        using var client = _factory.CreateClient();

        using var ready = await client.GetAsync(new Uri("/ready", UriKind.Relative));

        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var vectorIndex = body.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "vector-index");
        vectorIndex.GetProperty("status").GetString().Should().Be("Unhealthy");
        vectorIndex.GetProperty("description").GetString().Should().Contain("Postgres");
    }

    [Fact]
    public async Task Ingest_StoreUnreachableAtBoot_IsNotAccepted()
    {
        // The store is what refuses this ingestion (the content-hash lookup comes first): _extractor is a fake
        // that would happily answer, so its MustNotHaveHappened below proves the store is why nothing was
        // extracted, rather than merely being unable to rule the extractor out.
        using var client = _factory.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent([0x25, 0x50, 0x44, 0x46]), "file", "synthetic.pdf" },
            { new StringContent("synthetic-patient"), "patientId" },
            { new StringContent("synthetic-docref"), "documentReferenceId" },
            { new StringContent("lab_pdf"), "docType" },
        };

        HttpResponseMessage? response = null;
        try
        {
            response = await client.PostAsync(new Uri("/documents/ingest", UriKind.Relative), form);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // TestServer rethrows an unhandled pipeline exception to the caller: the request failed.
        }

        using (response)
        {
            response?.IsSuccessStatusCode.Should().BeFalse("a store that cannot be read must not let an ingestion through");
        }

        A.CallTo(() => _extractor.ExtractAsync(
                A<ClinicalDocumentType>._, A<ReadOnlyMemory<byte>>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    public void Dispose() => _factory.Dispose();

    private static int ClosedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
