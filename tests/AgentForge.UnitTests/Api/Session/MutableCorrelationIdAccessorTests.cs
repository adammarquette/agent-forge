using FluentAssertions;
using AgentForge.Api.Session;

namespace AgentForge.UnitTests.Api.Session;

public sealed class MutableCorrelationIdAccessorTests
{
    [Fact]
    public void CorrelationId_FirstAccess_MintsANonEmptyValue()
    {
        var sut = new MutableCorrelationIdAccessor();

        sut.CorrelationId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CorrelationId_AccessedTwiceWithinTheSameScope_ReturnsTheSameValue()
    {
        // BFF ingress mints one correlation id per request/hub invocation and propagates it to
        // every downstream call (ARCHITECTURE.md §11) - a second mint mid-request would break
        // that reconstruction.
        var sut = new MutableCorrelationIdAccessor();

        var first = sut.CorrelationId;
        var second = sut.CorrelationId;

        second.Should().Be(first);
    }

    [Fact]
    public async Task CorrelationId_TwoInstancesOnSeparateLogicalFlows_MintDifferentValues()
    {
        // One request / hub invocation is one logical flow, and each has to get its own id - this
        // guards that the mint is actually random per flow, not a shared or static constant.
        var firstFlow = new TaskCompletionSource<string>();
        var secondFlow = new TaskCompletionSource<string>();

        await Task.WhenAll(
            Task.Run(() => firstFlow.SetResult(new MutableCorrelationIdAccessor().CorrelationId)),
            Task.Run(() => secondFlow.SetResult(new MutableCorrelationIdAccessor().CorrelationId)));

        (await secondFlow.Task).Should().NotBe(await firstFlow.Task);
    }

    [Fact]
    public void CorrelationId_SetOnOneInstance_IsVisibleFromADifferentInstanceOnTheSameLogicalFlow()
    {
        // Regression test, the same mechanism ScopedAccessTokenProvider's own
        // remarks describe: CorrelationIdHandler is constructed by IHttpClientFactory in ITS OWN
        // handler-building scope and then pooled, so with per-instance storage the handler reads a
        // different, never-set accessor and stamps X-Correlation-Id with an id that appears in no
        // log line anywhere - propagation that looks real and is not. Storage has to be ambient to
        // the logical flow, not tied to which DI scope constructed which instance.
        var establishedAtIngress = new MutableCorrelationIdAccessor { CorrelationId = "corr-ingress" };
        var readByAnOutboundHandler = new MutableCorrelationIdAccessor();

        readByAnOutboundHandler.CorrelationId.Should().Be(establishedAtIngress.CorrelationId);
    }

    [Fact]
    public async Task CorrelationId_SetInOneConcurrentFlow_NeverLeaksIntoAnother()
    {
        // The other half of ambient storage: the Daily Agenda fans several turns out at once, and
        // two concurrent flows sharing one id would interleave in the log stream - the
        // reconstruction FR-OBS-1 exists for would silently mix two patients' turns together.
        var flowAResult = new TaskCompletionSource<string>();
        var flowBResult = new TaskCompletionSource<string>();

        var flowA = Task.Run(async () =>
        {
            var accessor = new MutableCorrelationIdAccessor { CorrelationId = "corr-a" };
            await Task.Delay(50);
            flowAResult.SetResult(accessor.CorrelationId);
        });
        var flowB = Task.Run(async () =>
        {
            var accessor = new MutableCorrelationIdAccessor { CorrelationId = "corr-b" };
            await Task.Delay(50);
            flowBResult.SetResult(accessor.CorrelationId);
        });

        await Task.WhenAll(flowA, flowB);

        (await flowAResult.Task).Should().Be("corr-a");
        (await flowBResult.Task).Should().Be("corr-b");
    }

    [Fact]
    public void NewCorrelationId_CalledTwice_ReturnsDistinctNonEmptyValues()
    {
        // Ingress mints explicitly rather than leaning on the lazy read, so a connection that
        // serves several requests in turn can never hand the second one the first one's id.
        var first = MutableCorrelationIdAccessor.NewCorrelationId();
        var second = MutableCorrelationIdAccessor.NewCorrelationId();

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().NotBe(first);
    }
}
