using System.Security.Cryptography;
using AgentForge.Llm;

namespace AgentForge.HermeticTests.Support;

/// <summary>
/// The one stubbed seam: stands in for the model behind <see cref="ILlmProvider"/>, which is both the
/// VLM the extractor calls (a request carrying a document or image block) and the LLM the answer composer
/// calls (text only). A document is answered only when its bytes hash to a fixture this script knows, so
/// the canned extraction cannot be returned for bytes the pipeline did not actually carry.
/// </summary>
internal sealed class ScriptedModelProvider(
    IReadOnlyDictionary<string, string> extractionRepliesBySha256,
    Func<string, string> composer) : ILlmProvider
{
    private readonly List<string> _documentHashes = [];
    private readonly List<string> _composerInputs = [];

    /// <summary>sha256 (hex) of every document block the extractor sent, in call order.</summary>
    public IReadOnlyList<string> DocumentHashes => _documentHashes;

    /// <summary>The user text of every composer call, in call order.</summary>
    public IReadOnlyList<string> ComposerInputs => _composerInputs;

    /// <summary>What the composer returned on its last call.</summary>
    public string? LastDraft { get; private set; }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var blocks = request.Messages.SelectMany(m => m.Content).ToList();
        var document = blocks.Select(b => b switch
        {
            LlmDocumentContent d => d.Base64Data,
            LlmImageContent i => i.Base64Data,
            _ => null,
        }).FirstOrDefault(b => b is not null);

        string reply;
        if (document is not null)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(document)));
            _documentHashes.Add(hash);
            reply = extractionRepliesBySha256.TryGetValue(hash, out var canned)
                ? canned
                : "I cannot read this document.";
        }
        else
        {
            var text = string.Join("\n", blocks.OfType<LlmTextContent>().Select(t => t.Text));
            _composerInputs.Add(text);
            reply = composer(text);
            LastDraft = reply;
        }

        return Task.FromResult(new LlmResponse(reply, [], LlmStopReason.EndTurn, new LlmUsage(0, 0, 0m)));
    }
}
