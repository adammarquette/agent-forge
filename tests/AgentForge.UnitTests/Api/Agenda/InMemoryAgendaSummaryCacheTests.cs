using FluentAssertions;
using AgentForge.Api.Agenda;
using Microsoft.Extensions.Options;

namespace AgentForge.UnitTests.Api.Agenda;

public sealed class InMemoryAgendaSummaryCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 7, 11);
    private static readonly AgendaSummaryCacheKey Key = new("default", "dr-jones", "patient-1", "appt-1", Day);

    private readonly SettableTimeProvider _time = new(Start);

    private InMemoryAgendaSummaryCache BuildSut(TimeSpan? ttl = null, int maxEntries = 100) => new(
        Options.Create(new AgendaOptions
        {
            SummaryCacheTtl = ttl ?? TimeSpan.FromMinutes(30),
            MaxCachedSummaries = maxEntries,
        }),
        _time);

    private static AgendaCachedSummary Summary(string text) => new(text, [], Start);

    [Fact]
    public void TryGet_AfterStoreForTheSameKey_ReturnsTheStoredSummary()
    {
        var sut = BuildSut();
        sut.Store(Key, Summary("summary one"));

        sut.TryGet(Key, out var hit).Should().BeTrue();
        hit!.Summary.Should().Be("summary one");
    }

    [Fact]
    public void TryGet_NothingStored_Misses()
    {
        BuildSut().TryGet(Key, out var hit).Should().BeFalse();
        hit.Should().BeNull();
    }

    private static AgendaSummaryCacheKey KeyDifferingIn(string component) => component switch
    {
        "site" => Key with { Site = "other-site" },
        "clinician" => Key with { ClinicianIdentity = "dr-someone-else" },
        "patient" => Key with { PatientId = "patient-2" },
        "appointment" => Key with { AppointmentId = "appt-2" },
        "day" => Key with { ClinicDay = Day.AddDays(1) },
        "patient-case" => Key with { PatientId = "PATIENT-1" },
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    [Theory]
    [InlineData("site")]
    [InlineData("clinician")]
    [InlineData("patient")]
    [InlineData("appointment")]
    [InlineData("day")]
    [InlineData("patient-case")]
    public void TryGet_KeyDiffersInAnyComponent_NeverServesAnotherKeysSummary(string component)
    {
        var other = KeyDifferingIn(component);
        // One patient's summary must never be shown for another, nor one site's or clinician's for another's.
        var sut = BuildSut();
        sut.Store(Key, Summary("patient-1's summary"));

        sut.TryGet(other, out var hit).Should().BeFalse();
        hit.Should().BeNull();
    }

    [Fact]
    public void TryGet_JustBeforeTheTtl_StillHits()
    {
        var sut = BuildSut(TimeSpan.FromMinutes(30));
        sut.Store(Key, Summary("fresh"));

        _time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));

        sut.TryGet(Key, out _).Should().BeTrue();
    }

    [Fact]
    public void TryGet_AtTheTtl_MissesSoTheSummaryIsRegenerated()
    {
        var sut = BuildSut(TimeSpan.FromMinutes(30));
        sut.Store(Key, Summary("stale"));

        _time.Advance(TimeSpan.FromMinutes(30));

        sut.TryGet(Key, out var hit).Should().BeFalse();
        hit.Should().BeNull();
    }

    [Fact]
    public void Store_SameKeyAgain_RestartsItsTtl()
    {
        var sut = BuildSut(TimeSpan.FromMinutes(30));
        sut.Store(Key, Summary("first"));
        _time.Advance(TimeSpan.FromMinutes(20));
        sut.Store(Key, Summary("second"));
        _time.Advance(TimeSpan.FromMinutes(20));

        sut.TryGet(Key, out var hit).Should().BeTrue();
        hit!.Summary.Should().Be("second");
    }

    [Fact]
    public void Store_BeyondTheSizeBound_HoldsAtMostTheBoundAndEvictsTheOldest()
    {
        var sut = BuildSut(maxEntries: 3);
        for (var i = 1; i <= 5; i++)
        {
            sut.Store(Key with { PatientId = $"patient-{i}" }, Summary($"summary {i}"));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        sut.Count.Should().Be(3);
        sut.TryGet(Key with { PatientId = "patient-1" }, out _).Should().BeFalse();
        sut.TryGet(Key with { PatientId = "patient-2" }, out _).Should().BeFalse();
        sut.TryGet(Key with { PatientId = "patient-5" }, out _).Should().BeTrue();
    }

    [Fact]
    public void Store_AtTheSizeBoundWithExpiredEntries_EvictsTheExpiredOnesFirst()
    {
        var sut = BuildSut(TimeSpan.FromMinutes(30), maxEntries: 3);
        sut.Store(Key with { PatientId = "expired-1" }, Summary("a"));
        sut.Store(Key with { PatientId = "expired-2" }, Summary("b"));
        _time.Advance(TimeSpan.FromMinutes(29));
        sut.Store(Key with { PatientId = "live" }, Summary("c"));
        _time.Advance(TimeSpan.FromMinutes(2));

        sut.Store(Key with { PatientId = "new" }, Summary("d"));

        sut.Count.Should().Be(2);
        sut.TryGet(Key with { PatientId = "live" }, out _).Should().BeTrue();
        sut.TryGet(Key with { PatientId = "new" }, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(13 * 60, 10)]
    [InlineData(30, 0)]
    public void AgendaOptions_TtlOrSizeOutOfRange_FailsValidation(int ttlMinutes, int maxEntries)
    {
        var options = new AgendaOptions
        {
            SummaryCacheTtl = TimeSpan.FromMinutes(ttlMinutes),
            MaxCachedSummaries = maxEntries,
        };

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().NotBeEmpty();
    }

    [Fact]
    public void AgendaOptions_Defaults_PassValidation()
    {
        var options = new AgendaOptions();

        options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Should().BeEmpty();
        options.SummaryCacheTtl.Should().Be(TimeSpan.FromMinutes(30));
        options.MaxCachedSummaries.Should().Be(1000);
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
