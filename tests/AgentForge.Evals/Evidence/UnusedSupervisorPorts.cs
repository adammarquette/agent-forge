using AgentForge.Data;
using AgentForge.Data.Entities;
using AgentForge.Documents;

namespace AgentForge.Evals.Evidence;

/// <summary>
/// The two <c>EvidenceAgentSupervisor</c> ports an evidence case does not exercise. No case attaches a
/// document or seeds a pre-ingested fact: those paths belong to the extraction slice and to E2, and folding
/// them in here would make a retrieval case fail for a reason that is not retrieval. Both throw rather than
/// returning empty, so a case that starts depending on one says so loudly instead of scoring a corpus it
/// did not mean to compose against.
/// </summary>
internal sealed class UnusedDocumentExtractor : IDocumentExtractor
{
    /// <inheritdoc />
    public Task<DocumentExtractionResult> ExtractAsync(
        ClinicalDocumentType documentType, ReadOnlyMemory<byte> content, string mediaType,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "An evidence case attached a document. The extraction slice scores that path; pin the retrieval "
            + "halves instead, or extend this harness deliberately.");
}

/// <summary>The derived-fact store, empty for every evidence case — see <see cref="UnusedDocumentExtractor"/>.</summary>
internal sealed class EmptyDerivedFactStore : IDerivedFactStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<DerivedFact>> GetByPatientAsync(
        string patientId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DerivedFact>>([]);

    /// <inheritdoc />
    public Task<IngestedDocument?> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Evidence cases do not ingest documents.");

    /// <inheritdoc />
    public Task<string?> FindPatientIdByDocumentReferenceIdAsync(
        string documentReferenceId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Evidence cases do not fetch source documents.");

    /// <inheritdoc />
    public Task AddAsync(IngestedDocument document, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Evidence cases do not ingest documents.");
}
