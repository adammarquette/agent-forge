using FluentAssertions;
using AgentForge.Integration.OpenEmr.Fhir;

namespace AgentForge.UnitTests.Integration.OpenEmr.Fhir;

public sealed class ObservationMapperTests
{
    [Fact]
    public void MapBundle_ValidLabObservation_ReturnsRecordWithValueAndReferenceRange()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "300",
                "status": "final",
                "category": [ { "coding": [ { "code": "laboratory" } ] } ],
                "code": { "text": "INR" },
                "valueQuantity": { "value": 2.3, "unit": "ratio" },
                "referenceRange": [ { "low": { "value": 2.0 }, "high": { "value": 3.0 } } ],
                "effectiveDateTime": "2026-06-01"
              }
            }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        records.Should().ContainSingle();
        var record = records[0];
        record.Source.Citation.Should().Be("Observation/300");
        record.Category.Should().Be("laboratory");
        record.CodeDisplay.Should().Be("INR");
        record.Value.Should().Be(2.3);
        record.Unit.Should().Be("ratio");
        record.ReferenceRangeLow.Should().Be(2.0);
        record.ReferenceRangeHigh.Should().Be(3.0);
        record.EffectiveDateTime.Should().Be(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        record.Status.Should().Be("final");
    }

    [Fact]
    public void MapBundle_EffectiveDateTimeCarryingAnOffset_KeepsTheOffsetRatherThanNormalisingToUtc()
    {
        // Unlike this namespace's other mappers, an Observation's date must survive as the day it
        // was drawn: the verification layer renders it to the clinician, and a 23:30 draw
        // normalised to UTC is stamped with the next day. FHIR R4 requires an
        // offset on any dateTime carrying a time, so this is the ordinary shape, not an edge case.
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "301",
                "status": "final",
                "category": [ { "coding": [ { "code": "laboratory" } ] } ],
                "code": { "text": "INR" },
                "valueQuantity": { "value": 4.6 },
                "effectiveDateTime": "2026-09-11T23:30:00-05:00"
              }
            }
          ]
        }
        """;

        var record = ObservationMapper.MapBundle(json)[0];

        // Asserted on the offset and the local date, not with Should().Be(DateTimeOffset) - that
        // compares instants, so it is green whether or not the offset survived.
        record.EffectiveDateTime.Should().NotBeNull();
        record.EffectiveDateTime!.Value.Offset.Should().Be(TimeSpan.FromHours(-5));
        record.EffectiveDateTime!.Value.Date.Should().Be(new DateTime(2026, 9, 11));
        record.EffectiveDateTime!.Value.UtcDateTime.Should().Be(
            new DateTime(2026, 9, 12, 4, 30, 0, DateTimeKind.Utc), "the instant itself is unchanged");
    }

    [Fact]
    public void MapBundle_VitalSignObservation_ReturnsVitalSignsCategory()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "301",
                "status": "final",
                "category": [ { "coding": [ { "code": "vital-signs" } ] } ],
                "code": { "text": "Heart rate" },
                "valueQuantity": { "value": 72, "unit": "beats/minute" }
              }
            }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        records.Should().ContainSingle().Which.Category.Should().Be("vital-signs");
    }

    [Fact]
    public void MapBundle_MissingValueQuantity_ReturnsNullValueAndUnit()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "302",
                "status": "final",
                "code": { "text": "Some qualitative result" }
              }
            }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        var record = records.Should().ContainSingle().Subject;
        record.Value.Should().BeNull();
        record.Unit.Should().BeNull();
    }

    [Fact]
    public void MapBundle_MissingReferenceRange_ReturnsNullLowAndHigh()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "303",
                "status": "final",
                "code": { "text": "BNP" },
                "valueQuantity": { "value": 150, "unit": "pg/mL" }
              }
            }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        var record = records.Should().ContainSingle().Subject;
        record.ReferenceRangeLow.Should().BeNull();
        record.ReferenceRangeHigh.Should().BeNull();
    }

    [Fact]
    public void MapBundle_MissingCategory_ReturnsUnknownCategory()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            { "resource": { "resourceType": "Observation", "id": "304", "status": "final", "code": { "text": "X" } } }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        records.Should().ContainSingle().Which.Category.Should().Be("unknown");
    }

    [Fact]
    public void MapBundle_CodeTextMissing_FallsBackToCodingDisplay()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            {
              "resource": {
                "resourceType": "Observation",
                "id": "305",
                "status": "final",
                "code": { "coding": [ { "display": "Potassium" } ] }
              }
            }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        records.Should().ContainSingle().Which.CodeDisplay.Should().Be("Potassium");
    }

    [Fact]
    public void MapBundle_EntryMissingId_SkipsEntryRatherThanFabricatingACitation()
    {
        const string json = """
        {
          "resourceType": "Bundle",
          "entry": [
            { "resource": { "resourceType": "Observation", "status": "final", "code": { "text": "No id" } } },
            { "resource": { "resourceType": "Observation", "id": "306", "status": "final", "code": { "text": "Has id" } } }
          ]
        }
        """;

        var records = ObservationMapper.MapBundle(json);

        records.Should().ContainSingle().Which.Source.Id.Should().Be("306");
    }

    [Fact]
    public void MapBundle_MalformedJson_ThrowsFhirParsingException()
    {
        var act = () => ObservationMapper.MapBundle("{ not valid");

        act.Should().Throw<FhirParsingException>();
    }
}
