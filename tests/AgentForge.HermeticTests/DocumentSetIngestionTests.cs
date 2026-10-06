using System.Text.Json;
using AgentForge.Agents.Ingestion;
using AgentForge.Data.Entities;
using AgentForge.GenerateFixtureDocuments;
using AgentForge.HermeticTests.Support;
using FluentAssertions;

namespace AgentForge.HermeticTests;

/// <summary>
/// Every document in the generated set, ingested end to end: the committed file's bytes through the real
/// ingestion service, extractor, schema gate, quote resolver, mapper and store, with only the model scripted
/// (<see cref="ManifestReplies"/>). A separate change, ARCHITECTURE-DOCUMENTS.md section 4
/// </summary>
public sealed class DocumentSetIngestionTests
{
    public static TheoryData<string> Documents() => new(FixtureDocuments.All.Select(d => d.FileName));

    public static TheoryData<string> OnePerDocumentType() => new(
        FixtureDocuments.All.GroupBy(d => d.Manifest.DocumentType).Select(g => g.First().FileName));

    /// <summary>
    /// <b>Failure mode guarded (invariant):</b> a document type, layout or page the pipeline cannot carry -
    /// a fact dropped between extraction and the store, a citation that loses its page, a fact filed under
    /// another document's reference.
    /// </summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public async Task Ingest_EachDocumentInTheSet_PersistsOneCitedFactPerManifestFact(string fileName)
    {
        var document = Document(fileName);
        await using var harness = await DocumentSetHarness.CreateAsync();

        var result = await harness.IngestAsync(document);

        result.Status.Should().Be(DocumentIngestionStatus.Ingested);
        result.FactCount.Should().Be(document.Manifest.Facts.Count);
        var stored = await harness.StoredAsync(document);
        stored.Should().NotBeNull();
        stored!.DocumentType.Should().Be(DocumentSetHarness.TypeOf(document));
        stored.OpenEmrDocumentReferenceId.Should().Be(DocumentSetHarness.DocumentReferenceIdOf(document));
        foreach (var expected in document.Manifest.Facts)
        {
            var fact = stored.DerivedFacts.Should().ContainSingle(
                f => f.FactType == expected.FactType && f.Citation.QuoteOrValue == expected.Quote,
                $"one {expected.FactType} fact quoting '{expected.Quote}'").Subject;
            // Every fact cites the page its own span is printed on - including the intake chief concern,
            // allergies and family history, which cited the demographics page.
            fact.Citation.PageOrSection.Should().Be(expected.Page.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fact.Citation.SourceType.Should().Be(CitationSourceType.Derived);
            fact.Citation.SourceId.Should().Be(DocumentSetHarness.DocumentReferenceIdOf(document));
            if (expected.FactType == ExpectedFact.LabResult)
            {
                using var payload = JsonDocument.Parse(fact.PayloadJson);
                payload.RootElement.GetProperty("value").GetString().Should().Be(expected.Value);
            }
        }
    }

    /// <summary>
    /// <b>Failure mode guarded (regression, FR-CITE-2):</b> an allergy the model invented stored as if its
    /// quote had been found. Intake free-text items used to borrow the form-level citation, so an invented
    /// allergy inherited the name line's <c>exact</c> - confidence 1.0 and the name line's box, on page 1.
    /// Its own quote is searched for on its own page now. Separate changes review N1
    /// </summary>
    [Fact]
    public async Task Ingest_IntakeFormWithAnInventedAllergy_StoresItUnlocatableWithNoBoxBesideTheRealOne()
    {
        var document = Document("intake-form-multipage.pdf");
        const string invented = "Latex (anaphylaxis)";
        await using var harness = await DocumentSetHarness.CreateAsync((d, reply) =>
        {
            if (d.FileName != document.FileName)
            {
                return reply;
            }

            var json = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            json["allergies"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["text"] = invented,
                ["citation"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["page"] = 2,
                    ["quote"] = invented,
                    ["bounding_box"] = new System.Text.Json.Nodes.JsonArray(0.1, 0.6, 0.3, 0.03),
                    ["match"] = "exact",
                },
            });
            return json.ToJsonString();
        });

        (await harness.IngestAsync(document)).Status.Should().Be(DocumentIngestionStatus.Ingested);

        var allergies = (await harness.StoredAsync(document))!.DerivedFacts
            .Where(f => f.FactType == ExpectedFact.IntakeAllergy).ToList();
        allergies.Should().HaveCount(2);
        var fabricated = allergies.Single(f => f.Citation.QuoteOrValue == invented);
        fabricated.ExtractionConfidence.Should().Be(0.0, "its quote is printed nowhere, whatever the name line says");
        fabricated.Citation.BoundingBox.Should().BeNull("a box would assert the quote is there");
        fabricated.Citation.PageOrSection.Should().Be("2");
        var real = allergies.Single(f => f.Citation.QuoteOrValue != invented);
        real.ExtractionConfidence.Should().Be(1.0);
        real.Citation.PageOrSection.Should().Be("2");
        real.Citation.BoundingBox.Should().NotBeNull();
    }

    /// <summary>
    /// <b>Failure mode guarded (regression, FR-CITE-2):</b> an invented fact paired with a quote that IS
    /// printed on its page. The quote was the only thing checked, so the invented allergy and the invented
    /// medication were stored at 1.0 with the real line's box, and their invented text went into the stored
    /// payload and the per-turn answer context. Separate changes review N1
    /// </summary>
    [Fact]
    public async Task Ingest_IntakeFormWithInventedFactsOnRealQuotes_StoresThemAtZeroWithNoBoxBesideTheRealOnes()
    {
        var document = Document("intake-form-multipage.pdf");
        const string inventedAllergy = "Latex (anaphylaxis)";
        const string inventedMedication = "Apixaban";
        await using var harness = await DocumentSetHarness.CreateAsync((d, reply) =>
        {
            if (d.FileName != document.FileName)
            {
                return reply;
            }

            // Each borrows a real item's citation verbatim - its page, its quote and its box.
            var json = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            var allergies = json["allergies"]!.AsArray();
            allergies.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["text"] = inventedAllergy,
                ["citation"] = allergies[0]!["citation"]!.DeepClone(),
            });
            var medications = json["current_medications"]!.AsArray();
            medications.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = inventedMedication,
                ["dose"] = medications[0]!["dose"]!.DeepClone(),
                ["citation"] = medications[0]!["citation"]!.DeepClone(),
            });
            return json.ToJsonString();
        });

        (await harness.IngestAsync(document)).Status.Should().Be(DocumentIngestionStatus.Ingested);

        var facts = (await harness.StoredAsync(document))!.DerivedFacts;
        var allergyFacts = facts.Where(f => f.FactType == ExpectedFact.IntakeAllergy).ToList();
        allergyFacts.Should().HaveCount(2);
        var fabricatedAllergy = allergyFacts.Single(f => f.PayloadJson.Contains(inventedAllergy, StringComparison.Ordinal));
        fabricatedAllergy.ExtractionConfidence.Should().Be(0.0, "its quote is printed, but it says a different allergy");
        fabricatedAllergy.Citation.BoundingBox.Should().BeNull("a box would point at a line that says something else");
        var realAllergy = allergyFacts.Single(f => f != fabricatedAllergy);
        realAllergy.ExtractionConfidence.Should().Be(1.0);
        realAllergy.Citation.BoundingBox.Should().NotBeNull();

        var medicationFacts = facts.Where(f => f.FactType == ExpectedFact.IntakeMedication).ToList();
        var fabricatedMedication = medicationFacts.Single(f => f.PayloadJson.Contains(inventedMedication, StringComparison.Ordinal));
        fabricatedMedication.ExtractionConfidence.Should().Be(0.0, "the line it quotes names another drug");
        fabricatedMedication.Citation.BoundingBox.Should().BeNull();
        medicationFacts.Where(f => f != fabricatedMedication).Should().OnlyContain(
            f => f.ExtractionConfidence == 1.0 && f.Citation.BoundingBox != null, "every faithful medication is still found");
    }

    /// <summary>
    /// <b>Failure mode guarded (regression, FR-CITE-2):</b> a lab result the report does not print, paired
    /// with a row that it does - the value filed under another analyte, or a value read off the reference
    /// range. The row's quote is printed, so without the lab rule both were stored at 1.0 with the real row's
    /// box. A separate change
    /// </summary>
    [Fact]
    public async Task Ingest_LabReportWithInventedResultsOnRealRows_StoresThemAtZeroWithNoBoxBesideTheRealOnes()
    {
        var document = Document("lab-bmp-two-column.pdf");
        await using var harness = await DocumentSetHarness.CreateAsync((d, reply) =>
        {
            if (d.FileName != document.FileName)
            {
                return reply;
            }

            // Each borrows the Sodium row's citation verbatim - its page, its quote and its box.
            var json = System.Text.Json.Nodes.JsonNode.Parse(reply)!;
            var tests = json["tests"]!.AsArray();
            var sodium = tests.Single(t => (string?)t!["test_name"] == "Sodium")!;
            tests.Add(Invented(sodium, "Magnesium", "136"));
            tests.Add(Invented(sodium, "Sodium", "145"));
            return json.ToJsonString();
        });

        (await harness.IngestAsync(document)).Status.Should().Be(DocumentIngestionStatus.Ingested);

        var labs = (await harness.StoredAsync(document))!.DerivedFacts
            .Where(f => f.FactType == ExpectedFact.LabResult).ToList();
        labs.Should().HaveCount(document.Manifest.Facts.Count + 2);
        var invented = labs.Where(f => f.PayloadJson.Contains("Magnesium", StringComparison.Ordinal)
            || f.PayloadJson.Contains("\"145\"", StringComparison.Ordinal)).ToList();
        invented.Should().HaveCount(2).And.OnlyContain(
            f => f.ExtractionConfidence == 0.0 && f.Citation.BoundingBox == null,
            "each quote is printed, but neither prints the result it is cited for");
        labs.Except(invented).Should().OnlyContain(
            f => f.ExtractionConfidence == 1.0 && f.Citation.BoundingBox != null, "every faithful result is still found");

        static System.Text.Json.Nodes.JsonNode Invented(System.Text.Json.Nodes.JsonNode real, string testName, string value)
        {
            var copy = real.DeepClone();
            copy["test_name"] = testName;
            copy["value"] = value;
            return copy;
        }
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant, FR-DOC-3 / W2-D3):</b> the ingestion cron re-posting a document it
    /// already sent duplicates the stored document and every fact derived from it, and the clinician's brief
    /// then shows each lab value twice.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnePerDocumentType))]
    public async Task Ingest_SameFileTwice_SecondIsAlreadyIngestedAndNothingIsDuplicated(string fileName)
    {
        var document = Document(fileName);
        await using var harness = await DocumentSetHarness.CreateAsync();

        var first = await harness.IngestAsync(document);
        var second = await harness.IngestAsync(document);

        first.Status.Should().Be(DocumentIngestionStatus.Ingested);
        second.Status.Should().Be(DocumentIngestionStatus.AlreadyIngested);
        second.FactCount.Should().Be(first.FactCount);
        harness.Model.DocumentHashes.Should().ContainSingle("the re-ingest must not call the model again");
        (await harness.CountRowsAsync()).Should().Be((1, document.Manifest.Facts.Count),
            "one stored document and one row per fact, however many times the bytes arrive");
    }

    /// <summary>
    /// <b>Failure mode guarded (boundary):</b> a document filed under the wrong <c>doc_type</c> - a lab report
    /// ingested as an intake form - persisting whatever the model made of it instead of being refused at
    /// the schema gate.
    /// </summary>
    [Theory]
    [MemberData(nameof(OnePerDocumentType))]
    public async Task Ingest_UnderTheOtherDocumentType_IsRejectedAndPersistsNothing(string fileName)
    {
        var document = Document(fileName);
        var wrongType = DocumentSetHarness.TypeOf(document) == ClinicalDocumentType.LabPdf
            ? ClinicalDocumentType.IntakeForm
            : ClinicalDocumentType.LabPdf;
        await using var harness = await DocumentSetHarness.CreateAsync();

        var result = await harness.IngestAsync(document, wrongType);

        result.Status.Should().Be(DocumentIngestionStatus.ExtractionRejected);
        (await harness.CountRowsAsync()).Should().Be((0, 0));
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant, CONVENTIONS.md section 7):</b> ingestion logging a
    /// patient identifier or a line of document text. Checked over the whole set, so every layout's path
    /// through the extractor - exact, unlocatable and unchecked quotes alike - has its logs read.
    /// </summary>
    [Fact]
    public async Task Ingest_WholeSet_LogsCarryNoIdentifierOrDocumentText()
    {
        await using var harness = await DocumentSetHarness.CreateAsync();

        foreach (var document in FixtureDocuments.All)
        {
            (await harness.IngestAsync(document)).Status.Should().Be(DocumentIngestionStatus.Ingested);
        }

        harness.Logs.Entries.Should().NotBeEmpty("the capture must have seen the run for an absence to mean anything");
        LogScan.Leaks(harness.Logs.Entries, IngestionLogSentinels()).Should().BeEmpty(
            "no log may carry a patient identifier, a name part or a line of document text");
    }

    public static TheoryData<string> FamilyNames() => new(
        FixtureDocuments.All.Select(d => d.Manifest.Patient.Name.Split(' ')[^1]).Distinct());

    /// <summary>
    /// <b>Failure mode guarded (red control for the log scan):</b> ingestion logging a patient's family
    /// name on its own - an interpolated surname, or a structured value holding nothing else - passing the
    /// scan because only the full name and the given name are looked for.
    /// </summary>
    [Theory]
    [MemberData(nameof(FamilyNames))]
    public void IngestionLogScan_ALoneFamilyNamePlanted_IsALeak(string familyName)
    {
        var planted = new[] { $"extracted 4 facts for {familyName} from page 1", familyName, $"name_{familyName}:" };

        var leaks = LogScan.Leaks(planted, IngestionLogSentinels());

        leaks.Select(l => l.Entry).Should().BeEquivalentTo(planted, $"'{familyName}' alone names the patient");
        leaks.Should().OnlyContain(l => l.Sentinel.Value.Equals(familyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <b>Failure mode guarded (red control for the log scan):</b> a name glued to other text - a
    /// concatenated key, a file name, a plural - passing because name parts were matched as whole words
    /// only.
    /// </summary>
    [Theory]
    [InlineData("patient=DemoHaroldWhitfield", "Whitfield")]
    [InlineData("HaroldWhitfield.pdf", "Whitfield")]
    [InlineData("the Whitfields chart", "Whitfield")]
    public void IngestionLogScan_ANameGluedToOtherText_IsALeak(string planted, string namePart)
    {
        LogScan.Leaks([planted], IngestionLogSentinels()).Should().Contain(
            l => l.Sentinel.Value == namePart, $"'{planted}' carries '{namePart}'");
    }

    /// <summary>
    /// <b>Failure mode guarded (red control for the log scan):</b> a short name part - "Ng" - matched
    /// inside an ordinary word, so a clean diagnostic line fails the scan and the name part gets dropped from
    /// it to make the test pass.
    /// </summary>
    [Theory]
    [InlineData("tracking content hash for the ingested document")]
    [InlineData("Ingesting")]
    [InlineData("Starting extraction; skipping page-level quotes")]
    public void IngestionLogScan_AWordContainingANamePart_IsNotALeak(string clean)
    {
        LogScan.Leaks([clean], IngestionLogSentinels()).Should().BeEmpty($"'{clean}' names nobody");
    }

    // Identifiers, each name part on its own, and every quote the documents print.
    private static IEnumerable<LogSentinel> IngestionLogSentinels() =>
        FixtureDocuments.All.SelectMany(d => new[]
            {
                d.Manifest.Patient.Name, d.Manifest.Patient.Mrn, d.Manifest.Patient.BirthDate,
            }
            .Concat(d.Manifest.Facts.Select(f => f.Quote))
            .Distinct()
            .Select(LogSentinel.Verbatim)
            .Concat(LogSentinel.NamePartsOf(d.Manifest.Patient.Name)))
            .DistinctBy(s => s.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <b>Failure mode guarded (invariant, FR-AUTH-2):</b> the click-to-source fetch cannot tell whose document
    /// an id is. <c>GET /evidence/document/{id}</c> refuses any id whose owner the store does not return as the
    /// session's patient, so the lookup must find what ingestion stored and nothing it did not. A separate change
    /// </summary>
    [Fact]
    public async Task FindPatientIdByDocumentReferenceId_AfterIngestion_ReturnsTheOwningPatientAndNullForAnUnknownId()
    {
        var document = FixtureDocuments.All[0];
        await using var harness = await DocumentSetHarness.CreateAsync();
        await harness.IngestAsync(document);

        (await harness.OwnerOfAsync(DocumentSetHarness.DocumentReferenceIdOf(document)))
            .Should().Be(DocumentSetHarness.PatientId);
        (await harness.OwnerOfAsync("hermetic-docref-never-ingested")).Should().BeNull();
    }

    /// <summary>
    /// <b>Failure mode guarded (invariant, FR-AUTH-2):</b> nothing in the schema stops two patients' documents
    /// being filed under one OpenEMR id, since <c>/documents/ingest</c> trusts its private-network caller
    /// (W2-D17). Such an id must resolve to nobody, so <c>GET /evidence/document/{id}</c> refuses it in both
    /// sessions; a lookup simplified to "first owner found" would open it in whichever session asked.
    /// </summary>
    [Fact]
    public async Task FindPatientIdByDocumentReferenceId_OneIdFiledUnderTwoPatients_ReturnsNull()
    {
        await using var harness = await DocumentSetHarness.CreateAsync();
        await harness.IngestAsync(FixtureDocuments.All[0], documentReferenceId: SharedDocumentReferenceId);
        await harness.IngestAsync(
            FixtureDocuments.All[1], patientId: "hermetic-patient-other", documentReferenceId: SharedDocumentReferenceId);

        (await harness.CountRowsAsync()).Documents.Should().Be(2, "both filings must have been stored for the case to mean anything");
        (await harness.OwnerOfAsync(SharedDocumentReferenceId)).Should().BeNull();
    }

    /// <summary>
    /// <b>Failure mode guarded (boundary):</b> the refusal above must count distinct <i>patients</i>, not rows. Two
    /// documents filed under one id for the same patient still have one owner, and refusing it would break
    /// click-to-source for that patient. A separate change
    /// </summary>
    [Fact]
    public async Task FindPatientIdByDocumentReferenceId_OneIdFiledTwiceUnderOnePatient_ReturnsThatPatient()
    {
        await using var harness = await DocumentSetHarness.CreateAsync();
        await harness.IngestAsync(FixtureDocuments.All[0], documentReferenceId: SharedDocumentReferenceId);
        await harness.IngestAsync(FixtureDocuments.All[1], documentReferenceId: SharedDocumentReferenceId);

        (await harness.CountRowsAsync()).Documents.Should().Be(2);
        (await harness.OwnerOfAsync(SharedDocumentReferenceId)).Should().Be(DocumentSetHarness.PatientId);
    }

    private const string SharedDocumentReferenceId = "hermetic-docref-shared";

    private static FixtureDocument Document(string fileName) => FixtureDocuments.All.Single(d => d.FileName == fileName);
}
