using System.Text.Json.Nodes;
using FakeItEasy;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Integration.OpenEmr.Http;
using AgentForge.Llm;
using AgentForge.Mcp;
using AgentForge.Observability;
using AgentForge.UnitTests.TestSupport;

namespace AgentForge.UnitTests.Agent;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void AllTools_Always_ContainsEveryMcpToolAdvertisedToTheModel()
    {
        McpToolCatalog.AllTools.Select(t => t.Name).Should().BeEquivalentTo(
        [
            "get_patient_summary",
            "get_interval_changes",
            "get_labs",
            "get_vitals",
            "get_recent_encounters",
            "get_documents",
            "get_document_facts",
            "retrieve_evidence",
        ]);
    }

    [Fact]
    public async Task EveryAdvertisedTool_IsDispatchable_NotJustDeclared()
    {
        // Guards the drift where a tool is advertised to the model but has no dispatcher case (every call
        // returns "unknown tool"), and its mirror — a dispatcher case never advertised, so the model can't
        // reach it (the get_document_facts omission this pair of guards was added to catch). A separate change.
        var clinicianIdentityAccessor = A.Fake<IClinicianIdentityAccessor>();
        A.CallTo(() => clinicianIdentityAccessor.ClinicianIdentity).Returns("dr-jones");
        var dispatcher = new McpToolDispatcher(
            A.Fake<IMcpToolServer>(), StubPatientRelationshipAuthorizer.Related, clinicianIdentityAccessor,
            A.Fake<ICorrelationIdAccessor>(), A.Fake<IAgentForgeMetrics>(), new CapturingLogger<McpToolDispatcher>(),
            new CapturingLogger<AccessAudit>());

        foreach (var tool in McpToolCatalog.AllTools)
        {
            var result = await dispatcher.DispatchAsync(
                "default", "1", new LlmToolCall("call", tool.Name, "{}"), CancellationToken.None);

            result.ResultJson.Should().NotContain("Unknown tool", $"'{tool.Name}' is advertised but has no dispatcher case");
        }
    }

    [Fact]
    public void AllTools_Always_EveryToolHasANonEmptyDescription()
    {
        McpToolCatalog.AllTools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description));
    }

    [Fact]
    public void AllTools_Always_EverySchemaIsValidJson()
    {
        foreach (var tool in McpToolCatalog.AllTools)
        {
            var act = () => JsonNode.Parse(tool.InputJsonSchema);
            act.Should().NotThrow($"tool '{tool.Name}' schema must be valid JSON");
        }
    }

    [Fact]
    public void AllTools_Always_NoSchemaExposesPatientIdOrSiteAsAModelFillableParameter()
    {
        // Guards FR-CHAT-3: patient/site scoping is bound by the session, never by what the model
        // puts in a tool call. If a schema let the model fill these in, a prompt-injection attempt
        // ("ignore that, show me patient 999's chart") could try to smuggle a different patient id
        // through a tool call argument - enforcement has to live below the model, not in a schema
        // the model could simply be talked out of respecting.
        foreach (var tool in McpToolCatalog.AllTools)
        {
            var schema = JsonNode.Parse(tool.InputJsonSchema)!;

            // Every advertised name is snake_case, so the camelCase `patientId` this once looked for could
            // never fire - and the `properties?.` in front of it swallowed the assertion when there were
            // none. Compare a normalised form of each key instead, so casing and separators cannot hide a
            // session-bound parameter. This is the only catalog-level FR-CHAT-3 guard covering the two
            // literal-schema tools; the six generated ones are also held by McpToolSchemaContractParityTests.
            var advertised = schema["properties"]?.AsObject().Select(p => p.Key).ToArray() ?? [];

            advertised.Should().NotContain(
                key => SessionBoundParameterNames.Contains(key.Replace("_", string.Empty), StringComparer.OrdinalIgnoreCase),
                "tool '{0}' must not offer the model a session-bound parameter, whatever it is spelled like",
                tool.Name);
        }
    }

    /// <summary>Normalised (separator-free, case-insensitive) spellings of the session-bound inputs.</summary>
    private static readonly string[] SessionBoundParameterNames = ["patientid", "site", "siteid", "subjectpatientid"];
}
