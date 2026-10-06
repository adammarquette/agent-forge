using System.Net;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Api.Session;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.UnitTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.UnitTests.Integration.OpenEmr.Http;

public sealed class CorrelationIdHandlerTests
{
    [Fact]
    public async Task SendAsync_Always_AddsCorrelationIdHeaderFromAccessor()
    {
        var accessor = A.Fake<ICorrelationIdAccessor>();
        A.CallTo(() => accessor.CorrelationId).Returns("corr-abc-123");
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new CorrelationIdHandler(accessor) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Patient/1"),
            CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues(CorrelationIdHandler.HeaderName)
            .Should().ContainSingle().Which.Should().Be("corr-abc-123");
    }

    [Fact]
    public async Task SendAsync_RequestAlreadyCarriesCorrelationIdHeader_ReplacesItWithAccessorValue()
    {
        var accessor = A.Fake<ICorrelationIdAccessor>();
        A.CallTo(() => accessor.CorrelationId).Returns("authoritative-id");
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new CorrelationIdHandler(accessor) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://emr.example.org/apis/default/fhir/Patient/1");
        request.Headers.Add(CorrelationIdHandler.HeaderName, "stale-id");

        await invoker.SendAsync(request, CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues(CorrelationIdHandler.HeaderName)
            .Should().ContainSingle().Which.Should().Be("authoritative-id");
    }

    [Fact]
    public async Task SendAsync_HandlerBuiltByHttpClientFactory_SendsTheCorrelationIdEstablishedForTheCallingFlow()
    {
        // The header is only worth attaching if it carries the id the rest of the trace uses.
        // IHttpClientFactory builds a client's handler chain in its OWN scope and then pools it, so
        // a DelegatingHandler's Scoped dependency is never the calling request's instance - the same
        // mechanism that broke AuthHandler (see ScopedAccessTokenProvider's remarks). Storage has to
        // be ambient to the logical flow or this header carries an id nothing else ever logged.
        var capturing = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddScoped<MutableCorrelationIdAccessor>();
        services.AddScoped<ICorrelationIdAccessor>(sp => sp.GetRequiredService<MutableCorrelationIdAccessor>());
        services.AddTransient<CorrelationIdHandler>();
        services.AddHttpClient("openemr")
            .ConfigurePrimaryHttpMessageHandler(() => capturing)
            .AddHttpMessageHandler<CorrelationIdHandler>();
        using var provider = services.BuildServiceProvider();
        using var requestScope = provider.CreateScope();
        var idEstablishedAtIngress = requestScope.ServiceProvider
            .GetRequiredService<MutableCorrelationIdAccessor>().CorrelationId;

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("openemr");
        await client.GetAsync(new Uri("https://emr.example.org/apis/default/fhir/Patient/1"), CancellationToken.None);

        capturing.LastRequest!.Headers.GetValues(CorrelationIdHandler.HeaderName)
            .Should().ContainSingle().Which.Should().Be(idEstablishedAtIngress);
    }
}
