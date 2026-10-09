using FluentAssertions;
using AgentForge.Api.Agenda;

namespace AgentForge.UnitTests.Api.Agenda;

public sealed class AgendaEndpointsPayloadTests
{
    private static readonly DateTimeOffset LoadedAt = new(2026, 7, 11, 12, 20, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToPayload_RowWithAnOlderSummary_CarriesTheSummarysOwnAsOfBesideThePagesAsOf()
    {
        // The UI labels a row whose summary predates the page from exactly these two fields.
        var result = new AgendaResult(
            [
                new AgendaRow("patient-1", "Synthetic One", LoadedAt.AddHours(1), "summary", [], Failed: false, FailureReason: null, SummaryAsOf: GeneratedAt),
                new AgendaRow("patient-2", null, LoadedAt.AddHours(2), null, [], Failed: true, FailureReason: "unavailable", SummaryAsOf: null),
            ],
            LoadedAt);

        var payload = AgendaEndpoints.ToPayload(result);

        payload.AsOf.Should().Be(LoadedAt);
        payload.Rows.Select(r => r.SummaryAsOf).Should().Equal(GeneratedAt, null);
    }
}
