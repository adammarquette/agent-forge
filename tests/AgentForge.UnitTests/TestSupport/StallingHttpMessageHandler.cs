using System.Diagnostics;

namespace AgentForge.UnitTests.TestSupport;

/// <summary>
/// Terminal <see cref="HttpMessageHandler"/> test double that never answers: it waits on the
/// cancellation token it is given and nothing else. Stands in for a dependency whose host accepts
/// the connection and then goes silent - the failure mode that took staging's <c>/ready</c> to 100
/// seconds, which a handler that throws immediately cannot reproduce.
/// </summary>
internal sealed class StallingHttpMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new UnreachableException("Task.Delay(Timeout.Infinite) only ever completes by cancellation.");
    }
}
