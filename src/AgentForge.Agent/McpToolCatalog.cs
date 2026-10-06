using System.Collections.Frozen;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentForge.Llm;
using AgentForge.Mcp;

namespace AgentForge.Agent;

/// <summary>
/// Describes the MCP tools (ARCHITECTURE.md §8.1) to the model, and - since a separate change - <b>is</b> the
/// set the model may cause to run, not only the set it is told about. Deliberately excludes
/// patientId and site from every schema - which patient and which OpenEMR site is session-bound,
/// resolved by the orchestrator from the authenticated launch context, never something the model
/// fills in on a tool call. This is the schema-level half of FR-CHAT-3's patient-scoping
/// enforcement; the tool dispatcher that forces the session's patient id regardless of what a
/// tool call argument says is the other half.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list below is the copilot's whole action surface, and that is what makes NG1's third verb
/// true.</b> <c>REQUIREMENTS.md</c> §12.4 says the Copilot "does not diagnose, recommend treatment, or place
/// orders". Placing an order is not something the model can say its way into - it would have to
/// <i>call</i> something - so unlike the other two verbs it is decidable here rather than in prose.
/// Every entry is a read; <see cref="Offers"/> is what the dispatcher gates on, so a tool wired into
/// its router and left off this list is inert. Adding a tool that changes the record means moving
/// NG1 and NG5 first (<c>ClinicalScopeGuardrailTests</c> is the bar).
/// </para>
/// <para>
/// The six Week 1 FHIR tools advertise a schema <b>generated from the request record the tool
/// validates against</b> (<see cref="McpToolInputSchema"/>), so the contract has one definition
/// rather than two kept in step by hand (NFR-CONTRACT-1). Descriptions - both the tool's own and
/// each parameter's - are prompt surface and stay hand-written; a parameter's lives on the record
/// as a <c>[Description]</c>. The two Week 2 tools have no request record to generate from: their
/// input rules are hand-coded in <see cref="McpToolDispatcher"/>, so their schemas are still
/// literals. <c>McpToolSchemaContractParityTests</c> holds every tool to one side of that line.
/// </para>
/// </remarks>
public static class McpToolCatalog
{
    /// <summary>Every tool the orchestrator may offer the model.</summary>
    public static IReadOnlyList<LlmToolDefinition> AllTools { get; } =
    [
        new LlmToolDefinition(
            "get_patient_summary",
            "Demographics, active problems, active medications, and allergies for the patient in " +
            "context, as one bounded bundle. Call this first, at the start of a brief.",
            McpToolInputSchema.For<GetPatientSummaryRequest>()),

        new LlmToolDefinition(
            "get_interval_changes",
            "Medication changes, new labs, and interval encounters since a given date - the " +
            "\"what changed since last visit\" diff. since_date is required.",
            McpToolInputSchema.For<GetIntervalChangesRequest>()),

        new LlmToolDefinition(
            "get_labs",
            "Lab Observations (INR, K+, creatinine, lipids, BNP, etc.) with values, units, dates, " +
            "and reference ranges. Optionally bounded to an interval.",
            McpToolInputSchema.For<GetLabsRequest>()),

        new LlmToolDefinition(
            "get_vitals",
            "Vital-signs Observations (blood pressure, heart rate). Optionally bounded to an interval.",
            McpToolInputSchema.For<GetVitalsRequest>()),

        new LlmToolDefinition(
            "get_recent_encounters",
            "A thin list (date, type, reason) of the most recent encounters. Request detail on a " +
            "specific one explicitly rather than assuming it from this list.",
            McpToolInputSchema.For<GetRecentEncountersRequest>()),

        new LlmToolDefinition(
            "get_documents",
            "Echo/EF reports and device interrogation narratives (DiagnosticReport and " +
            "DocumentReference). Values here are narrative text, not structured fields - any " +
            "specific value you extract from them (e.g. an ejection-fraction percentage) must be " +
            "labeled as derived, not stated as a directly-recorded fact.",
            McpToolInputSchema.For<GetDocumentsRequest>()),

        new LlmToolDefinition(
            "get_document_facts",
            "Facts the copilot already extracted from the patient's ingested documents (e.g. an uploaded " +
            "lab-report PDF), each citable as [Document/<id>] so the clinician can open the source document " +
            "at the exact region. Prefer citing these for a document-sourced value rather than restating it " +
            "as a directly-recorded fact.",
            """{"type":"object","properties":{}}"""),

        new LlmToolDefinition(
            "retrieve_evidence",
            "Search the clinical-guideline corpus for grounded snippets relevant to a question or claim, each " +
            "citable as [Guideline/<id>]. Use it to ground a recommendation in guidance rather than asserting " +
            "it unsupported; query is required.",
            """
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "The clinical question or claim to ground, e.g. 'target INR for atrial fibrillation on warfarin'."
                }
              },
              "required": ["query"]
            }
            """),
    ];

    /// <summary>
    /// What a tool call naming something outside <see cref="AllTools"/> is answered with. Says the
    /// scope limit rather than "unknown tool", because both things that land here need the same
    /// answer: a name the model invented, and a name record content told it to call
    /// (<c>NFR-SEC-2</c>). Neither should read as "try a different spelling".
    /// </summary>
    public const string OutOfScopeRefusalMessage =
        "That tool is not available. This copilot is read-only: it surfaces and cites this patient's " +
        "record and cannot order, prescribe, or change anything in it.";

    private static readonly FrozenSet<string> OfferedToolNames =
        AllTools.Select(tool => tool.Name).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="toolName"/> is one the model may actually cause to run. The dispatcher
    /// decides from this rather than from its own router, so the advertised surface and the executable
    /// surface cannot drift apart - which is the whole of NG1's "does not place orders" (see the class
    /// remarks). Ordinal: a tool name is a protocol token, never user text.
    /// </summary>
    public static bool Offers(string toolName) => OfferedToolNames.Contains(toolName);

    // Relaxed escaping keeps quotes readable in the committed file's diff; the file is never served as HTML.
    private static readonly JsonSerializerOptions RenderOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Renders <see cref="AllTools"/> - each tool's name, description and input schema, in catalog order - as
    /// one JSON document (<c>--export-tool-schemas</c>), so the tool-schema rendering is produced rather
    /// than written and a change to what the model is handed shows as a diff of that file. A separate change
    /// </summary>
    /// <returns>The document, indented with LF line endings and a trailing newline.</returns>
    public static string RenderSchemas()
    {
        var tools = new JsonArray();
        foreach (var tool in AllTools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputJsonSchema),
            });
        }

        var document = new JsonObject
        {
            ["title"] = "AgentForge MCP tools - the schemas advertised to the model",
            ["description"] =
                "Generated by scripts/generate-tool-schemas.sh from AgentForge.Agent.McpToolCatalog; do not edit "
                + "by hand. CONVENTIONS.md section 14 says which changes are breaking.",
            ["tools"] = tools,
        };

        return document.ToJsonString(RenderOptions) + "\n";
    }
}
