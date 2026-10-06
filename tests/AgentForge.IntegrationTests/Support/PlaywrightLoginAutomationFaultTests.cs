using FluentAssertions;
using Microsoft.Playwright;

namespace AgentForge.IntegrationTests.Support;

/// <summary>
/// Guards the environment-fault classification of a failed QA login: a Playwright error or a timeout is
/// reported as <see cref="QaEnvironmentUnavailableException"/>, anything else is not. Browser-free.
/// </summary>
// Selects this class into the merge-request job integration-tests-no-deployment. A separate change
[Trait("Deployment", "None")]
public sealed class PlaywrightLoginAutomationFaultTests
{
    private const string BaseUrl = "https://qa.invalid";

    public static TheoryData<Exception> EnvironmentFaults() => new()
    {
        new TimeoutException("Timeout 15000ms exceeded."),
        new PlaywrightException("net::ERR_CONNECTION_REFUSED"),
    };

    [Theory]
    [MemberData(nameof(EnvironmentFaults))]
    public async Task AsEnvironmentFaultAsync_PlaywrightOrTimeoutFailure_IsWrappedAsEnvironmentFault(Exception fault)
    {
        var act = () => PlaywrightLoginAutomation.AsEnvironmentFaultAsync<int>(() => throw fault, BaseUrl);

        var thrown = await act.Should().ThrowAsync<QaEnvironmentUnavailableException>();
        thrown.Which.InnerException.Should().BeSameAs(fault);
        thrown.Which.Message.Should().Contain("QA environment fault").And.Contain(fault.GetType().Name);
    }

    [Fact]
    public async Task AsEnvironmentFaultAsync_OtherFailure_PropagatesUnchanged()
    {
        var fault = new InvalidOperationException("no patient-select row");

        var act = () => PlaywrightLoginAutomation.AsEnvironmentFaultAsync<int>(() => throw fault, BaseUrl);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
    }

    [Fact]
    public async Task AsEnvironmentFaultAsync_SuccessfulStep_ReturnsItsResult()
    {
        var result = await PlaywrightLoginAutomation.AsEnvironmentFaultAsync(() => Task.FromResult(42), BaseUrl);

        result.Should().Be(42);
    }
}
