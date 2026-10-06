using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentForge.IntegrationTests.Api;

/// <summary>
/// Boots the real <c>Program.cs</c> composition as <c>Production</c>, where - as everywhere outside
/// Development - the host's default provider does not validate scopes. Needs no QA deployment: nothing
/// under test leaves the process, so every endpoint is a reserved <c>.invalid</c> name and the LLM
/// key is a placeholder that no call ever sends. The Week 2 data graph is held off even when the
/// runner's environment sets <c>AgentForgeData__ConnectionString</c> (as <c>docker-compose.yml</c>
/// does), so this stays a composition test rather than a database one.
/// </summary>
public sealed class ProductionHostFixture : WebApplicationFactory<global::Program>
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory defaults to Development, which validates scopes on its own and would
        // make every assertion against this fixture pass whatever Program.cs says.
        builder.UseEnvironment("Production");

        // UseSetting, not ConfigureAppConfiguration: Program.cs reads AgentForgeData:ConnectionString
        // before the host is built, and only settings reach it that early - over environment variables.
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["OpenEmr:BaseUrl"] = "https://openemr.production-composition-test.invalid",
            ["OpenEmr:Site"] = "default",
            ["OpenEmr:ClientId"] = "production-composition-test-client",
            ["OpenEmr:Scopes:0"] = "patient/Patient.read",
            ["Bff:PublicBaseUrl"] = "https://bff.production-composition-test.invalid",
            ["Llm:ApiKey"] = "placeholder-never-sent",
            ["Llm:Model"] = "placeholder-model",
            ["Llm:InputPricePerMillionTokensUsd"] = "0",
            ["Llm:OutputPricePerMillionTokensUsd"] = "0",
            ["AgentForgeData:ConnectionString"] = string.Empty,
        })
        {
            builder.UseSetting(key, value);
        }
    }
}
