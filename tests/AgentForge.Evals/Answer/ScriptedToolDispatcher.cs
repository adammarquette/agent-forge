using System.Text.Json;
using AgentForge.Agent;
using AgentForge.Integration.OpenEmr.Fhir;
using AgentForge.Llm;
using AgentForge.Mcp;

namespace AgentForge.Evals.Answer;

/// <summary>
/// Serves one answer case's synthetic chart to the shipped <c>AgentOrchestrator</c>, in exactly the JSON
/// <c>McpToolDispatcher</c> would have produced - the same result records, serialized the same way - so
/// <c>ToolResultJsonScanner</c> recovers the same citations and the same
/// <c>DomainConstraintInput</c> it would in production.
/// </summary>
/// <remarks>
/// The authorization decision is <b>not</b> on this path: the <c>authz-*</c> cases dispatch through the real
/// <c>McpToolDispatcher</c> and score the gate (M3). These cases substitute for it deliberately, the way
/// they substitute for the model, so what is left under test is the verification and degradation behaviour
/// above it. A tool the case did not describe throws rather than returning empty, because an unnoticed empty
/// result reads as a gap the model honestly reported.
/// </remarks>
internal sealed class ScriptedToolDispatcher(AnswerScenario scenario, string caseId) : IMcpToolDispatcher
{
    // Matches McpToolDispatcher's own serializer options, so the fixture cannot drift from production JSON.
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public Task<LlmToolResultContent> DispatchAsync(
        string site, string patientId, LlmToolCall toolCall, CancellationToken cancellationToken)
    {
        var failure = scenario.FailingTools?
            .FirstOrDefault(f => string.Equals(f.ToolName, toolCall.ToolName, StringComparison.Ordinal));
        if (failure is not null)
        {
            return Task.FromResult(new LlmToolResultContent(
                toolCall.Id, JsonSerializer.Serialize(new { error = failure.Error }), IsError: true));
        }

        var chart = scenario.Chart ?? new AnswerChartFixture();
        var resultJson = toolCall.ToolName switch
        {
            "get_patient_summary" => Serialize(new PatientSummaryResult(
                new PatientRecord(new ClinicalSourceRef("Patient", patientId), chart.DisplayName, new DateOnly(1957, 3, 11), "female"),
                [.. chart.Problems.Select(p => new ConditionRecord(
                    new ClinicalSourceRef("Condition", p.Id), p.Display, p.ClinicalStatus, null))],
                [.. chart.Medications.Select(m => new MedicationRecord(
                    new ClinicalSourceRef("MedicationRequest", m.Id), m.Display, m.Dosage, m.Status, null))],
                [.. chart.Allergies.Select(a => new AllergyRecord(
                    new ClinicalSourceRef("AllergyIntolerance", a.Id), a.Display, a.ClinicalStatus, null, null))])),

            "get_labs" => Serialize(new LabsResult([.. chart.Labs.Select(LabRecord)])),

            "get_vitals" => Serialize(new VitalsResult([])),

            _ => throw new InvalidOperationException(
                $"Answer case '{caseId}' dispatched '{toolCall.ToolName}', which this harness does not serve. " +
                "Pin the tool in the case's chart, or script a tool the harness knows."),
        };

        return Task.FromResult(new LlmToolResultContent(toolCall.Id, resultJson));
    }

    // The date is the fixture's, not a constant: a case seeding a superseded result is the only way the
    // gate sees ObservationRecency's ranking at all. Status stays "final" - no case
    // seeds a retracted result yet, and a field no fixture sets is one nothing keeps honest.
    private static ObservationRecord LabRecord(LabFixture lab) => new(
        new ClinicalSourceRef("Observation", lab.Id), "laboratory", lab.Display, lab.Value, lab.Unit,
        lab.ReferenceRangeLow, lab.ReferenceRangeHigh, lab.EffectiveDateTime, "final");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ResultJsonOptions);
}
