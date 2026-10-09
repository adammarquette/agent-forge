using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentForge.Mcp;

/// <summary>
/// Builds the model-facing JSON Schema for a tool from the request record that tool validates
/// against, so the schema the model reads and the contract <see cref="McpToolContract"/> enforces
/// are one definition rather than two kept in step by hand (NFR-CONTRACT-1 - "contracts must be the
/// source of truth, not the implementation").
/// </summary>
/// <remarks>
/// <para>
/// The invariant is one-directional: the advertised schema must never be <b>more permissive</b> than
/// the contract. <see cref="McpToolContract.Validate"/> runs <c>Validator.TryValidateObject</c> with
/// <c>validateAllProperties</c>, so the server enforces <i>every</i> <see cref="ValidationAttribute"/>
/// on the record - which means a constraint this builder cannot express must stop the build rather
/// than be dropped. A generated schema that quietly omits a constraint is worse than a hand-written
/// one, because it looks authoritative.
/// </para>
/// <para>
/// The record carries everything the model needs: the property, its type, whether it is required, the
/// constraints it must satisfy, and - via <see cref="DescriptionAttribute"/> - the prose the model
/// reads as instruction. That prose is prompt surface (`PROMPTS.md` §4), so editing
/// a <see cref="DescriptionAttribute"/> on a request record is editing a prompt.
/// </para>
/// </remarks>
public static class McpToolInputSchema
{
    /// <summary>Builds the advertised input schema for <typeparamref name="TRequest"/>.</summary>
    public static string For<TRequest>()
        where TRequest : notnull => For(typeof(TRequest));

    /// <summary>
    /// The exact attribute types this builder can express. Membership is by exact type, not assignability:
    /// a subclass can refuse more than its base while carrying the base's own data, so advertising the base
    /// constraint for it would tell the model a value is acceptable that the server rejects.
    /// </summary>
    private static readonly HashSet<Type> MappedConstraints =
    [
        typeof(RequiredAttribute),
        typeof(RegularExpressionAttribute),
        typeof(RangeAttribute),
        typeof(StringLengthAttribute),
        typeof(LengthAttribute),
        typeof(MinLengthAttribute),
        typeof(MaxLengthAttribute),
        typeof(AllowedValuesAttribute),
        typeof(DeniedValuesAttribute),
    ];

    /// <summary>Builds the advertised input schema for <paramref name="requestType"/>.</summary>
    /// <exception cref="NotSupportedException">
    /// A property's CLR type has no JSON Schema mapping, or a constraint on the property or the type itself
    /// cannot be expressed as one. Loud rather than lenient: any of them would leave the model a contract
    /// the server does not actually accept.
    /// </exception>
    public static string For(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        RejectConstraintsAboveTheProperty(requestType);

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in ModelFillableProperties(requestType))
        {
            var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            properties[name] = Describe(property);

            if (property.GetCustomAttribute<RequiredAttribute>() is not null)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        // The default encoder escapes an apostrophe as ' here. That is transient, not a wire
        // change: AnthropicRequestMapper re-parses this string and System.Text.Json escapes the
        // literal schemas' apostrophes exactly the same way on the way out.
        return schema.ToJsonString();
    }

    /// <summary>
    /// Refuses a contract whose rules sit above its properties. <c>Validator.TryValidateObject</c> applies
    /// type-level <see cref="ValidationAttribute"/>s and <see cref="IValidatableObject"/> as well as
    /// per-property ones, and neither has a JSON Schema keyword to live on - a cross-field rule mostly has
    /// no JSON Schema expression at all. So the answer is to refuse rather than to map: a schema silent
    /// about a rule the server refuses the call for is the asymmetry this builder exists to prevent, one
    /// level above where it was first found.
    /// </summary>
    private static void RejectConstraintsAboveTheProperty(Type requestType)
    {
        if (requestType.GetCustomAttributes<ValidationAttribute>(inherit: true).FirstOrDefault() is { } typeRule)
        {
            throw new NotSupportedException(
                $"'{requestType.Name}' carries {typeRule.GetType().Name} on the type itself. "
                + "McpToolContract.Validate enforces it, and no JSON Schema keyword on a property can carry "
                + "it - so the model would never be told. Express the rule per property, or do not enforce it "
                + "on a tool request.");
        }

        if (typeof(IValidatableObject).IsAssignableFrom(requestType))
        {
            throw new NotSupportedException(
                $"'{requestType.Name}' implements IValidatableObject. McpToolContract.Validate runs it, and a "
                + "cross-field rule has no JSON Schema expression - so the model would be refused for a rule "
                + "it was never shown. Express the rule per property, or do not enforce it on a tool request.");
        }
    }

    // MetadataToken orders by declaration, so the advertised schema is byte-stable across runs;
    // GetProperties' own order is not contractual.
    private static IEnumerable<PropertyInfo> ModelFillableProperties(Type requestType) =>
        requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SessionBoundAttribute>() is null)
            .OrderBy(p => p.MetadataToken);

    private static JsonObject Describe(PropertyInfo property)
    {
        var schemaType = SchemaTypeOf(property);
        var described = new JsonObject { ["type"] = schemaType };

        if (property.GetCustomAttribute<DescriptionAttribute>() is { } description)
        {
            described["description"] = description.Description;
        }

        // Ordered so the emitted keys do not depend on reflection's own attribute order.
        var constraints = property.GetCustomAttributes<ValidationAttribute>()
            .OrderBy(a => a.GetType().FullName, StringComparer.Ordinal);

        foreach (var constraint in constraints)
        {
            Apply(constraint, property, schemaType, described);
        }

        return described;
    }

    private static void Apply(ValidationAttribute constraint, PropertyInfo property, string schemaType, JsonObject described)
    {
        // Exact type, so a subclass of a mapped attribute lands here rather than on its base's arm.
        if (!MappedConstraints.Contains(constraint.GetType()))
        {
            throw Unmappable(constraint, property);
        }

        switch (constraint)
        {
            case RequiredAttribute { AllowEmptyStrings: false } when schemaType == "string":
                // `required` says the key must be present; it does not say "" is refused, and
                // DataAnnotations refuses it. Without this the advertised contract is the weaker one.
                AtLeast(described, "minLength", 1);
                break;

            case RequiredAttribute:
                break;

            case RegularExpressionAttribute expression:
                // Verbatim, not a translation: McpDateFilter.Pattern is written to mean the same thing
                // in ECMA-262 (which JSON Schema `pattern` is) as in .NET.
                described["pattern"] = expression.Pattern;
                break;

            case RangeAttribute range:
                ApplyRange(range, property, schemaType, described);
                break;

            case StringLengthAttribute length:
                RequireStringType(constraint, property, schemaType);
                if (length.MinimumLength > 0)
                {
                    AtLeast(described, "minLength", length.MinimumLength);
                }

                AtMost(described, "maxLength", length.MaximumLength);
                break;

            case LengthAttribute length:
                RequireStringType(constraint, property, schemaType);
                AtLeast(described, "minLength", length.MinimumLength);
                AtMost(described, "maxLength", length.MaximumLength);
                break;

            case MinLengthAttribute minimum:
                RequireStringType(constraint, property, schemaType);
                AtLeast(described, "minLength", minimum.Length);
                break;

            case MaxLengthAttribute maximum:
                RequireStringType(constraint, property, schemaType);
                AtMost(described, "maxLength", maximum.Length);
                break;

            case AllowedValuesAttribute allowed:
                described["enum"] = AsJsonArray(allowed.Values);
                break;

            case DeniedValuesAttribute denied:
                described["not"] = new JsonObject { ["enum"] = AsJsonArray(denied.Values) };
                break;

            // Unreachable while MappedConstraints and the arms above agree; kept so they cannot drift apart
            // silently, which is the failure mode this whole type exists to refuse.
            default:
                throw Unmappable(constraint, property);
        }
    }

    private static NotSupportedException Unmappable(ValidationAttribute constraint, PropertyInfo property) =>
        new($"No JSON Schema mapping for {constraint.GetType().Name} on "
            + $"'{property.DeclaringType?.Name}.{property.Name}'. McpToolContract.Validate enforces it, "
            + "so advertising a schema without it would hand the model a contract the server refuses - "
            + "map it here rather than dropping it.");

    private static void ApplyRange(RangeAttribute range, PropertyInfo property, string schemaType, JsonObject described)
    {
        if (schemaType is not ("integer" or "number"))
        {
            throw new NotSupportedException(
                $"[Range] on non-numeric '{property.DeclaringType?.Name}.{property.Name}' has no JSON Schema "
                + "mapping - a bound the model reads as a string constrains nothing.");
        }

        // An exclusive bound advertised as an inclusive one is permissive in exactly the direction that
        // costs a refused tool call mid-brief.
        described[range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum"] = AsJsonNumber(range.Minimum, property);
        described[range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum"] = AsJsonNumber(range.Maximum, property);
    }

    private static void RequireStringType(ValidationAttribute constraint, PropertyInfo property, string schemaType)
    {
        if (schemaType != "string")
        {
            throw new NotSupportedException(
                $"{constraint.GetType().Name} on non-string '{property.DeclaringType?.Name}.{property.Name}' "
                + "has no JSON Schema mapping.");
        }
    }

    // Keep the stricter of two floors when attributes overlap, never the looser.
    private static void AtLeast(JsonObject described, string keyword, int value)
    {
        if (described[keyword]?.GetValue<int>() is not { } existing || value > existing)
        {
            described[keyword] = value;
        }
    }

    private static void AtMost(JsonObject described, string keyword, int value)
    {
        if (described[keyword]?.GetValue<int>() is not { } existing || value < existing)
        {
            described[keyword] = value;
        }
    }

    private static JsonArray AsJsonArray(object?[] values) =>
        [.. values.Select(v => JsonNode.Parse(JsonSerializer.Serialize(v)))];

    private static JsonNode AsJsonNumber(object bound, PropertyInfo property)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(bound));
        return node?.GetValueKind() == JsonValueKind.Number
            ? node
            : throw new NotSupportedException(
                $"[Range] bound '{bound}' on '{property.DeclaringType?.Name}.{property.Name}' is not a JSON number.");
    }

    private static string SchemaTypeOf(PropertyInfo property) =>
        Type.GetTypeCode(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) switch
        {
            TypeCode.String => "string",
            TypeCode.Boolean => "boolean",
            TypeCode.Byte or TypeCode.SByte
                or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32
                or TypeCode.Int64 or TypeCode.UInt64 => "integer",
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
            // Loud rather than lenient: an untyped parameter is a contract the model cannot satisfy.
            _ => throw new NotSupportedException(
                $"No JSON Schema mapping for '{property.PropertyType}' on "
                + $"'{property.DeclaringType?.Name}.{property.Name}' - add one rather than advertising an "
                + "untyped parameter."),
        };
}
