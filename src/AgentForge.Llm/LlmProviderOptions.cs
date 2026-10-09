using System.ComponentModel.DataAnnotations;

namespace AgentForge.Llm;

/// <summary>
/// LLM provider configuration, bound via the Options pattern (CONVENTIONS.md §6).
/// Pricing is configured rather than hard-coded: it changes independently of a code release, and
/// REQUIREMENTS.md §15.1's grounding rule requires cost figures to come from measurement/configuration,
/// not an assumed-correct constant baked into source.
/// </summary>
public sealed class LlmProviderOptions : IValidatableObject
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Llm";

    /// <summary>Default per-attempt LLM timeout (seconds); see <see cref="AttemptTimeoutSeconds"/>.</summary>
    public const int DefaultAttemptTimeoutSeconds = 60;

    /// <summary>Default total LLM request timeout (seconds); see <see cref="TotalRequestTimeoutSeconds"/>.</summary>
    public const int DefaultTotalRequestTimeoutSeconds = 150;

    /// <summary>Base URL of the Anthropic Messages API, the default when <see cref="BaseUrl"/> is not set.</summary>
    public const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>Base URL of the Gemini API, the default when <see cref="BaseUrl"/> is not set for Gemini.</summary>
    public const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly string? _baseUrl;

    /// <summary>
    /// Which provider serves every model call. Defaults to Anthropic, so an environment that
    /// sets nothing keeps the provider it had.
    /// </summary>
    public LlmProviderKind Provider { get; init; } = LlmProviderKind.Anthropic;

    /// <summary>Provider API key. Never logged, never in source (CONVENTIONS.md §11).</summary>
    [Required(AllowEmptyStrings = false)]
    public required string ApiKey { get; init; }

    /// <summary>Model identifier, e.g. a Sonnet-class model (ARCHITECTURE.md §12).</summary>
    [Required(AllowEmptyStrings = false)]
    public required string Model { get; init; }

    /// <summary>
    /// Provider API base URL. Unset or blank means the configured <see cref="Provider"/>'s own API, so switching
    /// provider does not also require a base URL; an explicit value (a proxy, a test double) wins.
    /// </summary>
    public string BaseUrl
    {
        get => string.IsNullOrWhiteSpace(_baseUrl) ? DefaultBaseUrlFor(Provider) : _baseUrl;
        init => _baseUrl = value;
    }

    /// <summary>Published input-token price, for the cost estimate in <see cref="LlmUsage"/>.</summary>
    public required decimal InputPricePerMillionTokensUsd { get; init; }

    /// <summary>Published output-token price, for the cost estimate in <see cref="LlmUsage"/>.</summary>
    public required decimal OutputPricePerMillionTokensUsd { get; init; }

    /// <summary>
    /// Per-attempt timeout (seconds) for a single LLM HTTP call. Default 60s: the framework's 10s HTTP
    /// default is too tight for the heaviest agenda-synthesis prompt, forcing a deterministic-fallback
    /// degrade. Governs the resilience handler's AttemptTimeout.
    /// </summary>
    public int AttemptTimeoutSeconds { get; init; } = DefaultAttemptTimeoutSeconds;

    /// <summary>
    /// Total timeout (seconds) across all retries for one LLM request. Default 150s. Must be strictly
    /// greater than a single attempt or the standard resilience handler throws at startup; governs
    /// TotalRequestTimeout.
    /// </summary>
    public int TotalRequestTimeoutSeconds { get; init; } = DefaultTotalRequestTimeoutSeconds;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Provider))
        {
            yield return new ValidationResult(
                $"{nameof(Provider)} must be one of: {string.Join(", ", Enum.GetNames<LlmProviderKind>())}.",
                [nameof(Provider)]);
        }

        if (AttemptTimeoutSeconds <= 0)
        {
            yield return new ValidationResult(
                $"{nameof(AttemptTimeoutSeconds)} must be greater than zero.",
                [nameof(AttemptTimeoutSeconds)]);
        }

        if (TotalRequestTimeoutSeconds <= AttemptTimeoutSeconds)
        {
            yield return new ValidationResult(
                $"{nameof(TotalRequestTimeoutSeconds)} must be greater than {nameof(AttemptTimeoutSeconds)}.",
                [nameof(TotalRequestTimeoutSeconds)]);
        }

        if (InputPricePerMillionTokensUsd < 0)
        {
            yield return new ValidationResult(
                $"{nameof(InputPricePerMillionTokensUsd)} cannot be negative.",
                [nameof(InputPricePerMillionTokensUsd)]);
        }

        if (OutputPricePerMillionTokensUsd < 0)
        {
            yield return new ValidationResult(
                $"{nameof(OutputPricePerMillionTokensUsd)} cannot be negative.",
                [nameof(OutputPricePerMillionTokensUsd)]);
        }
    }

    private static string DefaultBaseUrlFor(LlmProviderKind provider) =>
        provider == LlmProviderKind.Gemini ? GeminiDefaultBaseUrl : AnthropicDefaultBaseUrl;
}
