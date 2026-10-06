using Microsoft.Playwright;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Classifies a QA fixture's failure to reach the shared environment - a browser or transport fault, as opposed
/// to an answer the environment gave - and rethrows it as <see cref="QaEnvironmentUnavailableException"/>
/// (for the Playwright login, a separate change for the HTTP token mints).
/// </summary>
internal static class QaEnvironmentFault
{
    /// <summary>
    /// Runs <paramref name="step"/> and rethrows an environment fault (<see cref="IsEnvironmentFault"/>) as
    /// <see cref="QaEnvironmentUnavailableException"/>, original kept as the inner exception; any other exception,
    /// including a non-success status the environment answered with, propagates unchanged.
    /// </summary>
    internal static async Task<T> WrapAsync<T>(Func<Task<T>> step, string operation, string baseUrl)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEnvironmentFault(ex))
        {
            throw new QaEnvironmentUnavailableException(
                $"QA environment fault, not a test result: {operation} against {baseUrl} did not complete " +
                $"({ex.GetType().Name}). Tests using this fixture did not run.",
                ex);
        }
    }

    /// <summary>
    /// True for a Playwright failure, a timeout, and a transport fault (connect, DNS, TLS, a dropped stream);
    /// false for an HTTP status the server answered with, a caller's cancellation, and anything else.
    /// </summary>
    internal static bool IsEnvironmentFault(Exception exception) => exception switch
    {
        // Playwright's timed calls throw System.TimeoutException, which is not a PlaywrightException.
        PlaywrightException or TimeoutException => true,
        // A StatusCode means the server answered with a status line; only a request that never got one is
        // transport. This arm also catches a malformed or truncated response from a server that IS up (a bad
        // status line, a connection reset mid-read) - .NET reports those as HttpRequestException with no
        // StatusCode too, same as never connecting at all.
        HttpRequestException { StatusCode: null } => true,
        // FileNotFound/DirectoryNotFound are IOExceptions too, and are a fixture's own configuration fault.
        FileNotFoundException or DirectoryNotFoundException => false,
        IOException => true,
        // HttpClient.Timeout elapsing; a caller's own cancellation carries no TimeoutException.
        TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };
}
