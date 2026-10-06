using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using FluentAssertions;
using AgentForge.Mcp;

namespace AgentForge.UnitTests.Mcp;

/// <summary>
/// The generator's central invariant: the advertised schema is **never more permissive** than the
/// contract <see cref="McpToolContract"/> enforces. <c>Validator.TryValidateObject(…,
/// validateAllProperties: true)</c> applies every <see cref="ValidationAttribute"/> on the record,
/// so every one of them has to reach the schema — or the build has to stop.
/// </summary>
/// <remarks>
/// Driven by purpose-built records rather than the six real ones, because the point is what happens
/// to a constraint <em>nobody has added yet</em>. A silently-dropped constraint hands the model a
/// contract it can satisfy and the server will still refuse, which is the asymmetry exists to
/// remove. A separate change
/// </remarks>
public sealed class McpToolInputSchemaTests
{
    [Fact]
    public void For_PropertyCarryingAConstraintTheSchemaCannotExpress_ThrowsRatherThanDroppingIt()
    {
        // Given a validation attribute the generator has no JSON Schema mapping for, When a schema is
        // built, Then it fails loudly - the same rule SchemaTypeOf already applies to an unmapped CLR
        // type. Degrading quietly is what makes a generated schema worse than a hand-written one: it
        // looks authoritative.
        var act = () => McpToolInputSchema.For<UnmappedConstraintRequest>();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*EmailAddressAttribute*", "the message must name the attribute that stopped the build")
            .And.Message.Should().Contain("Contact");
    }

    [Fact]
    public void For_RequestTypeCarryingAClassLevelConstraint_ThrowsRatherThanAdvertisingASchemaSilentAboutIt()
    {
        // Validator.TryValidateObject applies constraints declared on the TYPE as well as on its properties,
        // and Describe only ever walks properties. A class-level rule is not expressible as a keyword on any
        // one property, so the model would be told nothing and the server would refuse the call - the round-1
        // asymmetry one level up. Refusing is the only answer that keeps the guarantee true. A separate change
        var act = () => McpToolInputSchema.For<ClassLevelConstraintRequest>();

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*CrossFieldRuleAttribute*")
            .And.Message.Should().Contain("ClassLevelConstraintRequest");
    }

    [Fact]
    public void For_RequestTypeValidatingItselfAcrossFields_ThrowsRatherThanAdvertisingASchemaSilentAboutIt()
    {
        // IValidatableObject is the house idiom for cross-field rules here, so this is a reachable next edit
        // rather than a hypothetical - and a cross-field rule has no JSON Schema keyword to carry it.
        var act = () => McpToolInputSchema.For<SelfValidatingRequest>();

        act.Should().Throw<NotSupportedException>().WithMessage("*IValidatableObject*");
    }

    [Fact]
    public void For_PropertyCarryingASubclassOfAMappedConstraint_ThrowsRatherThanAdvertisingOnlyTheBaseRule()
    {
        // A subclass can tighten what the base attribute accepts while carrying the base's own data, so
        // advertising the base constraint would tell the model a value is fine that the server refuses.
        // Matching the attribute's exact type is what keeps "never more permissive" true here.
        var act = () => McpToolInputSchema.For<SubclassedConstraintRequest>();

        act.Should().Throw<NotSupportedException>().WithMessage("*StricterDateFilterAttribute*");
    }

    [Fact]
    public void For_ExclusiveRange_AdvertisesExclusiveBoundsRatherThanInclusiveOnes()
    {
        // Given [Range(1, 20, MinimumIsExclusive = true)], When advertised as `minimum: 1`, Then the
        // model is told 1 is allowed and the server rejects it. The permissive direction is the one
        // that costs a tool call mid-brief.
        var bounds = PropertyOf<ExclusiveRangeRequest>("count");

        Keyword(bounds, "exclusiveMinimum").Should().Be("1");
        Keyword(bounds, "exclusiveMaximum").Should().Be("20");
        bounds.ContainsKey("minimum").Should().BeFalse();
        bounds.ContainsKey("maximum").Should().BeFalse();
    }

    [Fact]
    public void For_InclusiveRange_AdvertisesInclusiveBounds()
    {
        var bounds = PropertyOf<InclusiveRangeRequest>("count");

        Keyword(bounds, "minimum").Should().Be("1");
        Keyword(bounds, "maximum").Should().Be("20");
        bounds.ContainsKey("exclusiveMinimum").Should().BeFalse();
    }

    [Fact]
    public void For_RangeOnANonNumericProperty_ThrowsRatherThanAdvertisingAStringAsABound()
    {
        var act = () => McpToolInputSchema.For<DateRangeRequest>();

        act.Should().Throw<NotSupportedException>().WithMessage("*Range*");
    }

    [Fact]
    public void For_MinLengthAndMaxLength_AdvertisesBothLengthBounds()
    {
        var advertised = PropertyOf<LengthBoundedRequest>("document_type");

        Keyword(advertised, "minLength").Should().Be("3");
        Keyword(advertised, "maxLength").Should().Be("40");
    }

    [Fact]
    public void For_StringLength_AdvertisesBothLengthBounds()
    {
        var advertised = PropertyOf<StringLengthRequest>("document_type");

        Keyword(advertised, "minLength").Should().Be("3");
        Keyword(advertised, "maxLength").Should().Be("40");
    }

    [Fact]
    public void For_Length_AdvertisesBothLengthBounds()
    {
        var advertised = PropertyOf<LengthAttributeRequest>("document_type");

        Keyword(advertised, "minLength").Should().Be("3");
        Keyword(advertised, "maxLength").Should().Be("40");
    }

    [Fact]
    public void For_AllowedValues_AdvertisesThemAsAnEnum()
    {
        var advertised = PropertyOf<AllowedValuesRequest>("document_type");

        Keyword(advertised, "enum").Should().Be("""["echo","ecg"]""");
    }

    [Fact]
    public void For_DeniedValues_AdvertisesThemAsANegatedEnum()
    {
        var advertised = PropertyOf<DeniedValuesRequest>("document_type");

        Keyword(advertised, "not").Should().Be("""{"enum":["banana"]}""");
    }

    [Fact]
    public void For_RequiredStringRejectingEmpty_AdvertisesAMinimumLengthAsWellAsRequiredness()
    {
        // JSON Schema `required` only demands the key is present - "" satisfies it. [Required] with
        // AllowEmptyStrings false does not, so `required` on its own is the weaker statement.
        var schema = JsonNode.Parse(McpToolInputSchema.For<RequiredStringRequest>())!;

        schema["required"]!.ToJsonString().Should().Be("""["query"]""");
        Keyword(schema["properties"]!["query"]!.AsObject(), "minLength").Should().Be("1");
    }

    [Fact]
    public void For_RequiredStringAllowingEmpty_AdvertisesRequirednessOnly()
    {
        var schema = JsonNode.Parse(McpToolInputSchema.For<RequiredEmptyAllowedRequest>())!;

        schema["required"]!.ToJsonString().Should().Be("""["query"]""");
        schema["properties"]!["query"]!.AsObject().ContainsKey("minLength").Should().BeFalse();
    }

    [Fact]
    public void For_SessionBoundProperty_IsExcludedWhateverItIsCalled()
    {
        // The exclusion is a property of the contract, not a match on the two names the Week 1 records
        // happen to use - a record calling its patient id something else must still not offer it.
        var schema = JsonNode.Parse(McpToolInputSchema.For<OddlyNamedSessionBoundRequest>())!;

        schema["properties"]!.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["query"]);
        schema["required"]!.ToJsonString().Should().Be("""["query"]""");
    }

    private static JsonObject PropertyOf<TRequest>(string schemaName)
        where TRequest : notnull =>
        JsonNode.Parse(McpToolInputSchema.For<TRequest>())!["properties"]![schemaName]!.AsObject();

    /// <summary>
    /// The advertised keyword as JSON text, or a marker naming what was missing. Returning a string
    /// rather than a nullable node keeps the null-conditional out of the assertion, where it would
    /// swallow the failure along with the lookup and pass on an ABSENT keyword.
    /// </summary>
    private static string Keyword(JsonObject advertised, string keyword) =>
        advertised[keyword]?.ToJsonString() ?? $"(no '{keyword}' advertised)";

    private sealed record UnmappedConstraintRequest
    {
        [EmailAddress]
        public string? Contact { get; init; }
    }

    /// <summary>A rule that spans the whole object, as a class-level attribute - what Describe never walks.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    private sealed class CrossFieldRuleAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value) => false;
    }

    /// <summary>Carries the base's pattern but refuses more than it - the hazard in matching on the base type.</summary>
    private sealed class StricterDateFilterAttribute() : RegularExpressionAttribute(McpDateFilter.Pattern)
    {
        public override bool IsValid(object? value) => false;
    }

    [CrossFieldRule]
    private sealed record ClassLevelConstraintRequest
    {
        [Required(AllowEmptyStrings = false)]
        public required string Query { get; init; }
    }

    private sealed record SelfValidatingRequest : IValidatableObject
    {
        [Required(AllowEmptyStrings = false)]
        public required string From { get; init; }

        [Required(AllowEmptyStrings = false)]
        public required string To { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
            string.CompareOrdinal(From, To) > 0 ? [new ValidationResult("From must not be after To.")] : [];
    }

    private sealed record SubclassedConstraintRequest
    {
        [StricterDateFilter]
        public string? SinceDate { get; init; }
    }

    private sealed record ExclusiveRangeRequest
    {
        [Range(1, 20, MinimumIsExclusive = true, MaximumIsExclusive = true)]
        public int Count { get; init; }
    }

    private sealed record InclusiveRangeRequest
    {
        [Range(1, 20)]
        public int Count { get; init; }
    }

    private sealed record DateRangeRequest
    {
        [Range(typeof(DateTime), "2026-01-01", "2026-12-31")]
        public string? On { get; init; }
    }

    private sealed record LengthBoundedRequest
    {
        [MinLength(3)]
        [MaxLength(40)]
        public string? DocumentType { get; init; }
    }

    private sealed record StringLengthRequest
    {
        [StringLength(40, MinimumLength = 3)]
        public string? DocumentType { get; init; }
    }

    private sealed record LengthAttributeRequest
    {
        [Length(3, 40)]
        public string? DocumentType { get; init; }
    }

    private sealed record AllowedValuesRequest
    {
        [AllowedValues("echo", "ecg")]
        public string? DocumentType { get; init; }
    }

    private sealed record DeniedValuesRequest
    {
        [DeniedValues("banana")]
        public string? DocumentType { get; init; }
    }

    private sealed record RequiredStringRequest
    {
        [Description("A question.")]
        [Required(AllowEmptyStrings = false)]
        public required string Query { get; init; }
    }

    private sealed record RequiredEmptyAllowedRequest
    {
        [Required(AllowEmptyStrings = true)]
        public required string Query { get; init; }
    }

    private sealed record OddlyNamedSessionBoundRequest
    {
        [SessionBound]
        [Required(AllowEmptyStrings = false)]
        public required string SubjectPatientId { get; init; }

        [SessionBound]
        [Required(AllowEmptyStrings = false)]
        public required string SiteId { get; init; }

        [Required(AllowEmptyStrings = true)]
        public required string Query { get; init; }
    }
}
