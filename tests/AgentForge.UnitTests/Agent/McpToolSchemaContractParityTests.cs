using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using AgentForge.Agent;
using AgentForge.Mcp;

namespace AgentForge.UnitTests.Agent;

/// <summary>
/// NFR-CONTRACT-1 asks for the contract to be the source of truth rather than an implementation of
/// it. The request records in <c>AgentForge.Mcp</c> are that contract - <see cref="McpToolContract"/>
/// enforces their DataAnnotations before any FHIR call - and <see cref="McpToolCatalog"/> is what the
/// model is actually handed. This fixture is the guard that the two describe the same tool.
/// </summary>
/// <remarks>
/// It is deliberately written against the *serialized* schema and its own reading of the record's
/// attributes, sharing no helper with whatever builds the catalog. A parity check that imported the
/// builder's own name-mangling or attribute lookup would agree with it by construction and catch
/// nothing.
/// </remarks>
public sealed class McpToolSchemaContractParityTests
{
    /// <summary>
    /// The wire name the model sees for each model-fillable property, spelled out rather than derived.
    /// Running the generator's own naming policy here would agree with it by construction: the deleted
    /// schema literals were what pinned these names, and something has to go on doing it.
    /// </summary>
    private static readonly Dictionary<string, string> WireNames = new(StringComparer.Ordinal)
    {
        ["SinceDate"] = "since_date",
        ["Count"] = "count",
        ["DocumentType"] = "document_type",
    };

    /// <summary>
    /// Every JSON Schema keyword that can express a given <see cref="ValidationAttribute"/>, stated
    /// independently of the generator. Each inner array is one constraint the attribute imposes, and
    /// at least one of its keywords must be advertised. An attribute missing from this table fails the
    /// sweep: either the generator drops it (the bug) or the generator learned it and this guard did
    /// not (also the bug).
    /// </summary>
    private static readonly Dictionary<Type, string[][]> ConstraintKeywords = new()
    {
        // `required` and the empty-string floor have tests of their own, below.
        [typeof(RequiredAttribute)] = [],
        [typeof(RegularExpressionAttribute)] = [["pattern"]],
        [typeof(RangeAttribute)] = [["minimum", "exclusiveMinimum"], ["maximum", "exclusiveMaximum"]],
        [typeof(MinLengthAttribute)] = [["minLength"]],
        [typeof(MaxLengthAttribute)] = [["maxLength"]],
        [typeof(StringLengthAttribute)] = [["maxLength"]],
        [typeof(LengthAttribute)] = [["minLength"], ["maxLength"]],
        [typeof(AllowedValuesAttribute)] = [["enum"]],
        [typeof(DeniedValuesAttribute)] = [["not"]],
    };

    /// <summary>
    /// The Week 1 FHIR tools and the request contract each one validates against. The two Week 2
    /// tools (<c>get_document_facts</c>, <c>retrieve_evidence</c>) are absent because they have no
    /// request record at all - their input rules are hand-coded in <see cref="McpToolDispatcher"/>
    /// - and <see cref="RequestContracts_Always_AccountForEveryAdvertisedTool"/> is what stops that
    /// exemption growing quietly.
    /// </summary>
    private static readonly Dictionary<string, Type> RequestContracts = new(StringComparer.Ordinal)
    {
        ["get_patient_summary"] = typeof(GetPatientSummaryRequest),
        ["get_interval_changes"] = typeof(GetIntervalChangesRequest),
        ["get_labs"] = typeof(GetLabsRequest),
        ["get_vitals"] = typeof(GetVitalsRequest),
        ["get_recent_encounters"] = typeof(GetRecentEncountersRequest),
        ["get_documents"] = typeof(GetDocumentsRequest),
    };

    /// <summary>Tools with no request record, named one by one so a seventh cannot join them silently.</summary>
    private static readonly string[] ToolsWithoutARequestContract = ["get_document_facts", "retrieve_evidence"];

    public static TheoryData<string> ContractBackedTools()
    {
        var data = new TheoryData<string>();
        foreach (var toolName in RequestContracts.Keys)
        {
            data.Add(toolName);
        }

        return data;
    }

    [Fact]
    public void RequestContracts_Always_AccountForEveryAdvertisedTool()
    {
        // Given a tool the model can call, When this fixture runs, Then it is either checked against a
        // request contract or listed as knowingly having none - never simply missing from both.
        McpToolCatalog.AllTools.Select(t => t.Name).Should().BeEquivalentTo(
            RequestContracts.Keys.Concat(ToolsWithoutARequestContract),
            "a tool advertised to the model with neither a request contract nor a named exemption is "
            + "unchecked by this fixture, which is the drift it exists to catch");
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_OffersExactlyTheContractsModelFillableProperties(string toolName)
    {
        // Given a request record and the schema advertised for the same tool, When their property sets
        // differ in either direction, Then the model is being handed a different tool from the one the
        // server validates - a property added to the record but not the catalog is unreachable, and one
        // added to the catalog but not the record is silently discarded.
        var advertised = AdvertisedProperties(toolName);

        advertised.Select(p => p.Key).Should().BeEquivalentTo(
            ModelFillableProperties(RequestContracts[toolName]).Select(p => SchemaName(p.Name)),
            "'{0}' advertises the properties its request contract defines, no more and no fewer", toolName);
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_MarksExactlyThePropertiesItsContractRequires(string toolName)
    {
        // Given [Required] on the record, When the advertised schema omits it (or claims it where the
        // record does not), Then the model is told the wrong thing about what it may leave out.
        var required = JsonNode.Parse(SchemaOf(toolName))!["required"]?.AsArray()
            .Select(n => n!.GetValue<string>()) ?? [];

        required.Should().BeEquivalentTo(
            ModelFillableProperties(RequestContracts[toolName])
                .Where(p => p.GetCustomAttribute<RequiredAttribute>() is not null)
                .Select(p => SchemaName(p.Name)),
            "'{0}' marks required exactly what McpToolContract.Validate rejects the request without", toolName);
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_CarriesEveryRegularExpressionItsContractEnforces(string toolName)
    {
        // Given [RegularExpression] on the record, When the advertised schema carries no `pattern`, Then
        // the model can emit a value that satisfies the schema it was handed and is rejected server-side
        // anyway - the advertised contract being the weaker of the two. Prose in a `description` is not
        // a constraint; only `pattern` is.
        var advertised = AdvertisedProperties(toolName);

        foreach (var property in ModelFillableProperties(RequestContracts[toolName]))
        {
            if (property.GetCustomAttribute<RegularExpressionAttribute>() is not { } constraint)
            {
                continue;
            }

            var schemaName = SchemaName(property.Name);

            // Read the node out before asserting: in `advertised[name]?["pattern"].Should().Be(...)` the
            // null-conditional swallows the assertion along with the lookup, so a MISSING pattern passes.
            var advertisedPattern = advertised[schemaName]?["pattern"];

            advertisedPattern.Should().NotBeNull(
                "'{0}.{1}' is regex-constrained server-side, so the model must be handed that same "
                + "constraint as a JSON Schema pattern - prose in a description is not one", toolName, schemaName);
            advertisedPattern!.GetValue<string>().Should().Be(
                constraint.Pattern,
                "'{0}.{1}' must advertise the very pattern the server enforces, not a paraphrase of it",
                toolName, schemaName);
        }
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_CarriesEveryNumericBoundItsContractEnforces(string toolName)
    {
        // Given [Range] on the record, When the advertised schema omits minimum/maximum, Then the model
        // can ask for a count the server refuses - the same asymmetry as a missing pattern.
        var advertised = AdvertisedProperties(toolName);

        foreach (var property in ModelFillableProperties(RequestContracts[toolName]))
        {
            if (property.GetCustomAttribute<RangeAttribute>() is not { } bound)
            {
                continue;
            }

            var schemaName = SchemaName(property.Name);

            AdvertisedNumber(advertised, schemaName, "minimum").Should().Be(
                JsonSerializer.Serialize(bound.Minimum),
                "'{0}.{1}' has a server-side lower bound the model must be told about", toolName, schemaName);
            AdvertisedNumber(advertised, schemaName, "maximum").Should().Be(
                JsonSerializer.Serialize(bound.Maximum),
                "'{0}.{1}' has a server-side upper bound the model must be told about", toolName, schemaName);
        }
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_RepresentsEveryConstraintItsContractCarries(string toolName)
    {
        // The sweep the per-attribute theories above cannot be: `McpToolContract.Validate` calls
        // Validator.TryValidateObject with validateAllProperties, so the server enforces EVERY
        // ValidationAttribute on the record - not the handful this fixture happens to name. A constraint
        // the generator drops leaves the model a contract it can satisfy and the server still refuses,
        // which is the asymmetry exists to remove, arriving through the generator instead of a
        // hand-written literal.
        var advertised = AdvertisedProperties(toolName);

        foreach (var property in ModelFillableProperties(RequestContracts[toolName]))
        {
            var schemaName = SchemaName(property.Name);

            foreach (var constraint in property.GetCustomAttributes<ValidationAttribute>())
            {
                ConstraintKeywords.Should().ContainKey(
                    constraint.GetType(),
                    "'{0}.{1}' carries {2}, which the server enforces - this fixture must know which "
                    + "JSON Schema keyword expresses it before it can check the model is told",
                    toolName, schemaName, constraint.GetType().Name);

                foreach (var alternatives in ConstraintKeywords[constraint.GetType()])
                {
                    alternatives.Any(keyword => advertised[schemaName]?.AsObject().ContainsKey(keyword) == true)
                        .Should().BeTrue(
                            "'{0}.{1}' carries {2}, so the advertised schema must say so with one of: {3}",
                            toolName, schemaName, constraint.GetType().Name, string.Join(", ", alternatives));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void RequestContract_ForAContractBackedTool_CarriesNoConstraintAboveTheProperty(string toolName)
    {
        // The sweep above walks properties, and so does the generator - so a rule declared on the TYPE is
        // invisible to both while Validator.TryValidateObject still enforces it. The generator refuses to
        // build such a schema; this says so where the rule is read rather than leaving the only evidence a
        // TypeInitializationException somewhere else in the suite.
        var contract = RequestContracts[toolName];

        contract.GetCustomAttributes<ValidationAttribute>(inherit: true).Should().BeEmpty(
            "'{0}' would enforce a class-level rule the advertised schema has no keyword to carry", toolName);
        typeof(IValidatableObject).IsAssignableFrom(contract).Should().BeFalse(
            "'{0}' would enforce a cross-field rule the model is never told about", toolName);
    }

    [Fact]
    public void ConstraintSweep_Always_HasConstraintsToSweep()
    {
        // The sweep above is per tool, and two tools legitimately carry no constraint at all - so on its
        // own it would pass while the records quietly lost their attributes, the false green a filter
        // matching no tests produces. This states the enforced set instead. It reddens when a constraint
        // is REMOVED as well as added, which is the point: relaxing what the server enforces is exactly
        // the change says must never be how the two sides are brought into line.
        var enforced = RequestContracts.Values
            .SelectMany(ModelFillableProperties)
            .SelectMany(p => p.GetCustomAttributes<ValidationAttribute>())
            .Select(a => a.GetType().Name);

        enforced.Should().BeEquivalentTo(
            [
                "RequiredAttribute",            // get_interval_changes.since_date
                "RegularExpressionAttribute",   // get_interval_changes.since_date
                "RegularExpressionAttribute",   // get_labs.since_date
                "RegularExpressionAttribute",   // get_vitals.since_date
                "RangeAttribute",               // get_recent_encounters.count
            ],
            "these are the constraints the six tools enforce on model-supplied input; changing the set is "
            + "a contract change, and the sweep that checks they are advertised must have something to see");
    }

    [Theory]
    [MemberData(nameof(ContractBackedTools))]
    public void AdvertisedSchema_ForAContractBackedTool_RefusesAnEmptyStringWhereItsContractDoes(string toolName)
    {
        // [Required] with AllowEmptyStrings false rejects ""; JSON Schema `required` only demands the key
        // is present, so `required` alone is the weaker of the two.
        var advertised = AdvertisedProperties(toolName);

        foreach (var property in ModelFillableProperties(RequestContracts[toolName]))
        {
            if (property.GetCustomAttribute<RequiredAttribute>() is not { AllowEmptyStrings: false }
                || property.PropertyType != typeof(string))
            {
                continue;
            }

            var schemaName = SchemaName(property.Name);

            // Read it out first: a null-conditional here would swallow the assertion along with the
            // lookup, so an ABSENT minLength would pass - the trap this fixture already fell into once.
            var advertisedFloor = advertised[schemaName]?["minLength"]?.GetValue<int>() ?? 0;

            advertisedFloor.Should().BeGreaterThan(
                0,
                "'{0}.{1}' is required and rejects an empty string server-side, which `required` on its "
                + "own does not say", toolName, schemaName);
        }
    }

    [Fact]
    public void McpDateFilterPattern_Always_MeansTheSameThingInEcma262AsItDoesInDotNet()
    {
        // The server validates with .NET regex; the model is handed the identical literal as a JSON
        // Schema `pattern`, which is ECMA-262. The two dialects are close, not identical, so a pattern
        // that is safe to hand over has to be checked rather than assumed.
        //
        // RegexOptions.ECMAScript is .NET's own ECMA-262 character-class mode, and it is what makes the
        // difference visible in-process: under it \d is [0-9], whereas .NET's default \d is every Unicode
        // decimal digit. That is not a hypothetical - "ge٢٠٢٦" is accepted by default
        // .NET and rejected by ECMA-262, and the pattern therefore spells [0-9] explicitly.
        //
        // What RegexOptions.ECMAScript does NOT emulate is anchor semantics: it keeps .NET's `$`, which
        // also matches before a trailing newline. Real ECMA-262 `$` (no `m` flag) does not, and neither
        // does RegularExpressionAttribute, which requires the match to span the whole value. Checked
        // out-of-band against Node 2026-09-20 - /pattern/.test("ge2026-01-01\n") === false - and pinned
        // here against the attribute, which is the engine that actually guards the tool call.
        var dotnet = new Regex(McpDateFilter.Pattern, RegexOptions.None);
        var ecma = new Regex(McpDateFilter.Pattern, RegexOptions.ECMAScript);
        var server = new RegularExpressionAttribute(McpDateFilter.Pattern);

        string[] classifiedIdentically =
        [
            "ge2026-01-01", "ge2026-01", "ge2026", "eq2026-01-01", "ap2026-01-01",
            "2026-01-01", "xx2026-01-01", "ge26-01-01", "ge2026-1-1",
            "ge٢٠٢٦", "ge٢٠٢٦-٠١-٠١",
        ];

        foreach (var candidate in classifiedIdentically)
        {
            ecma.IsMatch(candidate).Should().Be(
                dotnet.IsMatch(candidate),
                "'{0}' must mean the same to the model's engine as to the server's", candidate);
        }

        foreach (var notWholeString in new[] { "ge2026-01-01\n", "\nge2026-01-01", "ge2026-01-01X", "ge2026-01-01\nge9999" })
        {
            server.IsValid(notWholeString).Should().BeFalse(
                "'{0}' is not a whole-string match, and ECMA-262 `$` rejects it too", notWholeString);
        }

        // The one place the two genuinely part, recorded rather than papered over: DataAnnotations treats
        // an empty value as absent and defers to [Required], while a JSON Schema `pattern` rejects "".
        // The asymmetry runs the safe way - the model cannot satisfy the advertised schema and then be
        // refused server-side - so it is accepted, not fixed.
        server.IsValid(string.Empty).Should().BeTrue();
        ecma.IsMatch(string.Empty).Should().BeFalse();
    }

    /// <summary>
    /// The advertised bound as JSON text, or a marker naming what was missing. Returning a string
    /// rather than a nullable node keeps the null-conditional out of the assertion, where it would
    /// swallow the failure it exists to report.
    /// </summary>
    private static string AdvertisedNumber(JsonObject advertised, string schemaName, string keyword) =>
        advertised[schemaName]?[keyword]?.ToJsonString() ?? $"(no '{keyword}' advertised for '{schemaName}')";

    private static string SchemaOf(string toolName) =>
        McpToolCatalog.AllTools.Single(t => t.Name == toolName).InputJsonSchema;

    private static JsonObject AdvertisedProperties(string toolName) =>
        JsonNode.Parse(SchemaOf(toolName))!["properties"]!.AsObject();

    // Session-bound inputs (FR-CHAT-3) are declared on the contract with [SessionBound], so this reads
    // the same source the generator does rather than matching on the two names the Week 1 records use.
    private static IEnumerable<PropertyInfo> ModelFillableProperties(Type requestType) =>
        requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SessionBoundAttribute>() is null);

    private static string SchemaName(string propertyName) =>
        WireNames.TryGetValue(propertyName, out var wireName)
            ? wireName
            : throw new InvalidOperationException(
                $"'{propertyName}' is a model-fillable property with no pinned wire name. Add the name the "
                + "model must see to WireNames - deriving it would agree with the generator by construction.");
}
