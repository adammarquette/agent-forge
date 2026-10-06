using System.Text.Json;
using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.Verification;

/// <summary>
/// Walks the raw JSON of every tool result returned this turn to recover (a) the set of
/// resolvable citations and (b) the structured clinical data the domain-constraint rules need -
/// both derived from the same source of truth (what tools actually returned), never from the
/// model's prose. Generic by design: it recognizes record shapes structurally (which fields are
/// present), so it works for any tool's result without a tool-name-to-type lookup table to keep
/// in sync.
/// </summary>
public static class ToolResultJsonScanner
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Scans every blob in <paramref name="toolResultJson"/>. Malformed or error-shaped blobs are skipped, not thrown on.</summary>
    public static ToolResultScan Scan(IReadOnlyCollection<string> toolResultJson)
    {
        var citations = new HashSet<string>(StringComparer.Ordinal);
        // One resource can arrive twice in a turn: get_labs and get_interval_changes issue the same
        // FHIR query, and the brief calls both. Keyed by citation, which carries
        // the resource type, so one set is safe across all three record kinds.
        var collected = new HashSet<string>(StringComparer.Ordinal);
        List<MedicationRecord> medications = [];
        List<ObservationRecord> labs = [];
        List<ConditionRecord> problems = [];

        foreach (var json in toolResultJson)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                Walk(document.RootElement, citations, collected, medications, labs, problems);
            }
        }

        return new ToolResultScan(citations, new DomainConstraintInput(medications, labs, problems));
    }

    private static void Walk(
        JsonElement element,
        HashSet<string> citations,
        HashSet<string> collected,
        List<MedicationRecord> medications,
        List<ObservationRecord> labs,
        List<ConditionRecord> problems)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetString(element, "ResourceType", out var resourceType) && TryGetString(element, "Id", out var id))
                {
                    citations.Add($"{resourceType}/{id}");
                }

                if (HasAll(element, "MedicationDisplay", "Status", "Source"))
                {
                    AddIfNew(medications, collected, element.Deserialize<MedicationRecord>(DeserializeOptions), static m => m.Source.Citation);
                }
                else if (HasAll(element, "CodeDisplay", "Category", "Source"))
                {
                    AddIfNew(labs, collected, element.Deserialize<ObservationRecord>(DeserializeOptions), static o => o.Source.Citation);
                }
                else if (HasAll(element, "ProblemDisplay", "ClinicalStatus", "Source"))
                {
                    AddIfNew(problems, collected, element.Deserialize<ConditionRecord>(DeserializeOptions), static c => c.Source.Citation);
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, citations, collected, medications, labs, problems);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, citations, collected, medications, labs, problems);
                }

                break;
        }
    }

    // Identity, not value: two distinct resources carrying the same reading are two facts, and
    // collapsing them would hide one.
    private static void AddIfNew<T>(List<T> list, HashSet<string> collected, T? value, Func<T, string> citation)
    {
        if (value is not null && collected.Add(citation(value)))
        {
            list.Add(value);
        }
    }

    private static bool HasAll(JsonElement element, params string[] propertyNames) =>
        propertyNames.All(name => element.TryGetProperty(name, out _));

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
