using AgentForge.Api.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// Pins Program.cs's <c>ValidateOnStart</c> wiring for <see cref="ReadinessOptions"/>, not just
/// <see cref="ReadinessOptions.Validate"/> in isolation. <see cref="ReadinessOptionsTests"/> proves the
/// type rejects a non-positive <see cref="ReadinessOptions.ProbeTimeout"/> when asked; nothing proved the
/// host ever asks. Delete <c>.ValidateDataAnnotations()</c> (or <c>.ValidateOnStart()</c>) from that
/// registration and every other gate stays green while the host boots with a budget that cancels every
/// <c>/ready</c> probe before it starts - the defect carried across three review verdicts
/// without being pinned (note 76173 item 1).
/// </summary>
public sealed class ReadinessOptionsValidateOnStartWiringTests
{
    [Fact]
    public void HostStartup_ReadinessProbeTimeoutNotPositive_FailsFastRatherThanBootingSilently()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            // UseSetting, not ConfigureAppConfiguration: ValidateOnStart runs while Program builds the
            // host, before a test's ConfigureAppConfiguration callback would apply.
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = "https://openemr.readiness-wiring-test.invalid",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "readiness-wiring-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.readiness-wiring-test.invalid",
                ["Llm:ApiKey"] = "readiness-wiring-test",
                ["Llm:Model"] = "readiness-wiring-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
                ["Readiness:ProbeTimeout"] = "00:00:00",
            })
            {
                builder.UseSetting(key, value);
            }
        });

        // Accessing Server forces the host to build and start, which is where ValidateOnStart fires -
        // no request is ever sent.
        var act = () => factory.Server;

        act.Should().Throw<OptionsValidationException>()
            .Where(e => e.Message.Contains(nameof(ReadinessOptions.ProbeTimeout), StringComparison.Ordinal));
    }
}
