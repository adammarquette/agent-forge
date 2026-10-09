using System.Net;
using FluentAssertions;
using AgentForge.Llm;

namespace AgentForge.IntegrationTests.Llm;

/// <summary>
/// Confirms the real Anthropic API rejects an invalid key - independent of
/// <see cref="AnthropicQaFixture"/>, since it needs no valid key configured at all (unlike every
/// other test in this suite). Not an <see cref="IClassFixture{TFixture}"/> of the shared fixture
/// on purpose: that fixture's constructor requires a valid key to be configured, which would
/// block this test even though it doesn't need one.
/// </summary>
// Reaches the real Anthropic API, so it stays out of the merge-request job.
[Trait("Deployment", "Anthropic")]
public sealed class AnthropicLlmProviderAuthenticationTests
{
    private const string PlaceholderModel = "claude-sonnet-5";

    [Fact]
    public async Task CompleteAsync_InvalidApiKey_RejectsWithUnauthorizedFromRealApi()
    {
        var provider = AnthropicQaFixture.BuildProviderWithInvalidKey(PlaceholderModel);
        var request = new LlmRequest("system", [LlmMessage.FromText(LlmRole.User, "hello")], MaxOutputTokens: 16);

        var act = () => provider.CompleteAsync(request, CancellationToken.None);

        // The provider deliberately keeps the error body out of the message - it can echo request content,
        // and AgentOrchestrator logs this message verbatim (CONVENTIONS.md §7).
        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        exception.Which.Message.Should().Contain("status 401").And.Contain("error type authentication_error");
        exception.Which.Message.Should().NotContain("\"type\":\"error\"", "the real response body must never reach the message");
        exception.Which.Message.Should().NotContain("x-api-key", "the body's message text must not be folded in either");
    }
}
