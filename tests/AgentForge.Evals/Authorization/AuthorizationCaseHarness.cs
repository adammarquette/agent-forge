using System.Text.Json;
using AgentForge.Agent;
using AgentForge.Integration.OpenEmr;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Llm;
using AgentForge.Mcp;
using AgentForge.Mcp.Authorization;
using Microsoft.Extensions.Options;

namespace AgentForge.Evals.Authorization;

/// <summary>
/// Runs one role-confusion or injection case through the shipped enforcement path:
/// <c>McpToolDispatcher</c> → the real <c>PatientRelationshipAuthorizer</c> and
/// <c>PatientRelationshipGate</c> → <c>AuditingMcpToolServer</c> → a synthetic chart. Nothing about the
/// decision is re-implemented here; only the clinic day, the requester and the chart are fixtures.
/// reference: REQUIREMENTS.md §7.4 (FR-AUTH-2, FR-AUTH-3, FR-AUTH-4)
/// </summary>
internal static class AuthorizationCaseHarness
{
    // A fixed instant so nothing in the run depends on the wall clock. The stub directory ignores the date
    // filter this produces, so the value is arbitrary - what matters is that it never changes.
    private static readonly DateTimeOffset PinnedInstant = new(2026, 6, 15, 15, 0, 0, TimeSpan.Zero);

    public static async Task<CaseOutcome> RunAsync(GoldenCase testCase, CancellationToken cancellationToken)
    {
        var scenario = testCase.Authorization
            ?? throw new InvalidOperationException(
                $"Authorization case '{testCase.Id}' has no 'authorization' block.");

        var logs = new List<CapturedLog>();
        var clinicDay = scenario.ClinicDayAppointments
            .Select(a => new AppointmentRecord(
                new ClinicalSourceRef("Appointment", $"{scenario.Site}:{a.PatientId}"),
                a.PatientId,
                a.ProviderReference,
                a.Status,
                PinnedInstant))
            .ToArray();

        var identity = new FixedClinicianIdentity(scenario.RequesterIdentity);
        var correlation = new FixedCorrelationId();
        var authorizer = new PatientRelationshipAuthorizer(
            new StubAppointmentDirectory(clinicDay, scenario.AppointmentLookupFails),
            new ClinicClock(new FixedTimeProvider(PinnedInstant), Options.Create(new ClinicOptions())),
            new CapturingLogger<PatientRelationshipAuthorizer>(logs));

        var toolServer = new AuditingMcpToolServer(
            new SyntheticChartToolServer(), identity, correlation, new CapturingLogger<AccessAudit>(logs));

        var dispatcher = new McpToolDispatcher(
            toolServer, authorizer, identity, correlation, new NoOpMetrics(),
            new CapturingLogger<McpToolDispatcher>(logs), new CapturingLogger<AccessAudit>(logs));

        var result = await dispatcher.DispatchAsync(
            scenario.Site,
            scenario.SessionPatientId,
            new LlmToolCall("eval-tool-use-0", scenario.ToolCall.ToolName, scenario.ToolCall.ArgumentsJson),
            cancellationToken);

        return new CaseOutcome(
            Succeeded: !result.IsError,
            ResultJson: result.ResultJson,
            RejectionReason: result.IsError ? ExtractError(result.ResultJson) : null,
            Logs: logs);
    }

    private static string? ExtractError(string resultJson)
    {
        using var document = JsonDocument.Parse(resultJson);
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
