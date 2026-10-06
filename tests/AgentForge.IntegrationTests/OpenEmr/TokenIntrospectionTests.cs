using FluentAssertions;
using AgentForge.Integration.OpenEmr.Auth;
using AgentForge.IntegrationTests.Support;
using Refit;

namespace AgentForge.IntegrationTests.OpenEmr;

/// <summary>
/// Exercises the real introspection endpoint with a token that cannot possibly be valid, and asserts
/// the load-bearing property FR-AUTH-4 depends on: OpenEMR never affirms a never-issued token as
/// active. Measured against staging on 2026-09-28, the endpoint answers <c>200 {"active":false}</c>
/// for a non-JWT token, a JWT-shaped garbage token, a wrong client secret and an unknown client alike
/// - the fork's controller catches its own client-authentication failure and returns inactive. So
/// this test does NOT prove that <see cref="QaOpenEmrOptions.TestClientId"/> authenticates; only a
/// token issued to that client could, and the suite does not mint one. An
/// earlier observation of 400/500 for the two token shapes is why the <see cref="ApiException"/>
/// branch still tolerates a refusal.
/// </summary>
public sealed class TokenIntrospectionTests : IClassFixture<OpenEmrQaFixture>
{
    private readonly OpenEmrQaFixture _fixture;

    public TokenIntrospectionTests(OpenEmrQaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task IntrospectAsync_TokenThatWasNeverIssued_NeverReportsItActive()
    {
        // OpenEMR's /introspect takes client_id/client_secret form parameters (HTTP Basic Auth is not
        // accepted). TestClientId/TestClientSecret come from a confidential patient/-only
        // client, which OpenEMR enables on registration.
        if (string.IsNullOrEmpty(_fixture.Options.TestClientId))
        {
            throw new InvalidOperationException(
                $"{QaOpenEmrOptions.SectionName}__TestClientId (and TestClientSecret) must be set to an " +
                "enabled OpenEMR client (a confidential client asking for patient/ scopes only); " +
                "see QaOpenEmrOptions.TestClientId.");
        }

        var authClient = new OpenEmrAuthClient(_fixture.AuthApi);

        IntrospectionResponse? response;
        try
        {
            response = await authClient.IntrospectAsync(
                _fixture.Options.Site,
                $"never-issued-{Guid.NewGuid():N}",
                _fixture.Options.TestClientId,
                _fixture.Options.TestClientSecret,
                CancellationToken.None);
        }
        catch (ApiException)
        {
            // The server refused the call outright instead of returning active:false - not
            // RFC-7662-compliant, but it never affirmed the token as active either, so the
            // property this test actually guards still holds. See the class doc comment.
            return;
        }

        response.Active.Should().BeFalse();
    }
}
