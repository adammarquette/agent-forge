# LLM Prompt Inventory

This document lists everything the copilot sends to the language model: the request envelope, the three
places that call the model, the prompts each one uses, the tool definitions the model reads, and the settings
that travel with them. It also says, rule by rule, which prompt instructions are backed by deterministic code
and which are only asked of the model.

**The code is the source of truth for prompt text.** This document names the constant and file that hold each
prompt and summarises what it instructs; read the constant itself before changing or quoting a prompt.

**Scope.** `ILlmProvider` ([src/AgentForge.Llm](src/AgentForge.Llm/)) is the generative boundary covered
here. Retrieval also sends text off the host: the Cohere client under
[src/AgentForge.Retrieval/Cohere](src/AgentForge.Retrieval/Cohere/) calls `/v2/embed` and `/v2/rerank` with a
query and candidate passages. Those calls carry no instructions, so they are not prompts, but they are a second
egress of patient-adjacent text and belong in any review of what leaves the system.

## 1. The request envelope

`Llm__Provider` picks the provider, Anthropic (the default) or Gemini, and that provider's mapper turns the same
`LlmRequest` into its own wire request. The prompts and tool catalog below are identical on both.

**Anthropic.** `AnthropicRequestMapper.Map` (in [src/AgentForge.Llm/Anthropic](src/AgentForge.Llm/Anthropic/))
turns an `LlmRequest` into the wire request. These are the only fields sent:

| Wire field | Source | Notes |
|---|---|---|
| `model` | `Llm__Model` | one model for every call site; no per-flow override |
| `max_tokens` | `LlmRequest.MaxOutputTokens` | default 4096; document extraction uses 8192 |
| `system` | `LlmRequest.SystemPrompt` | a single string, no cache markers |
| `messages` | `LlmRequest.Messages` | the full conversation, oldest first, resent every call |
| `tools` | `LlmRequest.Tools` | omitted when null |

**Never sent:** `temperature`, `top_p`, `top_k`, `stop_sequences`, `metadata`, `tool_choice`, streaming, and
prompt-cache `cache_control` markers. Leaving out `temperature` is deliberate: the current model has deprecated
it and the API answers any explicit value with a 400 `invalid_request_error`, which would push every call
site into its deterministic fallback. `AnthropicMessageRequest` has no such field for that reason. The system
prompt's "low-temperature, extractive framing" phrase is therefore an instruction to the model, not a setting.

**Gemini.** `GeminiRequestMapper.Map` (in [src/AgentForge.Llm/Gemini](src/AgentForge.Llm/Gemini/)) builds
the body of `POST {BaseUrl}/v1beta/models/{Llm__Model}:generateContent`, with the key in the `x-goog-api-key`
header, never the URL. These are the only fields sent:

| Wire field | Source | Notes |
|---|---|---|
| `contents` | `LlmRequest.Messages` | the full conversation, oldest first, roles `user` and `model` |
| `systemInstruction` | `LlmRequest.SystemPrompt` | one text part; omitted when empty |
| `tools` | `LlmRequest.Tools` | one tool of `functionDeclarations`, schemas as `parametersJsonSchema`; omitted when null or empty |
| `generationConfig` | `LlmRequest.MaxOutputTokens` | `maxOutputTokens` only |

Images and PDFs travel as `inlineData` parts. A tool result is a `functionResponse` named after the call it
answers, its result wrapped in an `output` (or `error`) object. A `thoughtSignature` Gemini returns with a
function call is sent back with that call, because Gemini 3 models reject the replayed call without it.
`temperature`, safety settings and every other generation setting are never sent. A `STOP` finish on a turn that
calls a function is a tool-use turn; `SAFETY`, `RECITATION` and the other blocks are never a clean answer.
Thinking tokens count as output tokens in the cost estimate.

## 2. The three call sites

| Call site | System prompt | Tools | `max_tokens` | Sends document bytes |
|---|---|---|---|---|
| `AgentOrchestrator` (src/AgentForge.Agent) — pre-visit brief, follow-up chat, agenda rows | `CardiologyProfile.SystemPrompt` | all eight (`McpToolCatalog.AllTools`) | 4096 | no |
| `EvidenceAgentSupervisor` (src/AgentForge.Agents) — evidence composer behind `POST /evidence/ask` | `EvidenceComposerPrompt.System` | none | 4096 | no |
| `DocumentExtractor` (src/AgentForge.Documents) — document ingestion | `ExtractionPrompts.SystemFor(documentType)` | none | 8192 | yes, base64 PDF or image |

All three are metered for token usage and cost. The orchestrator loops for up to `MaxToolCallRounds = 5`
provider calls per turn, each resending the whole conversation plus every tool result so far.

## 3. The prompts

### 3.1 Chat and brief system prompt

**Where:** `CardiologyProfile.SystemPrompt` in
[src/AgentForge.Agent/CardiologyProfile.cs](src/AgentForge.Agent/CardiologyProfile.cs), about 4,800
characters. Sent on every orchestrator call: the pre-visit brief (UC-1), each follow-up turn (UC-2) and each
agenda row (UC-6), and again on each tool round within a turn. Use cases are defined in
[REQUIREMENTS.md](REQUIREMENTS.md).

**What it instructs**, rule by rule, is the left column of the table below. Two framing points: the copilot
is scoped to the one patient selected when the session opened, and it surfaces what is already in the record
rather than deciding. Non-goal NG1 states the latter: the copilot does not diagnose, recommend a treatment,
dose or medication change, or place orders. Citations are copied from each tool result's `Source` string as
`[ResourceType/Id]`, `[Document/<id>]` or `[Guideline/<id>]`.

#### 3.1.1 Which rules are load-bearing

Read this before shortening the prompt to save tokens:

| Rule | Enforced in code by | Safe to cut? |
|---|---|---|
| single-patient scope | `McpToolDispatcher` binds the patient server-side | No — cutting it loses the refusal wording, not the control |
| no diagnosis, no recommendation | nothing | No — the prompt is the only control |
| no orders | `McpToolCatalog.Offers` and the read-only layers below it (§7) | The sentence could go without losing the control, but the refusal wording would go with it |
| cite every fact, copy ids exactly | `ClinicalResponseVerifier` | No — the verifier catches unresolved brackets, and uncited lines only when they contain one of its clinical keywords; a claim phrased without a keyword ships uncited |
| call `get_document_facts`; cite `[Document/…]`, `[Guideline/…]` | the verifier resolves the tokens | No — without it the brief omits document-only findings |
| no prose provenance | nothing | No |
| the words "low-temperature" | nothing possible (§1) | The label only |
| do not reason beyond the record | nothing; the verifier checks that a token resolves, not that the claim follows from it | No — the only sentence forbidding outside-knowledge inference |
| state gaps plainly (UC-5) | nothing, but coupled to the verifier: `SourceAttributionEngine.GapIndicatorPhrases` exempts gap wording from the uncited-claim rule | No — rewording a gap line can get it suppressed as a claim |
| present both when sources disagree (UC-3) | nothing | No |
| lead with what changes today's plan | nothing | No — this is the product |
| resolve pronouns from history (UC-2) | conversation state | No |
| one parallel batch of all seven read tools | nothing | Partly — the batching is latency tuning, but the paragraph also explains `since_date` and keeps every read tool carrying live traffic so a tool-level failure stays visible |
| report overlapping results once | prose only; `ToolResultJsonScanner` de-duplicates by citation so safety flags cannot double-fire | No |

### 3.2 Synthesised user turns

Three `private const string` in
[src/AgentForge.Agent/AgentOrchestrator.cs](src/AgentForge.Agent/AgentOrchestrator.cs), sent as user turns
the clinician never types:

| Constant | When |
|---|---|
| `BriefRequestPrompt` | opens a pre-visit brief: what changed since the last visit and what matters today |
| `AgendaSummaryPrompt` | one agenda row: a 1–3 sentence summary meant to be scanned beside other patients |
| `MalformedOutputRepairPrompt` | the model replied empty or stopped early: asks for a complete plain-text answer with exact citations |

A real clinician question is appended verbatim as a user turn with no wrapper text. That is why defence
against prompt injection lives in code (§7), not in the prompt.

### 3.3 Document extraction

**Where:** [src/AgentForge.Documents/ExtractionPrompts.cs](src/AgentForge.Documents/ExtractionPrompts.cs).
Sent once per ingested document (`POST /documents/ingest`), never per chat turn. `SystemFor(documentType)`
picks `LabSystem` for a lab PDF or `IntakeSystem` for an intake form; each is a JSON-shape declaration
followed by the shared `GroundingRules`. The user turn is `UserInstruction`, a one-line request to return only
the JSON object, sent beside the document content block.

**`GroundingRules` instructs:** return only the JSON object; every fact carries a `citation` with a quote
copied verbatim from the document and its 1-based page; each citation carries a normalised
`[x, y, width, height]` bounding box (null only if the model genuinely cannot locate it — the prompt notes that
locating printed text is not inventing data); use null for absent fields and never invent, infer or normalise
a value that is not printed.

**The shapes.** `LabSystem` declares a `tests[]` array (test name, value, unit, reference range, collection
date, abnormal flag, citation) and asks for each test's quote to be its own result row, from the test name
through the value and, when printed, the unit. `IntakeSystem` declares demographics, chief concern, current
medications, allergies and family history; every item carries its own citation, and the top-level citation
covers the demographics only. Both shapes mirror the strict extraction contracts exactly: change one and the
other must move with it.

**One contract field is deliberately absent from the prompt.** `ExtractionCitation.Match` is stamped by the
extractor from what it found in the document, never read from the model's reply, so the model cannot assert
its own grounding; `DocumentExtractor` overwrites that slot on every citation. The model composing an evidence
answer does later see the stamped value, because the canonical extraction JSON is passed into the composer's
user turn, but no prompt explains what it means.

### 3.4 Evidence composer

**Where:** `EvidenceComposerPrompt.System` in
[src/AgentForge.Agents/EvidenceComposerPrompt.cs](src/AgentForge.Agents/EvidenceComposerPrompt.cs), about
1,100 characters. Sent only on `POST /evidence/ask`, which bypasses `AgentOrchestrator`.

**What it instructs:** answer the physician's question using only the supplied lab values and guideline
snippets; cite each clinical statement immediately with the bracketed token it came from — `[Lab/<id>]` for a
patient value, `[Guideline/<id>]` for a general recommendation — copied exactly and never invented, because an
uncited clinical statement is dropped; keep patient values distinct from guideline evidence; say so plainly
when the inputs do not support an answer; be concise.

Its user turn is not a constant. `EvidenceAgentSupervisor` assembles it from the question, the extracted lab
values, derived document facts, the full facts JSON and the retrieved snippets, each pre-labelled with its
token. That turn uses a third token family, `[Derived/<slug>]`, which the verifier also resolves but the system
prompt never names: the model learns it only from the shape of its input.

## 4. The tool catalog is prompt surface

[src/AgentForge.Agent/McpToolCatalog.cs](src/AgentForge.Agent/McpToolCatalog.cs) defines eight tools, and
each goes to the model with its name, description and JSON schema. The model reads descriptions as
instructions, so **editing a description is a prompt change.**

The catalog is also the allowlist. `McpToolDispatcher` asks `McpToolCatalog.Offers` before routing, so a tool
is callable only if it is advertised; a tool wired into the dispatcher but left off the catalog is inert.

For the six FHIR tools the schema is generated from the request record the tool validates against
(`McpToolInputSchema`), so each parameter's `[Description]` attribute on the records in
[src/AgentForge.Mcp](src/AgentForge.Mcp/) (for example `GetLabsRequest.cs`) reaches the model verbatim and
is prompt surface too. The `since_date` format is advertised as a JSON Schema `pattern`. The tool-level
descriptions are hand-written in the catalog.

| Tool | Parameters | What the description tells the model |
|---|---|---|
| `get_patient_summary` | none | demographics, problems, medications, allergies as one bundle; call first on a brief |
| `get_interval_changes` | `since_date` (required) | medication changes, new labs and encounters since a date |
| `get_labs` | `since_date` (optional) | lab observations with values, units, dates and reference ranges |
| `get_vitals` | `since_date` (optional) | blood pressure and heart rate observations |
| `get_recent_encounters` | `count` (1–20, default 3) | a thin encounter list; ask for detail explicitly rather than assume it |
| `get_documents` | `document_type` | echo and device-interrogation narratives; any value read from narrative text must be labelled derived |
| `get_document_facts` | none | facts already extracted from ingested documents, citable as `[Document/<id>]`; prefer citing these for document-sourced values |
| `retrieve_evidence` | `query` (required) | search the guideline corpus for snippets citable as `[Guideline/<id>]` |

**No tool takes a patient id.** The patient is bound server-side from the session; the dispatcher ignores any
patient id, site or scope in tool arguments and logs the attempt. That, not the prompt, is the control.

## 5. What else travels each turn

- **The whole conversation**, every call, with no truncation or summarisation window.
- **Every tool result so far**, as `tool_result` blocks containing the dispatcher's JSON and the `Source`
  citation strings the prompt tells the model to copy.
- **Document bytes**, base64, on the extraction path only: a document block for PDFs, an image block otherwise.
- **Verified assistant answers.** History holds the post-verification text, so a suppressed claim does not
  persist into later turns.
- **Refusal text written by the server** into the `tool_result` slot, which steers the next turn the way a
  prompt does. `McpToolCatalog.OutOfScopeRefusalMessage` answers a call to a tool the catalog does not offer
  by stating that the copilot is read-only and cannot order, prescribe or change anything — deliberately not
  "unknown tool", so neither an invented name nor one suggested by record content reads as "try another
  spelling". `PatientAccessRefusal.UserFacingMessage` in src/AgentForge.Mcp is the authorization refusal.

## 6. Settings

`LlmProviderOptions` in [src/AgentForge.Llm/LlmProviderOptions.cs](src/AgentForge.Llm/LlmProviderOptions.cs),
bound from `Llm__*` environment variables:

| Setting | Default | Effect |
|---|---|---|
| `Llm__Provider` | `Anthropic` | `Anthropic` or `Gemini`; anything else stops startup |
| `Llm__ApiKey` | — | credential for that provider |
| `Llm__Model` | — | the model for all three call sites |
| `Llm__BaseUrl` | the provider's API | `https://api.anthropic.com`, or `https://generativelanguage.googleapis.com` for Gemini |
| `Llm__InputPricePerMillionTokensUsd`, `Llm__OutputPricePerMillionTokensUsd` | — | cost estimate only; not sent |
| `Llm__AttemptTimeoutSeconds` | 60 | timeout for one HTTP attempt |
| `Llm__TotalRequestTimeoutSeconds` | 150 | timeout across all retries; must exceed the attempt timeout or startup fails |

Per call: `MaxOutputTokens` on `LlmRequest`, and `MaxToolCallRounds = 5` in the orchestrator. See
[DEPLOYMENT.md](DEPLOYMENT.md) for where these are set.

## 7. Prompt content vs deterministic enforcement

Several prompts ask for behaviour that code also enforces. A rule with nothing in the right column is only
asked for; tightening its wording does not tighten a control.

| Rule the prompt states | Enforced by |
|---|---|
| cite every clinical fact as `[ResourceType/Id]`; never invent an id | `ClinicalResponseVerifier` checks each citation against real tool results and suppresses unmatched claims |
| one patient per session | `McpToolDispatcher`: patient bound from the session, injected ids ignored and logged (FR-CHAT-3, NFR-SEC-2) |
| return only the JSON object (extraction) | partly: `DocumentExtractor.ExtractJsonObject` slices from the first `{` to the last `}`, so fences and commentary are tolerated; strict deserialisation rejects a wrong shape |
| every fact must include a citation (extraction) | the contracts: citation, page and quote are `required` on every lab row and intake item, and an explicit JSON `null` in a non-nullable member is rejected |
| the quote is copied verbatim (extraction) | `CitationBoundingBoxResolver` stamps each citation `exact` (found in the page's glyphs), `unlocatable` (searched and not found, or the cited page does not exist) or `unchecked` (no text layer to search). On `unlocatable` the model's bounding box is discarded. `FactQuoteSupport` additionally requires the quote to contain the fact's own text (whole-word, no synonyms, numbers keep their magnitude punctuation); a lab quote must print analyte, value and unit as one row, with only a small reviewed abbreviation table (`LDL`, `HDL`, `K+`) forgiven |
| never invent, infer or normalise (extraction) | partly: an invented or expanded value not present in its own quote comes out `unlocatable`; a value invented together with a matching fabricated quote is caught only by the verbatim check |
| do not place orders | the tool catalog: `Offers` gates routing, every offered tool is a read, `IOpenEmrFhirApi` declares only `GET`, and every SMART resource scope requested is `.read` |
| do not diagnose | nothing, and nothing lexical is possible: surfacing a recorded diagnosis and making one are the same sentence |
| do not recommend a treatment, dose or medication change | nothing; a lexical filter was considered and rejected (below) |
| low-temperature, extractive framing | nothing possible (§1); grounding rests on the citation verifier |
| present both when sources disagree | nothing |
| one parallel batch on the brief | nothing; a latency instruction, not a control |
| report an overlapping result once | partly: `ToolResultJsonScanner` de-duplicates by citation before `CardiologyConstraintEngine` computes safety flags; duplicate narration in prose is not detected |
| no prose provenance for document facts | nothing |

**Limits of the verbatim check.**

- **Marked, not suppressed.** An `unlocatable` fact still reaches the clinician, with page-level
  click-to-source and `DerivedFact.ExtractionConfidence` of 0.0 (`exact` 1.0, `unchecked` 0.5). The matcher is
  exact-only, so suppressing would drop real results on tokenisation quirks. Nothing yet filters on that
  confidence, and no prompt explains it to the model.
- **`unchecked` is a blind spot.** A page with no text layer (a scan or image) cannot be searched, so a
  fabricated quote there is caught only if the fact's text is missing from its own quote.
- **A future fuzzy matcher** must report its own outcome with a stated similarity floor rather than lowering
  `Exact`, so fuzzy evidence is never mistaken for verbatim evidence.
- **Metrics:** `agentforge.citation_quote_matches{outcome}`, `agentforge.extraction_field_outcomes{field,outcome}`
  and `agentforge.extraction_confidence` ([METRICS.md](METRICS.md)). On a digital corpus a rising `unchecked`
  count means the documents changed, not the model.

**Limits of the NG1 scope gate.**

- **It stops the copilot acting, not advising.** A correctly cited recommendation ("given the INR of 3.8
  [Observation/123], hold the warfarin dose") passes the verifier and ships. The golden case
  `answer-m1-scope-escape-cited-recommendation-ships` pins exactly that, and the eval gate reports it as a
  known scope escape beside metric M1 (groundedness: ungrounded clinical claims that reach the answer).
- **A lexical "do not recommend" filter was rejected** because the same verb-plus-dose shapes are how the
  record is surfaced ("hold parameters: HR < 55", a note's recorded recommendation, UC-3 medication
  reconciliation). A filter that suppresses legitimate record-surfacing would end up switched off.
- **`agentforge.out_of_scope_tool_calls` should always read zero.** Any non-zero value means the model emitted
  a tool name it was not offered, or record content steered it toward one; the attempted name goes to the log,
  not to a metric label.

## 8. What pins each prompt

| Prompt or surface | Pinned by (under [tests/](tests/)) |
|---|---|
| `CardiologyProfile.SystemPrompt` | `AgentForge.UnitTests/Agent/CardiologyProfileTests.cs` — required safety phrases are present (including all three NG1 verbs), document facts are requested, prose provenance is forbidden, and all seven read tools are named in the batch instruction |
| `ExtractionPrompts` | `AgentForge.UnitTests/Documents/DocumentExtractorTests.cs`, e.g. `ExtractAsync_LabReport_AsksForEachQuoteToPrintItsRowFromTheNameThroughTheUnit` and `ExtractAsync_WhenTheModelAssertsItsOwnMatchQuality_TheAssertionIsOverwritten`; the verbatim backstop by `CitationBoundingBoxResolverTests.cs` and `FactQuoteSupportTests.cs`; measured quote location over the generated document set by `AgentForge.HermeticTests/CitationBaselineTests.cs` |
| `EvidenceComposerPrompt.System` | no phrase test; the code around it is covered by `AgentForge.UnitTests/Agents/EvidenceAgentSupervisorTests.cs` and the ten `evidence-*` golden cases |
| Tool descriptions and schemas | `AgentForge.UnitTests/Agent/McpToolCatalogTests.cs`, `McpToolDispatcherAuthorizationTests.cs`, and `McpToolSchemaContractParityTests.cs` (each advertised schema matches its request record field by field) |
| No-orders guardrail and `OutOfScopeRefusalMessage` | `AgentForge.UnitTests/Agent/ClinicalScopeGuardrailTests.cs` (catalog, `GET`-only FHIR client, read-only SMART resource scopes) and `McpToolDispatcherGateCoverageTests.cs` |

**No test scores a prompt's behaviour.** The golden set in [evals/golden](evals/golden/) (98 cases across
`extraction`, `authorization`, `answer` and `evidence`) supplies the model's reply from the case file
(`stub_model_response`, or a `ScriptedLlmProvider` that never reads the request), so prompt text cannot change
any eval outcome. The phrase tests catch a deletion; nothing catches a wording change that makes a prompt work
less well. Re-run the evals after a prompt change to prove the surrounding code still works, and check the
model's real output for the prompt itself.

## 9. Known gaps

1. **"Low-temperature" is prose and cannot be anything else.** Do not add a `temperature` field; every call
   would fail with a 400 (§1). Whether the phrase should stay at all, given it costs input tokens, is open.
2. **No prompt caching.** The system prompt and all eight tool definitions are resent on every call, including
   each tool round. `cache_control` markers would cut input cost materially.
3. **One model for every call site.** There is no per-flow override, so a cheaper model cannot be chosen for
   extraction.
4. **The composer's user turn is unbounded.** `EvidenceAgentSupervisor` interpolates the full facts JSON and all
   retrieved snippets with no length cap.
5. **No prompt is behaviourally gated** (§8). The eval gate is real but scores the code downstream of the
   model.
6. **Two safety rules are prompt-only:** no diagnosis and no recommendation. No orders is enforced in code
   (§7).

The security platform's own agent prompts are versioned separately under
[security-platform/prompts/](security-platform/prompts/) and described in
[SECURITY-PLATFORM.md](SECURITY-PLATFORM.md).
