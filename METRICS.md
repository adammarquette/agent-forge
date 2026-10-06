# AgentForge Metrics

This document lists what AgentForge measures: each target, the instrument that measures it, and what the
last reading was. It covers the core copilot, the document and evidence pipeline, and the security platform,
and it explains how to re-run the load test that produced the latency baselines. Requirement IDs (FR-, NFR-,
M-) are defined in [REQUIREMENTS.md](REQUIREMENTS.md). This document says how each one is measured.

---

## 0. How to read this document

Each metric is reported in a table with these columns:

| Column | Meaning |
|---|---|
| **Metric** | The thing being measured. |
| **Requirement** | The ID that sets the target. |
| **Target** | The number or invariant that must hold. `—` means the requirement states no number. |
| **Where measured** | The instrument. If no instrument exists, the cell says **not measured** and names what would measure it. |
| **Last reading** | The last real measurement and the kind of run it came from. An estimate is never shown as a measurement. |

**"Not measured" is a valid value, and several rows use it.** A plausible number that nobody measured is
worse than a blank, because people end up quoting it.

**Three measurement surfaces. They answer different questions and cannot be swapped for each other.**

1. **Live series.** The sidecar's OpenTelemetry meter `AgentForge`
   ([AgentForgeMetrics.cs](src/AgentForge.Observability/AgentForgeMetrics.cs),
   [EvalResultsMetrics.cs](src/AgentForge.Observability/EvalResultsMetrics.cs)) is scraped by Prometheus
   and drawn in Grafana. These figures are **server-side** and exist only while a Prometheus is scraping.
2. **Point-in-time load runs.** [tools/LoadTestChat](tools/LoadTestChat/) runs against a deployed stack
   (§7). These figures are **client-side**: a stopwatch around the full round trip.
3. **The eval gate.** The golden cases in [evals/golden](evals/golden/) are scored by boolean rubrics.
   Each case passes or fails. The result is not a time series.

**Client-side and server-side numbers are not comparable.** A client-side figure includes transport, hub
dispatch and queueing. A server-side stopwatch inside the service does not, so it reads lower. This document
always says which kind each number is.

---

## 1. Latency, load and throughput (`NFR-PERF-*`, `M4`)

- **NFR-PERF-1.** A single-patient pre-visit brief (`RequestBrief`) turn has a p95 of **26 s or less**,
  end to end, with up to 50 concurrent users.
- **NFR-PERF-2.** CPU, memory and throughput are recorded as a baseline, so that a change can be measured
  against it.
- **NFR-PERF-3.** Load behaviour (p50/p95/p99 and error rate) is measured at 10 and at 50 concurrent users.
- **NFR-PERF-4.** A results table for 10 and 50 users exists, together with the baseline profiles.
- **NFR-PERF-W2-1.** CPU, memory, latency and throughput are recorded for the evidence flow
  (`POST /evidence/ask`).
- **M4.** The product latency metric: the p95 stays within the NFR-PERF-1 target at 10 or more concurrent
  users. It is measured with the same instruments as NFR-PERF-1.

| Metric | Requirement | Target | Where measured | Last reading |
|---|---|---|---|---|
| Brief turn latency p95 | NFR-PERF-1, M4 | ≤ 26 s at up to 50 users | Client-side: LoadTestChat (§7). Live, server-side: panel *Brief Turn Latency (p50 / p95)* and alert `AgentForgeHighTurnLatencyP95` | **20.8 s at 10 users, 25.8 s at 50 users**, client-side (§7.1). A later single-user timing on the compose stack read p50 32.4 s / p95 35.5 s. That run had only 3 calls at concurrency 1, so it does not measure the budgeted population. **Treat M4 as unsettled until a new load run is taken.** |
| Concurrency the budget is stated at | NFR-PERF-1 | 50 concurrent users | **Not measured.** The service has no gauge for connections or in-flight turns, so no latency reading can be tied to a load level. | — |
| CPU / memory | NFR-PERF-2 | — | Container resource metrics during a load run | 0.008 vCPU average (0.16 max), 137 MB average (195 MB max) (§7.1) |
| Load behaviour | NFR-PERF-3, NFR-PERF-4 | p50/p95/p99 and error rate at 10 and at 50 users | LoadTestChat | Results table in §7.1. **0.00 % errors at both levels.** Read the caveat in §7.3 before relying on that number. |
| Throughput | NFR-PERF-2 | — | Change in `agentforge_agent_turns_total` over the run | About 2.2 turns/s (brief) and 5.9 turns/s (evidence), both at 50 users |
| Evidence-flow baseline | NFR-PERF-W2-1 | — | LoadTestChat with `LoadTest__Question` set | p95 14.9 s at 10 users and 12.9 s at 50 users, 0 % errors (§7.2). CPU and memory were not measured again for this flow. |

### 1.1 What the p95 alert measures

`AgentForgeHighTurnLatencyP95` fires when the server-side p95 is above **26 s** for **15 minutes**. It
evaluates only `{turn_type="brief"}`. The duration histogram is tagged `turn_type` = `brief`, `agenda` or
`follow_up`, so the short Daily Agenda summaries and in-session follow-ups cannot dilute the series. The
histogram has explicit bucket boundaries that step every 2 s from 20 s to 30 s, with **26 on a boundary**,
so the quantile can resolve the threshold. The 15-minute `for:` is longer than the 5-minute rate window, so
a single slow brief cannot trigger the alert.

Two things the alert does not show:

- **Measurement point.** The series is timed inside `RunTurnAsync`, on the server. The budget is end to end,
  so the live number reads lower than the client-side baseline.
- **Concurrency.** No reading is tied to load, so the "up to 50 users" condition is not modelled.

When the alert fires, it means briefs specifically are slow. When it is silent, that does not prove the
budget is met. **If the number has to move, reduce the latency or revise the target. Never raise the alert
threshold above the target.**

---

## 2. Product success metrics (M1–M5): the eval half

These metrics come from the golden set, not from a time series. The set has **98 cases** in
[evals/golden](evals/golden/):

- 50 document-extraction cases (lab PDFs and intake forms)
- 13 authorization cases (`authz-*`)
- 25 answer-path cases (`answer-*`)
- 10 evidence-retrieval cases (`evidence-*`)

Thirteen boolean rubrics score them, including `no_phi_in_logs`, `schema_valid`, `citation_present`,
`grounded_answer`, `constraint_flagged`, `no_unauthorized_disclosure` and `transparent_degradation`. The
`answer-*` and `authz-*` cases run the shipped code: the `AgentOrchestrator`, the verification gate and the
tool dispatcher. Each case pins the model's reply or tool call, so the run is deterministic.

Run the gate with `dotnet run --project tests/AgentForge.Evals -- evals`. It reports each M metric on its own
line and writes it to `results.json`. The image build runs the gate too (§2.1, M-W2-4).
[evals/results](evals/results/) holds the newest committed run, in which every rubric passed at 100 %.

| Metric | Requirement | Target | Where measured | Last reading |
|---|---|---|---|---|
| **M1: groundedness.** Every clinical fact the copilot asserts is backed by a cited source. | M1 (with FR-VERIF-1) | 100 %, zero tolerance | 6 `answer-m1-*` cases. The `grounded_answer` rubric checks that a claim the case marks ungrounded is **absent from what ships**. Each side of the population must be non-empty. | 0 ungrounded claims reached the answer in 6 cases |
| **M2: constraint recall.** Every seeded cardiology-rule violation raises a `DomainConstraintFlag`, and no rule class is missed. | M2 | 100 %, every rule class covered | 12 `answer-m2-*` cases: one seeded violation per rule in `CardiologyConstraintRules.Default`, each paired with a near-miss control that must raise nothing. The set of raised rule IDs must exactly equal the seeded set. | 6 of 6 violations flagged across 5 of 5 rule classes. The 6 controls raised 0 flags. |
| **M3: authorization integrity.** No role-confusion or prompt-injection case discloses data to a user who is not entitled to it. | M3 (with FR-EVAL-2, NFR-SEC-2) | 0 unauthorized disclosures | 13 `authz-*` cases through `McpToolDispatcher` → `PatientRelationshipGate` → `AuditingMcpToolServer`. The `no_unauthorized_disclosure` rubric counts failures. | 0 disclosures in 13 cases (4 permit, 9 deny). All 13 attempts were logged. |
| **M4: latency.** | M4 | See §1 | See §1 | See §1 |
| **M5: transparent degradation.** Under an injected fault, the copilot gives a visible partial answer or a refusal. It never crashes and never fabricates. | M5 (with NFR-REL-1) | 100 % transparent, 0 crashes, 0 silent fabrications | 7 `answer-m5-*` cases: tool unavailable, empty chart, LLM timeout, rate limit (429), truncated reply repaired, repair exhausted, and request deadline. The rubric requires a visible gap or fallback banner **and** a degradation log line. | 7 of 7 degraded visibly (4 fallback, 3 synthesized). 0 silent or fabricated answers. |

**Limits of these numbers:**

- **M1** only suppresses an uncited line that contains one of the fourteen `ClinicalFactKeywords`. A claim
  phrased without a keyword still ships.
- **M1** does not check what kind of statement a line makes. A correctly cited treatment recommendation still
  ships, even though NG1 (the copilot does not recommend treatment) is enforced only by the prompt.
- **Both M1 escapes are covered by pinned cases** and counted separately in `pinned_escapes` in
  [evals/baseline.json](evals/baseline.json). If either count changes, the gate fails.
- **M2** is 100 % over the rule set as it currently exists, counted per rule ID rather than per flag.
- **M3** has no model in the loop: each case pins the tool call that a fully compromised model would make.
  It also covers only `McpToolDispatcher`. The other authorization checkpoints (the SMART launch,
  `/evidence/ask` and `/evidence/document/{id}`) have no eval case.
- **M5** has cases for six of the twelve failure modes in the failure-mode table in
  [REQUIREMENTS.md](REQUIREMENTS.md). M1 covers one more (a claim that cannot be grounded) and M3 another
  (authorization denied). Four failure modes have no case: conflicting records, ambiguous query, SMART
  session expired, and dependency down.

### 2.1 Document and evidence pipeline metrics (M-W2-*)

| Metric | Requirement | Target | Where measured | Last reading |
|---|---|---|---|---|
| **Extraction integrity.** Every derived fact from a document is schema-valid and carries a citation. | M-W2-1 | 100 % | Rubrics `schema_valid` (50 cases) and `citation_present` (34 cases). Both are in the quality tier: the baseline is 1.0, the gate blocks below 95 %, and the tier floor is 0.80. | Both at 100 % |
| **Evidence separation.** Every guideline claim is labelled as evidence, separate from patient-record facts. | M-W2-2 | 100 % | `evidence_grounded` (10 cases) checks that every shipped guideline claim cites a `[Guideline/<chunkId>]` that retrieval actually returned. | **Partly measured.** No rubric checks that a record fact is never labelled as evidence, or the reverse. |
| **Routing inspectability.** Every handoff in the supervisor graph is logged and can be reconstructed from the correlation ID. | M-W2-3 | 100 % of handoffs | `agentforge_routing_decisions_total` (panel *Supervisor Routing Decisions*), the *Per-encounter story* logs panel, and graph spans exported to Tempo | Instrumented. No collected span waterfall has been read back from a deployed environment. |
| **Gate efficacy.** An injected regression fails the eval gate. | M-W2-4 | The injected regression is blocked | [tests/AgentForge.Evals](tests/AgentForge.Evals/) with [evals/baseline.json](evals/baseline.json): a safety tier at 1.0, a quality tier at 0.80, and `max_regression` 0.05 per category | Shown separately for each control: the absolute floor, the 5-point per-category regression check, and the safety floor catching a single-case PHI leak |
| **Robustness.** Every missing-data, no-evidence or low-confidence case yields a gap statement or a refusal. | M-W2-5 | 100 % | `safe_refusal` (extraction), `transparent_degradation` (missing data on the answer path), and `evidence-out-of-corpus-no-evidence-found` (no evidence) | **Partly measured.** The low-confidence case has no eval case. |
| **PHI hygiene.** No raw PHI appears in logs, traces, eval data or cost reports. | M-W2-6 (with NFR-SEC-W2-1) | 0 occurrences | `no_phi_in_logs` on 96 of the 98 cases. Unit tests also scan host stdout (`HostStdoutPhiScanTests`), committed eval artifacts (`CommittedArtifactPhiScanTests`) and log templates (`DiagnosticLogTemplateTests`). Spans are scrubbed in code by `SpanPhiScrubber`. | The gate passes, and no case passes without scanning something |

---

## 3. Observability requirements (`FR-OBS-*`, `NFR-TRACE-1`)

These requirements are facts about the code, not about traffic.

| Requirement | Meaning | How it is met |
|---|---|---|
| **FR-OBS-1** | Every log line, tool call and LLM call carries a correlation ID, so a full request can be reconstructed from logs alone. | `CorrelationIdMiddleware` scopes every HTTP request and accepts a well-formed inbound `X-Correlation-Id`. `CorrelationIdHandler` is on all outbound HTTP clients. Every model call writes one `llm.call` log line. Chat turns also carry `ConversationId`, a one-way SHA-256 of the session ID that stays the same across the turns of one conversation. |
| **FR-OBS-2** | For any past request, you can find each step, its timing, any failure and its cost. | From logs (Loki): the `llm.call` lines carry tokens, cost, stop reason and latency, or the failure status. Sum the lines under one correlation ID to get the call count and cost for a request. The Prometheus counters are aggregates only. |
| **FR-OBS-3** | A live dashboard shows request count, error rate, p50/p95 latency, tool counts, retry counts and verification pass/fail. | [agentforge.json](observability/grafana/dashboards/agentforge.json), 24 panels (§6). |
| **FR-OBS-4** | At least three alerts exist, and each documents what it means and how on-call should respond. | 9 rules in [agentforge-alerts.yml](observability/alerts/agentforge-alerts.yml) (§6). |
| **FR-OBS-W2-1** | Per-encounter telemetry for the evidence flow: tool sequence, latency by stage, and retrieval stats. | **Partly met.** The evidence row of the dashboard and the *Per-encounter story* panel cover it. A fallback exit and `/evidence/ask` do not yet write a per-encounter line. |
| **FR-OBS-W2-2** | The dashboard has ingestion, graph and retrieval panels. | 14 panels in the evidence row, including *Extraction Grounding Confidence* and *Extraction Field-Level Pass Rate*. |
| **FR-OBS-W2-3** | A cost and latency report covering development spend, projected production cost and latency. | Cost per query for all three LLM flows (§4), and latency (§7). |
| **FR-AUTH-2** (observability part) | Authorization permits and refusals are visible live. | `agentforge_authorization_decisions_total`, tagged `outcome` (`permit`/`refuse`) and `reason` (three fixed values). It is kept separate from the tool-failure rate, because a refusal is correct behaviour and must not trigger `AgentForgeHighToolFailureRate`. |
| **NFR-TRACE-1** | The correlation ID propagates from ingress to agent, tool, LLM and backend. | In logs: yes. In traces: the `AgentForge` ActivitySource exports over OTLP to Tempo when `Observability__TraceOtlpEndpoint` is set, with spans scrubbed of identifiers. Both local compose wirings run Tempo. |
| **NFR-TRACE-W2-1** | The same propagation, for the evidence graph's spans. | Spans exist for every graph worker and retrieval stage. They are collected only where a Tempo is configured. |

**An expired session is not an authorization decision.** When an OpenEMR access token has passed its
one-hour lifetime, the request fails as a turn failure (`agentforge_agent_turns_total{outcome="failure"}`).
It is also counted on `agentforge_expired_session_refusals_total`, tagged `surface` = `chat-pre-turn`,
`chat-turn`, `evidence-ask`, `evidence-document` or `agenda`. No panel or alert covers that counter yet, so
read it with a Prometheus query.

| Metric | Requirement | Target | Where measured |
|---|---|---|---|
| **SLO-W2-1, document ingestion.** | NFR-SLO-W2-1 | **p95 ≤ 11 s**, server-side | `agentforge_document_ingestion_duration_seconds{outcome="ingested"}`. No alert is attached. |
| **SLO-W2-1, evidence retrieval.** | NFR-SLO-W2-1 | **p95 ≤ 6 s**, server-side | `agentforge_evidence_retrieval_duration_seconds`, summed over both call sites. Alert: `AgentForgeHighEvidenceRetrievalLatencyP95`. |

**SLO-W2-1** (requirement NFR-SLO-W2-1) is the pair of service-level objectives for the document and
evidence pipeline. Its alerts cover extraction failure rate, retrieval latency and eval regression. No run
has yet measured a p95 for either stage. The targets come from measured means (§7.4).

### 3.1 The two SLO numbers, and what may alert on them

- **Both SLOs time a single stage on the server. Neither is the whole turn.**
  - Ingestion is timed inside `DocumentIngestionService.IngestAsync`: the content hash, the idempotency
    lookup, the extractor call and the fact write. It does not include the upload.
  - Retrieval is timed around `IEvidenceRetriever.RetrieveAsync`: the embedding, the dense and sparse
    queries, the fusion and the rerank.
  - The client-side evidence-turn p95 (12.9–14.9 s) measures the whole turn, so it is **not** the
    retrieval SLO.
- **The ingestion SLO filters by outcome. The retrieval SLO cannot.**
  - Ingestion outcomes are `ingested`, `already_ingested`, `extraction_rejected` and `error`. Only
    `ingested` counts toward the SLO, because the other outcomes are either much faster or are failures.
  - The *Document Ingestion Latency (p95)* panel is unfiltered and includes `error` samples.
  - The retrieval histogram is tagged only `entry_point` (`evidence_ask` or `chat_tool`). Hit and miss
    are recorded on the counter, not the histogram, so a fast-failing retrieval outage would make the
    latency series look better.
  - **For that reason `AgentForgeRetrievalDegradation` is a separate rule. Do not read the latency alert
    as an outage detector.**
- **Bucket resolution.** Both histograms have explicit boundaries every second from 5 s to 15 s, with
  **6** and **11** on a boundary. Unit tests check that the boundaries reach `/metrics` and that the
  retrieval rule's `> 6` falls on one of them.
- **A throwing extractor is counted.** If the extractor or the store throws, `outcome="error"` is recorded
  before the exception is re-thrown. `AgentForgeHighExtractionFailureRate` counts `extraction_rejected`
  and `error` together as failures. A dead extractor therefore crosses the threshold the same way a broken
  schema gate would.
- **Split by `entry_point` when you investigate.** The 6 s target's base mean was measured through
  `/evidence/ask` only. A busy path can hide a slow quiet one in the combined p95.

---

## 4. Cost

**`agentforge_llm_cost_usd_total`** multiplies real token counts by `Llm__InputPricePerMillionTokensUsd` and
`Llm__OutputPricePerMillionTokensUsd`.

- If those prices are unset, the counter records `0`.
- [docker-compose.yml](docker-compose.yml) defaults them to `2.00` and `10.00`, which is the list rate of
  the default `Llm__Model` (`claude-sonnet-5`).
- **Nothing checks the prices against the model.** If you change `Llm__Model`, change both prices too.
- The figure is the cost as priced from token counts, not the invoiced amount.

All three LLM paths record usage: the orchestrator turn, the evidence composer and the document extractor.
No metric counts LLM calls per turn. To get that number, count the `llm.call` log lines under one
correlation ID.

These are the measured costs per turn, from a metered pass with `claude-sonnet-5` priced at $2.00 / $10.00
per million tokens:

| Flow | Tokens in / out | LLM calls | Cost per turn |
|---|---|---|---|
| Pre-visit brief (`RequestBrief`) | 10,443 / 1,986 | Not instrumented | **$0.0407** |
| Evidence question (`/evidence/ask`) | 1,007 / 414 | 1 | **$0.0061** |
| Document upload (ingest) | 2,384 / 718 | 1 | **$0.0119** |

The original brief load run (§7.1) consumed 2,504,947 input and 198,021 output tokens, about **$6.99** at the
same rates.

---

## 5. What nothing measures

1. **NFR-PERF-1's measurement point.** The live series is server-side and the budget is end to end.
2. **NFR-PERF-1's concurrency condition.** No concurrency or in-flight-turn metric exists.
3. **M1's keyword boundary and its scope escape.** See §2.
4. **Four failure modes** have no fault-injection case: conflicting records, ambiguous query, SMART session
   expired, and dependency down.
5. **M2's `entered-in-error` exclusion.** Golden fixtures cannot express an Observation status, so only unit
   tests cover it.
6. **M-W2-2's labelling half.** No rubric checks the source-type label itself.
7. **LLM calls per turn.** No metric exists. Count from logs instead (§4).
8. **Distributed traces.** These are collected only where a Tempo backend is configured.
9. **Queue depth.** The meter has no gauge for it, and no runtime queue exists. `IngestionJob` and
   `IngestionStatus.Pending` are defined in the data model, but nothing produces or consumes them. If
   ingestion becomes queued, add a depth gauge and a panel.

The eval gate's per-category pass rate **is** live. The image build runs the gate and bakes the results into
the image. The sidecar then publishes four gauges:

- `agentforge_eval_category_pass_rate{category}`
- `agentforge_eval_baseline_pass_rate{category}`
- `agentforge_eval_run_timestamp_seconds`
- `agentforge_eval_run_passed`

---

## 6. How to read the dashboards and alerts

Everything is in [observability/](observability/), and
[observability/README.md](observability/README.md) explains how to run it. There are two local modes:

- With the sidecar running on the host:
  `docker compose -f observability/docker-compose.yml up`
- With the sidecar in a container:
  `docker compose -f docker-compose.yml -f docker-compose.observability.yml --profile copilot up -d`

Grafana is then at `http://localhost:3000`, and Prometheus at `http://localhost:9090`. Both are bound to
`127.0.0.1`. Set the Grafana credentials with `GRAFANA_ADMIN_USER` and `GRAFANA_ADMIN_PASSWORD`. Prometheus
scrapes the sidecar's unprefixed `/metrics` directly over the private network. The public front door returns
404 for `/agentforge/metrics`, because that endpoint has no authentication of its own.

**The dashboard** ([agentforge.json](observability/grafana/dashboards/agentforge.json)) is pre-provisioned
as *AgentForge Clinical Copilot*.

- **Core copilot panels:**
  - turn rate and turn error rate
  - **brief** turn latency p50/p95, with the other turn types plotted beside it and a 26 s threshold line
  - tool-call rate and tool failure rate by tool
  - verification pass/fail
  - authorization decisions
  - LLM tokens/s and cost rate
  - Polly retry rate by pipeline
- **Evidence row:**
  - ingestion rate by outcome, and ingestion p95
  - graph worker p95 by worker
  - routing decisions
  - retrieval hit rate and p95
  - rerank latency
  - retrieval degradations by stage
  - extraction grounding confidence
  - extraction field-level pass rate
- **Evals:**
  - *Eval Pass Rate by Rubric Category - running build*: a live gauge for the eval run the image was built
    with.
  - *Eval Pass Rate by Rubric Category* (offline snapshot) and *Eval Snapshot Provenance and Gate Verdict*:
    two markdown panels showing the newest committed run in [evals/results](evals/results/). They read
    no datasource.
- **Logs:** *Per-encounter story* reads Loki. Query `{service_name="agentforge-api"}` in Grafana Explore.
- **Traces:** search Tempo for service `agentforge-api`, or use
  `{ span.agentforge.correlation_id = "<id>" }`.

**Extraction grounding confidence is not a score the model reports.** It is the share of a document's
checkable citation quotes that were found verbatim in the document's own text. A value of 1.0 means every
checkable quote was found. A value of 0.0 means none was, which signals fabricated facts. A scanned document
with no text layer records no observation, and its facts appear as `outcome="unchecked"` on the field-outcome
counter instead.

**The alerts** ([agentforge-alerts.yml](observability/alerts/agentforge-alerts.yml)) are below. Each rule's
`description` states its meaning and the on-call response, so read it there first.

| Alert | Fires when | Severity |
|---|---|---|
| `AgentForgeHighTurnLatencyP95` | brief p95 > 26 s for 15m | warning |
| `AgentForgeHighTurnErrorRate` | > 5 % of turns fail for 5m: the core loop is broken | critical |
| `AgentForgeHighToolFailureRate` | > 10 % of tool calls fail for 5m. Break down by the `tool` label. | warning |
| `AgentForgeElevatedVerificationFailureRate` | > 20 % of drafts fail verification over 15m. This is a quality signal, not an outage. | warning |
| `AgentForgeRetrievalDegradation` | any retrieval-stage degradation over 10m | warning |
| `AgentForgeHighExtractionFailureRate` | > 20 % of ingests rejected or erroring over 30m, with at least 5 attempts, for 10m | warning |
| `AgentForgeHighEvidenceRetrievalLatencyP95` | retrieval p95 > 6 s for 10m | warning |
| `AgentForgeEvalCategoryRegression` | a rubric category is more than 0.05 below its baseline | critical |
| `AgentForgeEvalGateFailed` | the running image's own eval run was blocked by the gate | critical |

**Keep the rule file and its checks in step.**
[agentforge-alerts-tests.yml](observability/alerts/agentforge-alerts-tests.yml) holds promtool unit tests
that include near-miss cases. [alert-rules-gate.sh](observability/alerts/alert-rules-gate.sh) pins three
things against the rule file:

- each rule's expression
- each rule's `for:` duration
- the 0.05 regression threshold, which must equal `max_regression` in
  [evals/baseline.json](evals/baseline.json)

If you change a threshold or a duration, change the matching test and table in the same edit.

---

## 7. Performance baselines

**Environment.** The baselines were measured against one deployed sidecar container on a managed container
host, with a 24 vCPU / 24 GB limit. It was reached through the reverse-proxy front door under `/agentforge`,
with real OpenEMR, real LLM calls and a real reranker. Nothing was mocked. That host no longer exists. The
numbers still describe the sidecar's own processing, but this compose stack has not been re-measured.

### 7.1 Pre-visit brief (`RequestBrief` over SignalR)

| Concurrency | Calls | Errors | Error rate | p50 | p95 | p99 |
|---|---|---|---|---|---|---|
| 10 | 30 | 0 | 0.00 % | 16,210 ms | 20,765 ms | 23,412 ms |
| 50 | 133 | 0 | 0.00 % | 17,876 ms | 25,795 ms | 26,309 ms |

- **What one call is.** Each call is one fresh brief turn: LLM reasoning, FHIR tool calls and synthesis.
  There is about 1.4 LLM round trips per turn.
- **CPU and memory.** CPU averaged 0.008 vCPU (0.16 max), and memory averaged 137 MB (195 MB max). The
  service is I/O-bound: latency is dominated by the LLM and OpenEMR, not by the sidecar's own compute.
- **Throughput** was about 2.2 turns/s at 50 users.

### 7.2 Evidence question (`POST /evidence/ask`)

| Concurrency | Calls | Errors | p50 | p95 | p99 | Throughput |
|---|---|---|---|---|---|---|
| 10 | 54 | 0 | 10,704 ms | 14,938 ms | 15,202 ms | ~0.9 turns/s |
| 50 | 356 | 0 | 7,523 ms | 12,862 ms | 20,206 ms | ~5.9 turns/s |

The p50 at 50 users is lower than at 10 because the 10-user phase ran first, from a cold start. Each stage's
server-side cost per execution was:

| Stage | Average per execution |
|---|---|
| Evidence retriever (hybrid RAG) | **3.16 s** (470 executions) |
| Answer composer (one LLM call) | **4.51 s**, the dominant cost |
| Critic (deterministic) | 0.16 ms |
| Rerank | 141 ms |

Rerank recorded only 20 of 470 calls. The most likely cause is the rate limit on a trial reranker key, with
the other calls falling back to the fused order. Check `agentforge_retrieval_degradations_total{stage="rerank"}`
when you re-run.

### 7.3 Caveats on these numbers

- **The brief run was partial.** During that run four of the five FHIR tools returned 401, and the turns
  degraded around them. Treat the brief p95 as a **floor**: a full five-source brief would be slower and
  would cost more.
- **The evidence run predates a check that now runs on every request.** Each `/evidence/ask` now makes one
  OpenEMR `Appointment` search for the patient-relationship check. That adds one round trip to the
  client-side figures. It does not affect the retrieval SLO, because the check runs outside that stopwatch.
- **A 0 % error rate does not prove the system did the work.** Graceful degradation completes a turn even
  when its dependencies fail. A revoked `Llm__ApiKey` once produced a "clean" run with a p95 of 145 ms and
  0 errors, in which no model was ever reached.

### 7.4 How the SLO-W2-1 targets were derived

Each target is calculated the same way:

1. Take the stage's measured **mean**.
2. Multiply it by the widest p95/p50 ratio from any load run on file. That ratio is **1.71**, from the
   evidence run at 50 users.
3. Round up to the next whole second.

| Stage | Mean | × 1.71 | Target |
|---|---|---|---|
| Retrieval | 3.16 s (470 executions) | 5.40 s | **p95 ≤ 6 s** |
| Ingestion | 5.9 s (5 ingests, concurrency 1) | 10.09 s | **p95 ≤ 11 s** |

The ingestion derivation is the weaker of the two: it rests on only 5 samples, and its ratio was borrowed from
a different flow.

### 7.5 Re-running the load test

[tools/LoadTestChat](tools/LoadTestChat/) drives many concurrent connections across a small pool of real
sessions. Real sessions are needed because `/launch` requires a genuine SMART login in a browser, and the
harness has no way around it.

1. **Get sessions.** Either:
   - log in through `<your front-door URL>/agentforge/launch` in two or three separate browser profiles and
     copy each `.AspNetCore.Session` cookie, or
   - set `LoadTest__LoginUsername` and `LoadTest__LoginPassword` with a demo account, and the harness logs in
     with Playwright.
2. **Run the brief flow.**

   ```bash
   LoadTest__BaseUrl="https://copilot.example.org/agentforge" \
   LoadTest__SessionCookies=".AspNetCore.Session=<value1>;.AspNetCore.Session=<value2>" \
   LoadTest__ConcurrencyLevels="10,50" \
   LoadTest__DurationSeconds="60" \
   dotnet run --project tools/LoadTestChat
   ```

3. **Run the evidence flow.** Add a guideline question with
   `LoadTest__Question="What do current guidelines recommend for this patient's LDL target?"`.
   `LoadTest__PatientIds` selects the demo patients for the Playwright login.
4. **Start small.** Do a short run at concurrency 1 first. Every call spends real LLM money.
5. **Check that the system did the work before you quote a number.** Read `/metrics` and look for the
   series below. On a new process a series that was never recorded is **absent**, not zero. Treat a missing
   series as a failure.

| Flow | Series | Required value |
|---|---|---|
| Brief | `agentforge_tool_calls_total{outcome="success"}` | A healthy share of all tool calls. The unfiltered total counts failures too. |
| Brief | `agentforge_llm_tokens_total` | Non-zero |
| Brief | `agentforge_agent_turns_total` | Non-zero. This only shows that the hub was reached. |
| Evidence | `agentforge_worker_duration_seconds_count{worker="answer-composer"}` | Non-zero |
| Evidence | `agentforge_evidence_retrievals_total{outcome="hit",entry_point="evidence_ask"}` | Non-zero |
| Evidence | `agentforge_llm_tokens_total` | Non-zero |

6. **Read the matching server-side series.** For the brief flow, compare the run with
   `{turn_type="brief"}` on the turn histogram. An evidence run takes no orchestrator turn, so read the worker
   and `agentforge_evidence_retrieval_*` histograms instead.
7. **Record CPU and memory** from `docker stats` or your host's container metrics over the same window.

**Never load-test production.**

---

## 8. Security platform metrics

The security platform ([security-platform/](security-platform/), see
[SECURITY-PLATFORM.md](SECURITY-PLATFORM.md)) attacks the copilot. These metrics show whether it:

- finds vulnerabilities
- judges them consistently
- keeps fixed vulnerabilities fixed
- does all of that at a known cost

**Current status.** The shipped image is a deterministic HTTP replayer. It sends allowlisted attack cases
against a target, enforces a spend ceiling, and writes a results document for each run (cost, halt reason,
event trace). The four agents (Orchestrator, Red Team, Judge, Documentation) are defined, with versioned
prompts and message schemas, but they do not run yet. **So most rows below have no reading yet.** The
platform's own store is separate from the copilot's Prometheus/Grafana stack, which observes the target.

| Metric | Requirement | Definition | Produced by | Target |
|---|---|---|---|---|
| **Attack success rate (ASR), per category** | FR-OBSV-W3-2, FR-MAS-W3-5 | For each attack category and each target build: the attacks the Judge rules successful, divided by the attacks judged. Partial results are counted separately. | Judge | — |
| **Resilience trend** | FR-OBSV-W3-3 | The change in ASR per category from one target build to the next. | Derived from Judge rulings | — |
| **Open findings by state** | FR-OBSV-W3-4, FR-DOCAGENT-W3-2 | Confirmed vulnerabilities by state (open, in progress, resolved) and by severity. A finding is resolved only when its regression case passes against a fixed build. | Documentation agent | At least 3 reports |
| **Category coverage** | FR-OBSV-W3-1, FR-THREAT-W3-2, FR-SUITE-W3-1 | Threat-model categories that have at least one judged case, with the case count per category. | Judge | 6 categories in the threat model, at least 3 with results |
| **OWASP mapping coverage** | NFR-OWASP-W3-1 | The share of attack cases with an OWASP Top 10 or OWASP LLM Top 10 category (the `owasp` field in [attack-case.schema.json](security-platform/schema/attack-case.schema.json)). | Case author | 100 % |
| **Novel-case yield** | FR-MAS-W3-2, FR-MAS-W3-3, FR-SUITE-W3-3 | Cases the Red Team generated or mutated beyond the seed suite, and how many of them succeeded or partly succeeded. | Red Team, joined to Judge rulings | — |
| **Regression pass rate** | FR-REGR-W3-1, FR-REGR-W3-2 | Over one full replay of the stored exploit cases against a build: the cases whose exploit no longer works, divided by the cases replayed. | Regression harness | — |
| **Reappearances** | FR-REGR-W3-3 | Resolved findings whose regression case fails again on a later build. The finding goes back to open. | Regression harness | Every one detected |
| **Cross-category regressions** | FR-REGR-W3-4 | Fixes after which another category's ASR rose, or one of its regression cases began failing. | Regression harness | Every one flagged |
| **Judge agreement with ground truth** | FR-MAS-W3-9, NFR-AIDISC-W3-1 | On a fixed, labelled calibration set: correct rulings divided by the set size, with the misses split into missed exploits and false alarms. | Judge calibration run | — |
| **Judge drift** | FR-MAS-W3-9, NFR-AIDISC-W3-1 | The change in agreement between calibration runs after the model, prompt or target changes. | Judge calibration run | — |
| **Confirmed exploits approved** | NFR-TESTDESIGN-W3-1, FR-MAS-W3-9 | Known exploits in the calibration set that the Judge rules safe. | Judge, tested by the platform's own tests | **0**, an invariant |
| **Cost per run** | FR-OBSV-W3-5 | Total model spend for one run, broken down per agent. | Each agent; the Orchestrator holds the total | — |
| **Cost per confirmed finding** | FR-MAS-W3-7, FR-OBSV-W3-5 | Cost per run divided by the findings confirmed in that run. A run with no confirmed finding is reported as spend with no result. | Orchestrator | — |
| **Budget-halt events** | FR-MAS-W3-7 | Runs stopped by the hard spend ceiling, with the spend at the moment of the halt. | Orchestrator. The replayer already records `halted` and `halt_reason: "spend_ceiling"`. | The ceiling holds |
| **Traced runs** | FR-OBSV-W3-6 | The share of runs with one end-to-end trace that covers every agent step, in order. | Every agent. The replayer already writes an ordered event trace for each run. | 100 % |

**How the metrics fit together.**

- **ASR** says whether the copilot holds up under attack.
- **Coverage** is the denominator that makes ASR meaningful.
- **Novel-case yield** separates a platform that finds new attacks from a static list of payloads.
- **The regression rows** show that fixes stay fixed.
- **The Judge rows** back all the others: every other number is a count of Judge rulings, so a Judge that is
  wrong or drifting would move every number while each one still looked plausible.
- **The cost rows** are the inputs for projecting cost at scale.

**The replayer's spend ceiling** is set with `SECURITY_PLATFORM_SPEND_CEILING_USD` and
`SECURITY_PLATFORM_USD_PER_REQUEST`. Each case is sent whole or not at all. After a halt, nothing more is
sent.

**Preconditions.** None of these metrics means anything until two conditions hold:

- a live target, not a mock, is under attack (FR-TARGET-W3-2)
- at least one agent role runs live (FR-SUITE-W3-4)

The production front door is refused in code. Point the platform at a staging front door.

**Relationship to the copilot's metrics.**

- **M3 and ASR measure the same boundary with different instruments.** M3 (§2) uses pinned tool calls
  scored by rubrics. ASR uses a Judge ruling on a live target. Neither can stand in for the other.
- **The full cost of an attack run has two parts, and no counter reports their sum.** The copilot's cost
  counter (§4) prices the copilot's own model calls, which the attacks cause. The platform's cost rows price
  the platform's own calls.
