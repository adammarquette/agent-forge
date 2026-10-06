namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// A QA fixture could not reach or drive the shared environment (e.g. the staging OpenEMR login page timed
/// out), so the tests depending on it never ran. Its name and message mark the failure as an environment fault
/// rather than a result about the behavior under test; the underlying error is kept as the inner exception.
/// </summary>
public sealed class QaEnvironmentUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
