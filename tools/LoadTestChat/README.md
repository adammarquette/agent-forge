# LoadTestChat

Load/stress-test harness for the deployed AgentForge sidecar (REQUIREMENTS.md
NFR-PERF-3/4). Not part of the shipped product and not run by CI - `dotnet build`/`dotnet format` cover it,
but `unit-tests`/`integration-tests` filter by project filename and never invoke it.

## Why session cookies, not a scripted login

There is no way to programmatically mint many distinct authenticated chat sessions: `/launch` on the real
deployed app requires a genuine browser-mediated SMART OAuth redirect through OpenEMR every time - the
test-only session-seed bypass used by the integration test suite only exists in the in-process test host,
never in the real deployed binary (by design - it's an auth bypass, and it must never exist in a real
deployment).

Instead, this tool multiplexes many concurrent SignalR connections across a **small pool of real session
cookies** you obtain once via an actual browser login. This tests server-side concurrency/compute handling
under load - what NFR-PERF-3/4 measures - without touching production auth surface at all.

## Getting a session cookie

1. Open `http://localhost:8080/agentforge/launch` in a real browser (adjust to your stack's front door).
2. Log in and approve as usual - you'll land on the chat SPA once the session is established.
3. Open DevTools → Application (Chrome) / Storage (Firefox) → Cookies, find the session cookie (default
   ASP.NET Core session cookie name: `.AspNetCore.Session`), and copy its value.
4. Repeat 1-3 a couple more times (in separate browser profiles/incognito windows, so each login is a
   genuinely distinct session) if you want a bigger pool for the real run - one is enough for the dry run.

## Running it

```bash
LoadTest__SessionCookies=".AspNetCore.Session=<value1>;.AspNetCore.Session=<value2>" \
LoadTest__ConcurrencyLevels="10,50" \
LoadTest__DurationSeconds="60" \
dotnet run --project tools/LoadTestChat
```

**Each brief worker first reads `GET /patient` once** and connects to the hub with the `contextKey` it returns,
as the chat page does. The hub refuses every method on a connection without it (`ChatHub.PatientChangedMessage`,
`ARCHITECTURE.md` §8.2), so a harness that skipped it would report a 100% error rate. That read is
connection setup and is not timed, but it is real: four FHIR reads and one `patient_context` access-audit row
per worker, per concurrency level. A worker whose `GET /patient` fails records one `GET /patient failed` error
and stops, so an old sidecar without the key shows up as errors rather than as a plausible latency.

**So does each evidence worker** (`LoadTest__Question` set): it sends the same `contextKey` as the `context` form
field on every `POST /evidence/ask`, as `evidence.html` does, because the endpoint answers a form without it
`409` (`EvidenceEndpoints`, `ARCHITECTURE.md` §8.2). Same untimed read, same audit row, same
`GET /patient failed` error when it fails.

Every call is a real `RequestBrief` chat turn against real OpenEMR/LLM dependencies (unless you set
`LoadTest__Question`, below, which switches the flow) - **this spends real money** (REQUIREMENTS.md explicitly expects
and asks for the actual dollar figure to be reported, pulled from the `agentforge_llm_cost_usd_total`
metric). Start with a short, low-concurrency dry run to confirm the harness
actually works before running the full 10/50-concurrent-user pass.

### Measuring the Week-2 evidence flow

Set `LoadTest__Question` to a guideline/evidence question and every call becomes a `POST /evidence/ask`
request driving the hybrid-RAG `retrieve_evidence` path (Core Req 3) instead of the Week-1 brief. It is a
stateless HTTP call to the Week-2 evidence graph, not a chat turn — see *Output* for what that means for the
metrics. Both flows share the same client-side round-trip measurement, so the two runs are directly
comparable — the vs-Week-1 baseline the cost/latency report needs.

```bash
LoadTest__LoginUsername=cardio1 LoadTest__LoginPassword='<demo password>' \
LoadTest__Question="What do the current cardiology guidelines recommend for this patient's LDL target given their ASCVD risk?" \
LoadTest__ConcurrencyLevels="10,50" LoadTest__DurationSeconds="60" \
dotnet run --project tools/LoadTestChat
```

### Environment variables

| Variable | Purpose |
|---|---|
| `LoadTest__SessionCookies` | **Required unless the login vars below are set.** `;`-separated `Name=Value` session cookies from real browser logins |
| `LoadTest__BaseUrl` | Deployed base URL (defaults to the local compose front door, `http://localhost:8080/agentforge`) |
| `LoadTest__ConcurrencyLevels` | `,`-separated concurrency levels to run in sequence (default `10`) |
| `LoadTest__DurationSeconds` | How long to hammer each concurrency level, in seconds (default `15`) |
| `LoadTest__Question` | If set, every call is a stateless `POST /evidence/ask` **evidence request** (Week-2 hybrid-RAG path, Core Req 3) instead of a `RequestBrief` brief (Week-1). Use a guideline/evidence question so the graph invokes `retrieve_evidence`. Records no `agentforge_agent_turn_duration_seconds` sample — see *Output*. |
| `LoadTest__LoginUsername` / `LoadTest__LoginPassword` | Alternative to `LoadTest__SessionCookies`: auto-bootstrap real sessions via Playwright (a genuine `/launch` SMART login). |
| `LoadTest__PatientIds` | `,`-separated patient ids for the Playwright bootstrap - one distinct session per id. |

## Output

For each concurrency level: total calls, successes/errors, error rate, and p50/p95/p99 latency in
milliseconds, computed client-side from real round-trip timings - the numbers NFR-PERF-4 asks for. Cross-
reference against the deployed app's own `/metrics` (`agentforge_agent_turn_duration_seconds` histogram,
`agentforge_agent_turns_total`, `agentforge_llm_cost_usd_total`) and `docker stats` (CPU/memory) for
the full baseline picture.

**Qualify the histogram by `turn_type` when you do.** Since a separate change the duration histogram is split
`brief` / `agenda` / `follow_up`, so an unqualified query sums populations that are not comparable. A default
run drives `RequestBrief` turns and should be read against `{turn_type="brief"}` — the same series
`AgentForgeHighTurnLatencyP95` and `NFR-PERF-1` are stated over.

**Setting `LoadTest__Question` leaves that histogram empty altogether — do not read it as a latency
statement about the evidence flow.** The evidence run does not take an orchestrator turn at all: it POSTs to
`/evidence/ask`, which `EvidenceEndpoints.HandleAskAsync` serves from `IEvidenceAgentSupervisor` without
ever entering `AgentOrchestrator.RunTurnAsync` — the only place `RecordAgentTurn` is called. So during an
evidence-flow run **every** `turn_type` series looks idle, `follow_up` included, and an empty
`{turn_type="follow_up"}` query means the flow was never instrumented there, not that the label is broken.
(`turn_type="follow_up"` does exist, but it is the in-session chat follow-up over SignalR — `UC-2`, which
this harness has no mode for.) The server-side counterparts that *do* move for an evidence run are
`agentforge_worker_duration_seconds{worker=...}` per graph stage and the
`agentforge_evidence_retrieval_*` histograms; end to end, the harness's own client-side percentiles above
are the only latency numbers that flow has.

## Before you believe a number

**Every dependency failure this harness can hit reports as success** - `NFR-REL-1` graceful degradation
completes a turn whose dependencies failed - **but the two you are likeliest to hit do not look alike, and
only one of them looks wrong.**

- **A 401ing FHIR scope** gives you `0.00% error rate` at a **completely plausible p95**, because the model
  still runs: that is how 2026-07-10 produced p95 20,765 ms at 10 concurrent and 25,795 ms at 50 - real
  numbers that describe a *partial* brief, one tool of five, rather than the five-source brief the product
  targets. Nothing about the figure announces this.
- **A revoked `Llm__ApiKey`** collapses the turn instead. A 2026-09-20 dry run reported **p50 124 ms, p95
  145 ms, 0 errors** over 64 calls that reached no model at all, with the service's own
  `agentforge_agent_turns_total{outcome="success"}` agreeing
  (`METRICS.md`, *2026-09-20: the re-run attempt*).

The second is absurd enough to catch by eye. **The first is not, and it is the one that has already gone into
a results table.**

So after any run, read `/metrics` and check all three of these before quoting a latency:

| Series | Require | Failing it means |
|---|---|---|
| `agentforge_tool_calls_total{outcome="success"}` | non-zero **and a healthy share of that tool's total** | no FHIR tool *succeeded* - the brief was written from an empty chart |
| `agentforge_llm_tokens_total` | non-zero | no LLM call succeeded - the turns are hollow |
| `agentforge_agent_turns_total` | non-zero | the hub was never reached at all |

**The `outcome` filter on the first row is the whole point of it.**
`AgentForgeMetrics.RecordToolCall` increments `agentforge_tool_calls_total` for a **failed** call as well as a
successful one, tagging it `outcome="failure"` rather than skipping it - `McpToolDispatcher` records both
branches. So an unqualified `> 0` on that counter is satisfied by exactly the condition this check exists to
catch: the 2026-07-10 run, where four of five FHIR tools 401ed throughout and the p95 that came out of it
describes a partial brief. Compare success against total and require the ratio to be healthy, which is the
*success ratio* the precondition asks for and not the same thing as a non-zero count.

On a freshly started process these are **absent rather than zero** - an OpenTelemetry series is created by
its first recording - so a grep that matches no line is not a passing check. Read the value, and treat a
missing series as a failure rather than as a pass.

**The table above is written for the default `RequestBrief` run. Two of its three rows invert on an evidence
run** (`LoadTest__Question` set) and will read as failures when nothing is wrong: `/evidence/ask` takes no
orchestrator turn, so `agentforge_agent_turns_total` does not move, and the graph retrieves from the guideline
corpus rather than dispatching MCP FHIR tools, so `agentforge_tool_calls_total` does not either. Only
`agentforge_llm_tokens_total` carries over unchanged. The evidence-run equivalents of the first two rows are
`agentforge_worker_duration_seconds_count{worker="answer-composer"}` (turns that reached the composer - the
denominator `METRICS.md` uses for this flow) and `agentforge_evidence_retrievals_total{outcome="hit",entry_point="evidence_ask"}`
(retrieval actually returned candidates; the label excludes chat-tool retrievals, which record there too since
a separate change).
