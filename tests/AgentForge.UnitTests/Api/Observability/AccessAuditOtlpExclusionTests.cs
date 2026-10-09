using System.Collections.Concurrent;
using System.Reflection;
using AgentForge.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AgentForge.UnitTests.Api.Observability;

/// <summary>
/// The access-audit trail is the one stream that names the patient (FR-AUTH-4), so it must never reach an
/// OpenTelemetry log backend - Loki, or any OTLP sink an environment configures. It stays on the built-in console
/// provider only (maintainer ruling on a separate change item 1, option A). Boots the real host under Production and hangs a
/// capturing processor on the OpenTelemetry logger provider, which is the pipeline every OTel exporter - console and
/// OTLP alike - reads from, so the filter under test is the one Program.cs registers, and no configuration rule may
/// undo it.
/// </summary>
public sealed class AccessAuditOtlpExclusionTests : IDisposable
{
    private const string AccessAuditCategory = "AgentForge.AccessAudit";
    private const string DiagnosticCategory = "AgentForge.Api.Agenda.AgendaRosterService";

    private readonly CategoryCapture _otelRecords = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AccessAuditOtlpExclusionTests() => _factory = BootProduction(_otelRecords, []);

    [Fact]
    public void AccessAuditLine_NeverReachesTheOpenTelemetryLogPipeline_WhileDiagnosticLinesStillExport()
    {
        var loggers = _factory.Services.GetRequiredService<ILoggerFactory>();

        Write(loggers.CreateLogger(DiagnosticCategory), "synthetic diagnostic line");
        Write(loggers.CreateLogger(AccessAuditCategory), "synthetic audit line");

        _otelRecords.Categories.Should().Contain(DiagnosticCategory,
            "diagnostic logs must keep flowing to OTLP, or this test would pass with OTel logging switched off");
        _otelRecords.Categories.Should().NotContain(AccessAuditCategory,
            "the audit trail names the patient and is console-only (item 1, option A)");
    }

    // Each key outranks a plain `AgentForge.AccessAudit` rule in the framework's selector: a longer category, or the
    // provider's full type name. The review set the first and the exclusion went red.
    [Theory]
    [InlineData("Logging:OpenTelemetry:LogLevel:AgentForge.AccessAudit*")]
    [InlineData("Logging:OpenTelemetry:LogLevel:*AgentForge.AccessAudit")]
    [InlineData("Logging:OpenTelemetry:LogLevel:AgentForge.AccessAudit*AgentForge.AccessAudit")]
    [InlineData("Logging:OpenTelemetry.Logs.OpenTelemetryLoggerProvider:LogLevel:AgentForge.AccessAudit*")]
    public void AccessAuditLine_WhenConfigurationNamesTheCategoryMoreSpecifically_StillNeverReachesOpenTelemetry(string key)
    {
        var otelRecords = new CategoryCapture();
        using var factory = BootProduction(otelRecords, new() { [key] = "Information" });
        var loggers = factory.Services.GetRequiredService<ILoggerFactory>();

        Write(loggers.CreateLogger(DiagnosticCategory), "synthetic diagnostic line");
        Write(loggers.CreateLogger(AccessAuditCategory), "synthetic audit line");

        otelRecords.Categories.Should().Contain(DiagnosticCategory, "diagnostic logs must keep flowing to OTLP");
        otelRecords.Categories.Should().NotContain(AccessAuditCategory,
            "no configuration may put the audit trail, which names the patient, on an OTel exporter");
    }

    [Fact]
    public void ConfigurationRuleForTheOpenTelemetryProvider_StillAppliesToADiagnosticCategory()
    {
        // Guards the theory above against passing because configuration rules stopped reaching the OTel provider.
        var otelRecords = new CategoryCapture();
        using var factory = BootProduction(otelRecords, new() { [$"Logging:OpenTelemetry:LogLevel:{DiagnosticCategory}*"] = "None" });

        Write(factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(DiagnosticCategory), "synthetic diagnostic line");

        otelRecords.Categories.Should().NotContain(DiagnosticCategory);
    }

    [Fact]
    public void AccessAuditCategory_StillWritesToTheConsoleProvider()
    {
        // Guards the test above against passing because the audit trail was switched off rather than rerouted.
        _factory.Services.GetServices<ILoggerProvider>().Should().ContainSingle(p => p is ConsoleLoggerProvider);
        _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(AccessAuditCategory)
            .IsEnabled(LogLevel.Information).Should().BeTrue("FR-AUTH-4 records every granted access at Information");
    }

    [Fact]
    public void EveryAccessAuditMethod_WritesThroughTheDedicatedCategory()
    {
        // A caller-supplied ILogger would carry the caller's category, which no filter can tell apart from diagnostics.
        var loggerParameterTypes = typeof(AccessAuditLog)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == typeof(AccessAuditLog))
            .Select(m => m.GetParameters()[0].ParameterType)
            .ToList();

        loggerParameterTypes.Should().HaveCount(2).And.OnlyContain(t =>
            t.IsGenericType
            && t.GetGenericTypeDefinition() == typeof(ILogger<>)
            && t.GetGenericArguments()[0].FullName == AccessAuditCategory);
    }

    public void Dispose() => _factory.Dispose();

    private static WebApplicationFactory<Program> BootProduction(CategoryCapture otelRecords, Dictionary<string, string> extraSettings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OpenEmr:BaseUrl"] = "https://openemr.access-audit-otlp-test.invalid",
                ["OpenEmr:Site"] = "default",
                ["OpenEmr:ClientId"] = "access-audit-otlp-test",
                ["OpenEmr:Scopes:0"] = "launch",
                ["Bff:PublicBaseUrl"] = "https://bff.access-audit-otlp-test.invalid",
                ["Llm:ApiKey"] = "access-audit-otlp-test",
                ["Llm:Model"] = "access-audit-otlp-test",
                ["Llm:InputPricePerMillionTokensUsd"] = "0",
                ["Llm:OutputPricePerMillionTokensUsd"] = "0",
            }.Concat(extraSettings))
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
                services.ConfigureOpenTelemetryLoggerProvider(otel => otel.AddProcessor(otelRecords)));
        });

    // ILogger.Log directly: the extension methods trip CA1848, and a LoggerMessage per test line is noise.
    private static void Write(ILogger logger, string message) =>
        logger.Log(LogLevel.Warning, default, message, null, static (state, _) => state);

    private sealed class CategoryCapture : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<string> _categories = new();

        public IReadOnlyCollection<string> Categories => [.. _categories];

        // LogRecord instances are pooled, so only the category is kept.
        public override void OnEnd(LogRecord data) => _categories.Enqueue(data.CategoryName ?? string.Empty);
    }
}
