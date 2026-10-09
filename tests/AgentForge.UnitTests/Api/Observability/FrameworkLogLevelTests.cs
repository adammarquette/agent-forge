using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The framework's own loggers write request and outbound URLs at Information, and this sidecar's URLs carry
/// Patient, Binary and document ids in their path (<c>/fhir/Patient/{id}</c>, <c>/evidence/document/{id}</c>). Those
/// lines are diagnostic, so CONVENTIONS.md §7 bars them; the shipped <c>appsettings.json</c> holds both
/// categories at Warning, where neither logs a URL. Boots the real host so the file is what is tested, not a copy of
/// it. The <c>HttpClient</c> loggers are also removed in code, because their URL scope ignores the level; this
/// level is the backstop, and <c>HostStdoutPhiScanTests</c> the runtime check.
/// </summary>
public sealed class FrameworkLogLevelTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public FrameworkLogLevelTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = "https://openemr.framework-log-level-test.invalid",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "framework-log-level-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.framework-log-level-test.invalid",
                ["Llm:ApiKey"] = "framework-log-level-test",
                ["Llm:Model"] = "framework-log-level-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
            })
            {
                builder.UseSetting(key, value);
            }
        });
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.OpenEmrFhir.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.OpenEmrFhir.ClientHandler")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    public void UrlLoggingFrameworkCategory_IsSilentAtInformation(string category)
    {
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse($"{category} logs URLs carrying patient ids at Information");
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue("failures must still reach the diagnostic stream");
    }

    [Fact]
    public void SidecarCategory_StillLogsAtInformation()
    {
        // Guards the test above against passing because logging was switched off wholesale.
        _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentForge.Api.Agenda.AgendaRosterService")
            .IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    public void Dispose() => _factory.Dispose();
}
