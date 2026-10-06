using System.ComponentModel.DataAnnotations;
using AgentForge.Api.Health;
using FluentAssertions;

namespace AgentForge.UnitTests.Api.Health;

/// <summary>
/// The retry budget for the store's startup work. A zero delay turns the retry into a hot loop
/// against a store that is already struggling; a cap below the first delay is a contradiction.
/// </summary>
public sealed class DataStoreStartupOptionsTests
{
    private static List<ValidationResult> Validate(DataStoreStartupOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Defaults_AreValid() => Validate(new DataStoreStartupOptions()).Should().BeEmpty();

    [Fact]
    public void Validate_NonPositiveInitialDelay_Fails() =>
        Validate(new DataStoreStartupOptions { InitialRetryDelay = TimeSpan.Zero })
            .Should().ContainSingle(r => r.MemberNames.Contains(nameof(DataStoreStartupOptions.InitialRetryDelay)));

    [Fact]
    public void Validate_CapBelowInitialDelay_Fails() =>
        Validate(new DataStoreStartupOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
        }).Should().ContainSingle(r => r.MemberNames.Contains(nameof(DataStoreStartupOptions.MaxRetryDelay)));

    [Fact]
    public void Validate_MaxRetryDelayOverTaskDelayLimit_Fails() =>
        Validate(new DataStoreStartupOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromDays(50),
        }).Should().ContainSingle(r => r.MemberNames.Contains(nameof(DataStoreStartupOptions.MaxRetryDelay)));

    [Fact]
    public void Validate_InitialRetryDelayOverTaskDelayLimit_Fails() =>
        Validate(new DataStoreStartupOptions
        {
            InitialRetryDelay = TimeSpan.FromDays(50),
            MaxRetryDelay = TimeSpan.FromDays(50),
        }).Should().ContainSingle(r => r.MemberNames.Contains(nameof(DataStoreStartupOptions.InitialRetryDelay)));

    [Fact]
    public void Validate_MaxRetryDelayAtTaskDelayLimit_Succeeds() =>
        Validate(new DataStoreStartupOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = DataStoreStartupOptions.MaxTaskDelay,
        }).Should().BeEmpty();
}
