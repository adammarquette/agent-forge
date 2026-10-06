namespace AgentForge.Api.Health;

/// <summary>
/// The one bounded GET every HTTP <c>/ready</c> dependency check makes. Shared rather than repeated
/// per check because the budget is a property of readiness itself, not of any one dependency -
/// all three HTTP checks previously inherited <see cref="HttpClient"/>'s 100-second default and all
/// three could therefore hold the endpoint open. The vector-index check is the
/// one that is not HTTP; it applies the same budget over its own connection.
/// </summary>
internal static class ReadinessProbe
{
    /// <summary>
    /// GETs <paramref name="uri"/> under <paramref name="probeTimeout"/>, translating a transport
    /// failure or an exhausted budget into a value rather than an exception - a dependency being
    /// down is an expected readiness outcome, not an error the endpoint should surface as a 500.
    /// </summary>
    public static async Task<ReadinessProbeOutcome> GetAsync(
        HttpClient httpClient, string uri, TimeSpan probeTimeout, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(probeTimeout);
        try
        {
            return new ReadinessProbeOutcome(
                await httpClient.GetAsync(uri, budget.Token).ConfigureAwait(false), null, TimedOut: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Only our own budget counts as a timeout; a caller that cancelled (the request was
            // aborted) tells us nothing about the dependency, so it is reported as unreachable.
            var timedOut = budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            return new ReadinessProbeOutcome(null, ex, timedOut);
        }
    }

    /// <summary>
    /// The one wording every <c>/ready</c> check uses for a dependency that did not answer, so two
    /// dependencies failing the same way read the same way in the body.
    /// </summary>
    public static string DescribeUnreachable(bool timedOut, TimeSpan probeTimeout) =>
        timedOut ? $"no answer within {probeTimeout.TotalSeconds:0.###}s" : "unreachable";
}

/// <summary>
/// What a single bounded probe found: a <paramref name="Response"/>, or the
/// <paramref name="Failure"/> that stopped it and whether that was the budget expiring.
/// </summary>
/// <param name="Response">The dependency's response, or <c>null</c> if it never answered. The caller owns disposal.</param>
/// <param name="Failure">The transport failure or cancellation, when there is no response.</param>
/// <param name="TimedOut">True when the probe's own budget expired rather than the transport failing.</param>
internal sealed record ReadinessProbeOutcome(HttpResponseMessage? Response, Exception? Failure, bool TimedOut)
{
    /// <summary>Describes why the dependency did not answer, for the check's health description.</summary>
    public string DescribeFailure(TimeSpan probeTimeout) =>
        ReadinessProbe.DescribeUnreachable(TimedOut, probeTimeout);
}
