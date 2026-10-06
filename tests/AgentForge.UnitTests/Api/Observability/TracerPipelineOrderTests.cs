using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// Pins that the REAL host's tracer pipeline runs <c>SpanPhiScrubber</c> ahead of its exporters (review of
/// a separate change finding 1). The scrubber's own tests prove what it does to a span; nothing else proves Program.cs
/// registers it, or registers it first - delete or move that one line and every other gate stays green while
/// patient ids, query strings and exception text reach Tempo. This boots <c>Program</c> on an in-memory
/// TestServer with the real OTLP exporter, swaps only its transport for a capturing handler and makes its
/// processor synchronous, so the bytes checked are the bytes that would have left the process.
/// </summary>
public sealed class TracerPipelineOrderTests : IDisposable
{
    private const string PatientId = "9a7b4c1e-2f3d-4e5a-8b6c-7d8e9f0a1b2c";
    private readonly CapturingOtlpHandler _otlp = new();
    private readonly WebApplicationFactory<Program> _factory;

    public TracerPipelineOrderTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // UseSetting, not ConfigureAppConfiguration: Program resolves the trace plan before Build().
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = "https://openemr.pipeline-order-test.invalid",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "pipeline-order-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.pipeline-order-test.invalid",
                ["Llm:ApiKey"] = "pipeline-order-test",
                ["Llm:Model"] = "pipeline-order-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                ["Observability:TraceOtlpEndpoint"] = "http://tempo.pipeline-order-test.invalid:4318/v1/traces",
            })
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services => services.Configure<OtlpExporterOptions>(o =>
            {
                // Synchronous export on span end, so processor ORDER decides what is exported, not a race.
                o.ExportProcessorType = ExportProcessorType.Simple;
                o.HttpClientFactory = () => new HttpClient(_otlp);
            }));
        });
    }

    [Fact]
    public async Task InboundRequestWithIdentifiers_ExportedSpanCarriesTemplatedPathAndNoIdentifier()
    {
        var client = _factory.CreateClient();

        await client.GetAsync(new Uri($"/evidence/document/{PatientId}?patient={PatientId}", UriKind.Relative));
        // The server span ends as the TestServer disposes the request context, just after the response returns.
        var tracer = _factory.Services.GetRequiredService<TracerProvider>();
        for (var i = 0; i < 50 && _otlp.Calls == 0; i++)
        {
            tracer.ForceFlush();
            await Task.Delay(100);
        }

        var exported = _otlp.Payloads();
        exported.Should().Contain("/evidence/document/{id}", "the inbound span must reach the exporter, scrubbed");
        exported.Should().NotContain(PatientId, "a patient id reached the trace exporter");
        exported.Should().NotContain("patient=", "a query string reached the trace exporter");
    }

    public void Dispose()
    {
        _factory.Dispose();
        _otlp.Dispose();
    }

    private sealed class CapturingOtlpHandler : HttpMessageHandler
    {
        private readonly List<byte[]> _bodies = [];

        public int Calls
        {
            get
            {
                lock (_bodies)
                {
                    return _bodies.Count;
                }
            }
        }

        // OTLP/protobuf carries attribute strings as raw UTF-8, so a byte search is a content search.
        public string Payloads()
        {
            lock (_bodies)
            {
                return string.Join("\n", _bodies.Select(b => Encoding.UTF8.GetString(b)));
            }
        }

        // The OTLP exporter calls the synchronous HttpClient.Send, which the base handler does not implement.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            lock (_bodies)
            {
                _bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
