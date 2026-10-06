using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using AgentForge.Api.Health;
using Microsoft.Extensions.Configuration;

namespace AgentForge.UnitTests.Api.Health;

public sealed class ReadinessOptionsTests
{
    [Fact]
    public void ProbeTimeout_SectionAbsent_DefaultsToABudgetAReadinessCallerCanWaitOut()
    {
        // The defect this default exists to close: every /ready probe used to inherit
        // HttpClient.Timeout's 100s, so an unreachable dependency held the endpoint open long past
        // the point any load balancer or uptime check had already called the service down. The
        // default has to be short enough to be safe in an environment that never configures it,
        // because neither deployed environment does.
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var options = config.GetSection(ReadinessOptions.SectionName).Get<ReadinessOptions>()
            ?? new ReadinessOptions();

        options.ProbeTimeout.Should().BePositive();
        options.ProbeTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Bind_ProbeTimeoutConfigured_IsReadFromTheReadinessSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Readiness:ProbeTimeout"] = "00:00:04",
            })
            .Build();

        var options = config.GetSection(ReadinessOptions.SectionName).Get<ReadinessOptions>();

        options!.ProbeTimeout.Should().Be(TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ProbeTimeoutNotPositive_IsRejectedSoNoEnvironmentCanDisableTheBound(int seconds)
    {
        // ValidateOnStart turns this into a boot failure rather than a /ready that cannot answer:
        // a zero or negative budget would cancel every probe before it began.
        var options = new ReadinessOptions { ProbeTimeout = TimeSpan.FromSeconds(seconds) };

        var results = options.Validate(new ValidationContext(options));

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(ReadinessOptions.ProbeTimeout));
    }

    [Fact]
    public void ResultCacheTtl_SectionAbsent_DefaultsShortEnoughForPostDeployVerifyToConverge()
    {
        // post-deploy-verify.sh --wait 120 polls /ready every 5s. A fresh deploy whose first external
        // probe fails is re-probed one TTL later, so the TTL has to leave that loop room to see the
        // recovery; long enough, too, that a poll loop is no longer one dependency request per call
        var options = new ConfigurationBuilder().AddInMemoryCollection([]).Build()
            .GetSection(ReadinessOptions.SectionName).Get<ReadinessOptions>() ?? new ReadinessOptions();

        options.ResultCacheTtl.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Bind_ResultCacheTtlConfigured_IsReadFromTheReadinessSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Readiness:ResultCacheTtl"] = "00:00:10",
            })
            .Build();

        var options = config.GetSection(ReadinessOptions.SectionName).Get<ReadinessOptions>();

        options!.ResultCacheTtl.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ResultCacheTtlNotPositive_IsRejected(int seconds)
    {
        // A zero TTL would quietly restore one dependency request per /ready call.
        var options = new ReadinessOptions { ResultCacheTtl = TimeSpan.FromSeconds(seconds) };

        var results = options.Validate(new ValidationContext(options));

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(ReadinessOptions.ResultCacheTtl));
    }
}
