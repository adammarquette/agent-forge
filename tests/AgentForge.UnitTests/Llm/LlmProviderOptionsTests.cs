using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using AgentForge.Llm;

namespace AgentForge.UnitTests.Llm;

public sealed class LlmProviderOptionsTests
{
    [Fact]
    public void Validate_AllRequiredFieldsPresentAndNonNegativePricing_ProducesNoErrors()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        var results = Validate(options);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Validate_DefaultBaseUrl_IsAnthropicApi()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        options.BaseUrl.Should().Be("https://api.anthropic.com");
    }

    [Fact]
    public void Validate_NegativeInputPrice_ProducesError()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = -1.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.InputPricePerMillionTokensUsd)));
    }

    [Fact]
    public void Validate_NegativeOutputPrice_ProducesError()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = -1.00m,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.OutputPricePerMillionTokensUsd)));
    }

    [Fact]
    public void Timeouts_Defaults_AreTunedForLlmSynthesis()
    {
        // Regression: the framework's 10s/30s HTTP defaults are too tight for the heaviest
        // agenda synthesis prompt, forcing a deterministic-fallback degrade. These defaults give the LLM
        // call room to complete without over-extending the demo.
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        options.AttemptTimeoutSeconds.Should().Be(60);
        options.TotalRequestTimeoutSeconds.Should().Be(150);
    }

    [Fact]
    public void Validate_NonPositiveAttemptTimeout_ProducesError()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
            AttemptTimeoutSeconds = 0,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.AttemptTimeoutSeconds)));
    }

    [Fact]
    public void Validate_TotalTimeoutBelowAttemptTimeout_ProducesError()
    {
        // The standard resilience handler requires the total budget to be >= a single attempt, or it
        // throws at startup - validate here so a misconfiguration fails fast with a clear message.
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
            AttemptTimeoutSeconds = 60,
            TotalRequestTimeoutSeconds = 30,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.TotalRequestTimeoutSeconds)));
    }

    [Fact]
    public void Validate_MissingApiKey_ProducesError()
    {
        var options = new LlmProviderOptions
        {
            ApiKey = string.Empty,
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.ApiKey)));
    }

    [Fact]
    public void Provider_NotConfigured_DefaultsToAnthropic()
    {
        // Every environment that predates sets no Llm__Provider and must keep running on Anthropic.
        var options = new LlmProviderOptions
        {
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        options.Provider.Should().Be(LlmProviderKind.Anthropic);
    }

    [Fact]
    public void BaseUrl_GeminiWithNoBaseUrlConfigured_DefaultsToTheGenerativeLanguageApi()
    {
        var options = new LlmProviderOptions
        {
            Provider = LlmProviderKind.Gemini,
            ApiKey = "test-key",
            Model = "gemini-test-model",
            InputPricePerMillionTokensUsd = 0m,
            OutputPricePerMillionTokensUsd = 0m,
        };

        options.BaseUrl.Should().Be("https://generativelanguage.googleapis.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BaseUrl_UnsetOrBlank_FollowsTheProvidersDefault(string? configured)
    {
        // Railway renders an unset variable as an empty string; that must mean "the provider's default",
        // never a relative base address that fails on the first call.
        var options = new LlmProviderOptions
        {
            Provider = LlmProviderKind.Gemini,
            ApiKey = "test-key",
            Model = "gemini-test-model",
            BaseUrl = configured!,
            InputPricePerMillionTokensUsd = 0m,
            OutputPricePerMillionTokensUsd = 0m,
        };

        options.BaseUrl.Should().Be(LlmProviderOptions.GeminiDefaultBaseUrl);
    }

    [Fact]
    public void BaseUrl_ExplicitlyConfigured_WinsOverTheProvidersDefault()
    {
        var options = new LlmProviderOptions
        {
            Provider = LlmProviderKind.Gemini,
            ApiKey = "test-key",
            Model = "gemini-test-model",
            BaseUrl = "https://proxy.example",
            InputPricePerMillionTokensUsd = 0m,
            OutputPricePerMillionTokensUsd = 0m,
        };

        options.BaseUrl.Should().Be("https://proxy.example");
    }

    [Fact]
    public void Validate_ZeroPricesForAFreeTier_ProducesNoErrors()
    {
        // Gemini's free tier is priced at zero; the cost estimate then reads zero, which is true, not an error.
        var options = new LlmProviderOptions
        {
            Provider = LlmProviderKind.Gemini,
            ApiKey = "test-key",
            Model = "gemini-test-model",
            InputPricePerMillionTokensUsd = 0m,
            OutputPricePerMillionTokensUsd = 0m,
        };

        Validate(options).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ProviderOutsideTheEnum_ProducesAnErrorNamingTheOption()
    {
        // The binder accepts a number for an enum, so "Llm__Provider=7" binds to an undefined value rather than
        // failing; validation is what stops it booting into a switch with no arm for it.
        var options = new LlmProviderOptions
        {
            Provider = (LlmProviderKind)7,
            ApiKey = "test-key",
            Model = "claude-sonnet-5",
            InputPricePerMillionTokensUsd = 3.00m,
            OutputPricePerMillionTokensUsd = 15.00m,
        };

        var results = Validate(options);

        results.Should().ContainSingle(r => r.MemberNames.Contains(nameof(LlmProviderOptions.Provider)))
            .Which.ErrorMessage.Should().Contain("Provider").And.Contain("Anthropic").And.Contain("Gemini");
    }

    private static List<ValidationResult> Validate(LlmProviderOptions options)
    {
        var context = new ValidationContext(options);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, context, results, validateAllProperties: true);
        return results;
    }
}
