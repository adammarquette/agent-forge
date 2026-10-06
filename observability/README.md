# Observability stack (Epic 9 + Epic 107)

Self-hosted **Prometheus + Loki + Tempo + Grafana**. Prometheus scrapes the sidecar's own OpenTelemetry
`/metrics` endpoint; Loki (Epic 107) ingests the sidecar's **logs** over OpenTelemetry's OTLP/HTTP
exporter; Tempo ingests its **spans** the same way, scrubbed of identifiers first; Grafana reads
from all three (`Program.cs`). ARCHITECTURE.md §11 / CONVENTIONS.md §7
keep the sidecar itself provider-agnostic - this folder is the one place specific backends are chosen,
and it's optional infra: the app boots and serves traffic without any of it (see
`ObservabilityHealthCheck`; the log and trace exporters are fail-open - an unset log endpoint means
console-only logging, an unset trace endpoint means no trace exporter at all).

## Run it

Which file you use depends on **where the sidecar runs**, because that decides whether Prometheus can
reach it. Picking the wrong one is not an error - it comes up clean and every dashboard panel is blank.

**A. Sidecar on the host (`dotnet run`)** - this folder's own stack, a separate compose project:

```bash
# 1. Start the sidecar (separately) - the fixed local dev port is 5113:
dotnet run --project src/AgentForge.Api

# 2. Start the observability stack (from the repo root). No --env-file: compose resolves the
#    default .env from THIS folder, so observability/.env is where the credential goes
#    (cp observability/.env.example observability/.env):
docker compose -f observability/docker-compose.yml up
```

> Keeping `GRAFANA_ADMIN_*` in the **repo-root** `.env` instead means adding `--env-file .env` to that
> command every time — without it the repo-root file is silently ignored and Grafana stays on
> `admin`/`admin`, and with it the file has to exist, because an explicit `--env-file` is required where
> the default `.env` is optional (a clean clone has neither, and the command exits 1).

**B. Sidecar in a container (`--profile copilot`)** - the repo-root overlay, which runs these four
*inside* the main compose project so they share its network (see below):

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml --profile copilot up -d
```

Either way Grafana is on <http://localhost:3000> with the same dashboard and datasources. **What feeds
them is not the same**, so each bullet below says which run mode it describes.

> **Not `localhost:8080/grafana`, locally.** The front door's `/grafana` route needs a
> `GRAFANA_UPSTREAM` that no compose file sets, so it answers **`404`** here; it is the *deployed* wiring's
> way in. Pointing it at this Grafana would mean turning `GF_SERVER_SERVE_FROM_SUB_PATH` on, which would break
> the direct `:3000` address above — so both local modes keep the direct one and leave the path dark.
> reference: [`../reverse-proxy/README.md`](../reverse-proxy/README.md)

- Grafana (**A and B**): <http://localhost:3000> (`admin`/`admin` by default). **Both** modes publish it on
  `127.0.0.1` only and take the credential from `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` (reference:
  #438). It matters most under **B**, where Grafana shares a network with the databases. **Where that
  credential lives differs by mode**, because compose resolves the default `.env` from the first compose
  file's directory: under **B** that is the repo root, so the repo-root `.env` is read with no flag; under
  **A** it is `observability/`, so the credential belongs in `observability/.env`
  (`observability/.env.example`) — or in the repo-root `.env` with `--env-file .env` passed explicitly. Put
  it in the repo-root `.env` and run **A** without the flag and Grafana stays on the default silently. The
  **AgentForge Clinical Copilot** dashboard is pre-provisioned (`grafana/dashboards/agentforge.json`):
  agent-turn rate, error rate, p50/p95 latency, tool-call rate + failure rate by tool,
  verification pass/fail rate, authorization permit/refuse decisions, LLM tokens/cost, and Polly
  retry rate by pipeline. **The authorization and retry panels are new in a separate change** and are what
  make `FR-OBS-3`'s *decision outcomes* and *retry counts* legs real: the retry panel was a raw
  `{otel_scope_name="Polly"}` dump that counted nothing, and the authorization decision had no
  counter at all, so an entitlement refusal was visible only in the access-audit log.
  **This describes the dashboard in this tree, which is what wirings A and B both load.** The
  hand-built image staging ran is older and lacks those two panels (staging deploys each
  `develop` build since) — see *Deploying it alongside the
  sidecar* below.
  **Two of its panels are fed by no datasource at all, deliberately**: *Eval Pass Rate by
  Rubric Category* and *Eval Snapshot Provenance and Gate Verdict* are markdown rendering the newest
  committed `../evals/results/<date>.json` - the run on the record, which no environment publishes. The live
  counterpart is *Eval Pass Rate by Rubric Category - running build*, which plots the run the
  deployed image was built with from the same series `AgentForgeEvalCategoryRegression` evaluates; see
  *Alerts* below. The two markdown panels read identically whether or not the sidecar is
  running or Prometheus is scraping anything, and each says so on its own face rather than in a tooltip.
  Re-render them from the newest committed snapshot whenever a new eval run is committed.
- Prometheus (**A and B**): <http://localhost:9090> - evaluates `alerts/agentforge-alerts.yml` in both
  modes. The scrape target is what differs: under **A** stock `prom/prometheus` scrapes
  `host.docker.internal:5113/metrics` every 15s from a mounted `prometheus/prometheus.yml`; under **B** the
  overlay BUILDS `prometheus/Dockerfile` and its entrypoint renders
  `SIDECAR_TARGET=reverse-proxy:${SIDECAR_PORT:-8081}` into `prometheus/prometheus.deployed.yml` - **the
  sidecar**, which under compose runs in the proxy's network namespace and so answers on the proxy's name at
  its own port (`DEPLOYMENT.md` §2). That port is Kestrel directly, not through
  nginx, so the scrape path stays the unprefixed `/metrics` - and that endpoint is not the same one a
  front-door client reaches. **On Railway the scrape target is `agent-forge-api:8080`** (`.railway/railway.ts`
  `SIDECAR_TARGET`), where the sidecar has its own private hostname rather than sharing the proxy's namespace;
  it is still a direct private-network scrape at Kestrel that never touches the proxy listener. `/agentforge/metrics`,
  the path the public proxy would forward to, is hard-`404`ed at `reverse-proxy/nginx.conf.template` since
  with a **case-insensitive** regex, since the sidecar routes case-insensitively — precisely because
  the endpoint has no auth of its own and these scrape targets are the only callers it needs to answer
  (`DEPLOYMENT.md` §3). **Rendered rather than mounted**, because
  `SIDECAR_PORT` moves Kestrel and the proxy together and a literal target would silently be left behind -
  blank dashboards behind a `/ready` that still says `Healthy` being the only symptom. Editing
  `prometheus.deployed.yml` under **B** therefore needs `up --build`; the alert rules are still a live mount.
  reference: #417, a separate change
- Loki (**A and B**): <http://localhost:3100> - query logs from **Grafana → Explore → Loki**, e.g.
  `{service_name="agentforge-api"}`. How the sidecar reaches it is what differs: under **A** it pushes
  automatically in Development (`appsettings.Development.json` sets `Observability:LokiOtlpEndpoint` to
  `http://localhost:3100/otlp/v1/logs`); under **B** that same value would mean the sidecar's *own*
  container, so the overlay sets `Observability__LokiOtlpEndpoint=http://loki:3100/otlp/v1/logs` on
  `agent-forge-api` instead. Either way, if Loki isn't running the sidecar just logs to console
  (fail-open). Config in `loki/loki-config.yaml`.
- Tempo (**A and B**): <http://localhost:3200> is its query API; read traces in **Grafana → Explore
  → Tempo** - search by service `agentforge-api`, or TraceQL such as
  `{ span.agentforge.correlation_id = "<id>" }` for one evidence ask (the attribute is on its `evidence.ask`
  root; the smoke run exercised HTTP spans, not a live ask). Reaching it differs the way
  Loki does: under **A** `appsettings.Development.json` sets `Observability:TraceOtlpEndpoint` to
  `http://localhost:4318/v1/traces`, which is why this folder's file also publishes `127.0.0.1:4318`; under
  **B** the overlay sets `Observability__TraceOtlpEndpoint=http://tempo:4318/v1/traces` and publishes only
  the query port. Tempo not running means spans are dropped and nothing else changes. **What arrives is
  already scrubbed** (`SpanPhiScrubber`): URLs keep their origin and an `{id}`-templated path, and query
  strings, client addresses, free-text status and `exception` events never leave the process. **Spans are
  no longer printed to stdout by default** - set `Observability__TraceConsoleExporter=true` to see them
  there while debugging without Tempo. Config in `tempo/tempo.yaml`.

## Pointing it at the containerized sidecar

The default `prometheus/prometheus.yml` scrapes `host.docker.internal:5113` — the sidecar run locally with
`dotnet run`. Against the sidecar **container** that target is dead, and this folder's compose file is a
*separate* compose project, so it lands on its own network and cannot reach the sidecar at all.

Use [`../docker-compose.observability.yml`](../docker-compose.observability.yml) (run mode **B** above)
rather than editing anything. It is an overlay on the main stack, so the four services join that project's
network, and it closes both halves of the gap:

| Half | What the overlay does |
|---|---|
| Metrics | Builds `prometheus/Dockerfile` (tag `agent-forge-prometheus:local`, or `PROMETHEUS_IMAGE`) and passes `SIDECAR_TARGET=reverse-proxy:${SIDECAR_PORT:-8081}`, which its entrypoint renders into `prometheus/prometheus.deployed.yml` — that address **is** the sidecar's Kestrel, because it shares the proxy's network namespace and has no name of its own. The stock image cannot do this: Prometheus performs no environment substitution, so a mounted config would pin a literal port that `SIDECAR_PORT` walks away from. |
| Logs | Sets `Observability__LokiOtlpEndpoint=http://loki:3100/otlp/v1/logs` on `agent-forge-api`. Compose sets no such variable otherwise — only `appsettings.Development.json` does, and it points at `localhost:3100`, which **inside a container is the container itself**. |
| Traces | Sets `Observability__TraceOtlpEndpoint=http://tempo:4318/v1/traces` on `agent-forge-api`, for the same reason as logs. |
| Readiness | Sets `Observability__PrometheusHealthUrl=http://prometheus:9090/-/healthy` on `agent-forge-api`, for the same reason: `localhost:9090` inside the container is the container. This is what makes `/ready` report `Healthy` instead of `Degraded` — see *Readiness* below. |

Grafana's provisioned datasources already address `prometheus:9090` / `loki:3100` / `tempo:3200` by service name, so the
shared network is all they need — no datasource edit either way.

## Deploying it alongside the sidecar

> **Production is declared and pinned since 2026-09-24 (carried to `develop`'s source by
> a separate change).** Its `OBSERVABILITY_IMAGES` entry carries the two CI-published tags staging ran when it was
> promoted — the same procedure in `DEPLOYMENT.md` §9 *Promoting the observability tier to production* fills
> it for the next promotion too. Its Prometheus TSDB is 1024 MB, with retention of `15d` or `400MB`,
> whichever comes first (`PROMETHEUS_TSDB_BY_ENV`). The Prometheus image now takes
> `PROMETHEUS_RETENTION_TIME` / `PROMETHEUS_RETENTION_SIZE`. When they are unset, as in both local wirings,
> Prometheus keeps its own default of `15d` with no size cap.
>
> **Staging is running this.** `.railway/railway.ts` declares
> `prometheus` and `grafana` as services on the **staging** environment, built from the two Dockerfiles here,
> its pins are filled, and **both services were `Online` there when this was written** (2026-09-21) — stood up
> by an **operator** apply, not by a pipeline: no job of ours created them. Check rather than trust this line:
> `railway service list --environment staging`. **Since a separate change staging runs CI output, always**: its
> entries are `TRACKS_DEVELOP`, so every `develop` push deploys the images `publish-observability-images`
> built from that commit, and the first such apply replaces the images staging ran until then, which were
> built and pushed **by hand** — the remaining half, done. **That hand-built grafana image was a
> dashboard revision behind this directory** — `grafana-sha-4768e5d79880` predates a separate change, so it carries 18
> non-row panels rather than 24: no *Authorization Decisions (permit / refuse)*, the superseded raw
> `{otel_scope_name="Polly"}` table instead of the per-pipeline retry rate, neither of the two extraction
> panels a separate change added on 2026-09-22 (*Extraction Grounding Confidence*, *Extraction Field-Level Pass
> Rate*), and neither of the two eval-snapshot panels a separate change added the same day (*Eval Pass Rate by
> Rubric Category*, *Eval Snapshot Provenance and Gate Verdict*), nor the live eval panel a separate change added.
> It also predates a separate change, so its
> turn-latency panel is the unfiltered one — a single p50/p95 over every turn type, no threshold line —
> rather than the `turn_type="brief"` panel `NFR-PERF-1`'s budget actually applies to; a difference in a
> panel rather than a seventh missing one. The instruments are exported by the
> sidecar regardless; it is the panels that are missing, and a `develop` apply is what
> deploys them. **The last
> two are not instruments at all** — they render a committed `evals/results/<date>.json` as markdown, so
> republishing the image is the *whole* of what a deployed Grafana needs in order to show them.
> Production's `OBSERVABILITY_IMAGES` entry is **filled**, since its 2026-09-24 promotion — an empty or
> missing pin would omit both services from the graph entirely, but that is no longer this environment's
> state. Two differences from every local run:
> **Loki is not deployed** (metrics only — a separate change owns the log drain and its PHI scrubbing), and
> **Grafana is reachable from the internet** — at **`<front door>/grafana`**, routed there by
> the `reverse-proxy` service over the private network rather than at a Railway domain of its own, and told it
> lives under that prefix by `GF_SERVER_ROOT_URL` + `GF_SERVER_SERVE_FROM_SUB_PATH` (both, or its redirects
> and assets point at `/`). **That is a different host name, not a smaller audience**: the proxy authenticates
> nothing, so its credential is still the entire boundary — and the *Generate Domain* step that used to gate
> the exposure is gone, so the boundary is load-bearing from the apply onward. It comes from
> **environment-level shared variables, NOT `preserve()`** — never from a file. That distinction is the
> security of the service and this file had it backwards: `preserve()` declines to manage a value, and an
> unset `GF_SECURITY_ADMIN_PASSWORD` fails **open** (`admin/admin`). `.railway/railway.ts` is authoritative. Prometheus has no domain at all. Production runs the
> same wiring, with its credential meant to be set **before** its hand apply — confirm the
> credential live rather than trusting that step happened, per `DEPLOYMENT.md` §9. See
> [`DEPLOYMENT.md`](../DEPLOYMENT.md) § *Observability: three
> wirings* for the ruling that allows it, and for why it would not hold for real patient data.
>
> The two images are parameterized, and since a separate change the **overlay uses that parameterization too** rather
> than only the deploy: Prometheus renders `SIDECAR_TARGET` into its scrape config at start (default
> `reverse-proxy:8081`, the compose value — the template, the entrypoint's `sed` pattern and
> `ENV SIDECAR_TARGET` are one address written three times and move together), and Grafana interpolates
> `PROMETHEUS_URL` / `LOKI_URL` / `TEMPO_URL` into its provisioned datasources. `PROMETHEUS_URL` defaults to the compose container name. **`LOKI_URL` has no default since a separate change**: empty or unset means "no Loki here", and the image then provisions neither the Loki datasource nor its panel. **`TEMPO_URL` likewise has none**: empty or unset means no Tempo datasource.
> Because the defaults *are* the compose values, an overlay run at the default `SIDECAR_PORT` renders a file
> byte-identical to the one that used to be bind-mounted — the change buys the moved case, and costs the
> unmoved one nothing.


Each of the four has its own `Dockerfile` (`prometheus/`, `loki/`, `tempo/`, `grafana/`) with the build context at the
repo root, so the same images that back this compose file deploy anywhere containers do — the compose file is
the reference wiring, not a special local-only mode. Two things travel with them wherever they go:

- **Grafana is the only surface that should ever be reachable.** Prometheus, Loki and Tempo have **no auth of
  their own**; keep them on the private network. Grafana's admin credentials come from `GF_SECURITY_ADMIN_USER` /
  `GF_SECURITY_ADMIN_PASSWORD` in the environment — **never baked into the image**, and never left at the
  `admin`/`admin` default outside a local run. Both compose files wire that passthrough
  (`GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD`, reference: #438) — but a passthrough is not a value: a
  deployment built from these images still has to supply a real one from its own environment.
- **Loki needs a durable mount at `/loki`** so ingested logs survive a container restart, the same way the
  sidecar needs one at `/keys`. The root overlay provides one (the `loki-data` volume); **this folder's
  compose file does not**, so a restart there discards whatever Loki had ingested. It is one of several
  wiring differences between the two files — the others being the network and compose project, the
  Prometheus scrape target, how the sidecar reaches Loki, `restart: unless-stopped` (overlay only), and
  Prometheus's `extra_hosts: host.docker.internal` (this folder's file only, since its target is the host).
  [`DEPLOYMENT.md`](../DEPLOYMENT.md) § *Observability: three wirings* is the
  side-by-side.
- **Tempo follows the same rule at `/var/tempo`**: the overlay mounts `tempo-data` there, this
  folder's file mounts nothing, and the deployed service mounts a Railway volume.

See [`DEPLOYMENT.md`](../DEPLOYMENT.md) for the stack this sits beside and
[`DEPLOYMENT.md`](../DEPLOYMENT.md) for where it lands in the
network picture.

### Loki on staging, and why not in production

**Staging only.** `.railway/railway.ts` declares a `loki` service wherever `LOKI_IMAGE_BY_ENV` carries a
filled pin **and** the metrics tier is declared. That map has a `staging` key and **no `production` key**,
and `npm run iac:selftest` refuses one. The reason is a separate change: production's sidecar logs carried FHIR
Patient ids on every tool call, so shipping them to a queryable store would have put patient identifiers one
Grafana login away. Since a separate change item 1 the access-audit trail, which is the stream that names the patient,
is filtered out of the OTLP push and stays on stdout, so Loki would receive diagnostic lines only. Adding a
production key is still the maintainer's separate decision; switching Loki on in staging is a separate change.
Staging holds synthetic data only. **Enabling production after a separate change lands is one line**,
`production: "<loki-sha-12>"` in that map, plus lifting the self-test's refusal.

- **Transport: direct OTLP push from the sidecar.** Where Loki is declared, the sidecar gets
  `Observability__LokiOtlpEndpoint=http://<loki private domain>:3100/otlp/v1/logs`. That is the same exporter
  both local wirings use, and it is fail-open in `Program.cs`. The alternatives were rejected:
  - a Railway log drain would ship **every** service's stdout, OpenEMR's included, which is a separate change
    unsolved PHI problem;
  - a shipper service adds a component that only relays what the sidecar can already send.
- **Private.** No domain, and no auth of its own (`auth_enabled: false`), exactly like Prometheus.
  - `loki-data`: 1024 MB in `sfo`, mounted at `/loki`.
  - Retention: `168h`, from `loki/loki-config.yaml`, enforced by the compactor.
  - The image runs as root because a Railway volume mounts root-owned.
- **Absent means invisible, not red.** Without Loki:
  - the sidecar gets no endpoint, so it registers no exporter and logs nothing about it;
  - `/ready` does not probe Loki anywhere;
  - Grafana gets `LOKI_URL=""`, so its entrypoint deletes the Loki datasource and loads the dashboard
    variant built without the *Per-encounter story* panel. That is 23 non-row panels instead of 24, and
    none of them errors.
  - **This needs a Grafana image built from a commit carrying this entrypoint.** Every `grafana-sha`
    published, including the hand-built `4768e5d79880` staging ran, still shows the Loki datasource,
    with an empty URL, and the erroring panel. Production can only take a `develop` build
    staging ran, and every one since a separate change carries it. `iac:selftest` still refuses the known
    earlier tags wherever Loki is absent.
- **Published by `publish-observability-images`** as `loki-sha-<12>`, from `develop` pushes,
  like the other two. **Nothing deploys it yet**: the maintainer ruled on 2026-09-24 that Loki stays off
  on staging until a separate change lands, so `LOKI_IMAGE_BY_ENV.staging` is `""`. Setting it to `TRACKS_DEVELOP`
  switches it on, from each `develop` push's own build; `iac:selftest` pins the off state until then.

### Tempo on staging, and why not in production

**Staging only, by the same disposition as Loki**, applied rather than decided twice.
`TEMPO_IMAGE_BY_ENV` in `.railway/railway.ts` has a `staging` key and **no `production` key**, and
`npm run iac:selftest` refuses one. Production's reason is not Loki's: spans are scrubbed before export, so
they do not carry the identifiers production's logs do. Production still gets none, independently of whether
its metrics tier is filled (it now is) - enabling a trace store there is a separate, maintainer's
call: one line in that map plus lifting the refusal.

- **Transport: direct OTLP/HTTP push from the sidecar** to `http://<tempo private domain>:4318/v1/traces`,
  the exporter both local wirings use. Grafana reads it at `TEMPO_URL=http://<tempo private domain>:3200`.
- **Private.** No domain and no auth of its own. `tempo-data`: 1024 MB in `sfo`, mounted at `/var/tempo`;
  retention `168h` from `tempo/tempo.yaml`; the image runs as root for Loki's root-owned-volume reason.
- **Read through the front door.** Traces open in Grafana **Explore** at `/grafana`, the route Grafana is
  already served on. Tempo gets no proxy route and no dashboard panel, so no panel count changes.
- **Absent means invisible.** No endpoint on the sidecar, so no trace exporter; `/ready` never probes Tempo;
  Grafana gets `TEMPO_URL=""` and its entrypoint deletes the Tempo datasource. A Grafana image built before
  a separate change has no Tempo datasource to delete, so unlike Loki there is no stale-image hazard.
- **Published by `publish-observability-images`** as `tempo-sha-<12>`, from `develop` pushes,
  and verified there with `tempo -config.verify=true`. **Staging runs that push's build** — its entry is
  `TRACKS_DEVELOP`, so the first `develop` apply after a separate change creates Tempo there.

## Alerts

`alerts/agentforge-alerts.yml` - **9 rules in two groups** (`FR-OBS-4` requires ≥3; `NFR-SLO-W2-1` names
three specific ones). Every rule carries a `summary` and a `description` combining **meaning** (what firing
means) and **on-call response** (what to check first, in order). This compose file
runs Prometheus's rule *evaluation* only - wiring a real Alertmanager (Slack/PagerDuty/email
routing) is a deployment-specific choice left to whoever stands this up for real, not hard-coded
here.

**`AgentForgeHighTurnLatencyP95` evaluates one turn type, not all of them.** Its series is filtered to
`agentforge_agent_turn_duration_seconds_bucket{turn_type="brief"}`, because `NFR-PERF-1`'s 26s budget is
stated for a single-patient `RequestBrief` turn (UC-1) alone. Daily Agenda per-patient summaries
(`turn_type="agenda"`, one turn per rostered patient) and in-session follow-ups (`turn_type="follow_up"`)
are excluded - unfiltered they outnumber briefs and hold the p95 down. **Two consequences when you read
this rule.** It is silent on the other two turn types, so an agenda fan-out can be slow without anything
firing: check Grafana panel 3, which plots all three. And its `for: 15m` is longer than every other rule's
here on purpose - the filtered series sits on the 25.8s baseline, where one slow brief lingers in the `[5m]`
rate window long enough to satisfy a shorter `for:` by itself. The histogram's explicit buckets
(`AgentForgeMetrics.AgentTurnDurationBucketBoundariesSeconds`, applied by an `AddView` in `Program.cs`) put
**26 on a boundary**, which is what lets the quantile resolve the threshold at all. Changing one side
without the other blunts the rule silently, so the two are pinned together: `AlertRuleThresholdTests` reads
this file, parses the threshold out of the rule's own `expr`, and asserts it is one of those boundaries -
editing `> 26` here and editing the boundary list both redden the unit suite. A separate change

| Group | Rule | Fires when | Response action lives |
|---|---|---|---|
| `agentforge-slo` | `AgentForgeHighTurnLatencyP95` | **brief**-turn p95 > 26 s for 15m (filtered and re-debounced; see below) | in the rule's `description` |
| | `AgentForgeHighTurnErrorRate` | >5% of turns fail for 5m | " |
| | `AgentForgeHighToolFailureRate` | >10% of tool calls fail for 5m | " |
| | `AgentForgeElevatedVerificationFailureRate` | >20% of drafts fail verification over 15m | " |
| | `AgentForgeRetrievalDegradation` | any stage degraded (failed, span `outcome=degraded`) over 10m | " |
| `agentforge-week2` | `AgentForgeHighExtractionFailureRate` | >20% of the ingestion attempts **that reached the extractor** are schema-rejected over 30m, with ≥5 attempts, for 10m — **all three numbers chosen for shape, none measured** | " |
| | `AgentForgeHighEvidenceRetrievalLatencyP95` | evidence-retrieval p95 > 6 s for 10m — `NFR-SLO-W2-1`'s number, derived in [`METRICS.md`](../METRICS.md) §*Week 2 SLO targets*; 6 is a bucket boundary since a separate change | " |
| | `AgentForgeEvalCategoryRegression` | a rubric category is >5 points below its baseline — a copy of `evals/baseline.json`'s `max_regression`, pinned equal to it by CI | " |
| | `AgentForgeEvalGateFailed` | the running image's own eval run was blocked by the gate (`agentforge_eval_run_passed == 0`) — every failure mode, including the safety-floor, orphan and population failures the rule above cannot see | " |

**The response action is in the rule, not here, and that is deliberate:** what a responder sees when a rule
fires is the alert's own annotations, so a procedure kept one file away is a procedure nobody reads. This
table routes; the rules carry the content.

**What to know before trusting a silent — or a firing — Week 2 rule.** Each is stated in full inside the rule
it belongs to; this is the index.

- **`AgentForgeEvalCategoryRegression` reads the run the running image was built with**. The
  `Dockerfile`'s `evals` stage runs the eval gate against the image's own source and bakes
  `evals/results.json` + `baseline.json` into it; the sidecar publishes them from its own `/metrics` as
  `agentforge_eval_category_pass_rate{category}`, `agentforge_eval_baseline_pass_rate{category}`,
  `agentforge_eval_run_timestamp_seconds` and `agentforge_eval_run_passed` (the gate's verdict, 1/0). **Two rules
  read them, and it takes both:** the category rule sees a >5-point drop in one category, and
  **`AgentForgeEvalGateFailed`** sees the verdict - so it fires on the gate failures a per-category drop cannot
  show: a safety rubric one case under its 100% floor (about a 1-point drop), an orphaned baseline row, a
  failed population check. `agentforge-alerts-tests.yml` asserts that exact case: verdict rule firing,
  category rule silent. No new service and nothing new exposed: Prometheus already
  scrapes that endpoint wherever it runs, so the rule can fire **wherever Prometheus scrapes a sidecar built
  from this tree** - staging always, since it has no sidecar pin to name one: every `develop` push's
  own build is exactly that, applied unattended - and production too since its
  observability pin was filled and hand-applied 2026-09-24. **Staleness:** the values live exactly as long as the process; a stopped sidecar's
  series go stale at the next failed scrape and the alert resolves, and a category the build's run did not
  evaluate has no rate (its baseline is still published) rather than a frozen one. **Silence can mean "no
  run"**: a host-run sidecar before `dotnet run --project tests/AgentForge.Evals -- evals`, or an image built
  some other way, publishes nothing and logs a warning naming the missing file. **It cannot see a model
  version moving under a pinned build** - the gate is hermetic, and so is the run baked in.
  **Its relationship to the CI gate** is the other half of the same point: the gate is the stronger control
  wherever a red pipeline stops a merge. The alert is for a regression that reaches a deployed environment by another route. That means a
  hand-edited image pin or an operator apply (not a model version moving under a merged build - above).
  **Until 2026-09-22 that relationship was overstated in both directions**: the gate's own `>5 %` arm was
  unreachable behind a `pass_threshold` of `1.0`, so this rule was the only statement of the policy anywhere
  that could be exercised at all — and it could only be exercised under `promtool`. A separate change lowered the
  quality rubrics' floor to `0.80`, the gate arm now fires there and has been observed firing, and the two
  are genuinely two controls. A separate change was therefore a **follow-up** rather than a prerequisite:
  the rule is no longer the only implementation, it is the runtime half of one that already works at merge
  time. **For the five safety rubrics it is still the only `>5 %` statement** - their gate floor is `1.0`,
  so the gate's arm is unreachable for them by ruling and this rule is what a runtime drop would meet.
- **`AgentForgeHighEvidenceRetrievalLatencyP95`'s `> 6` is a bucket boundary, so a firing reads as a
  number.** Since a separate change both Week 2 histograms have an `AddView` in `Program.cs` with explicit boundaries
  (`AgentForgeMetrics.EvidenceRetrievalDurationBucketBoundariesSeconds` and
  `DocumentIngestionDurationBucketBoundariesSeconds`): one every second from 5 to 15 s, with **6** and **11**
  on a boundary, where OTel .NET's defaults left 6 s inside a 5-10 s bucket and 11 s inside a 10-25 s one.
  The expression crosses 6 exactly when more than 5 % of the window's retrievals took longer than 6 s (the
  rule then waits out its `for: 10m`). It is pinned the
  way a separate change pinned the turn rule: `AlertRuleThresholdTests` parses `> 6` out of this file and requires it
  to be one of the boundaries, and `Week2HistogramExportTests` boots the host, scrapes `/metrics` and
  requires the exported `le` set to be exactly the declared one, so a renamed instrument or a mistyped view
  is red rather than a silent return to the defaults.
- **`AgentForgeHighEvidenceRetrievalLatencyP95` covers both retrieval call sites, summed.** Since a separate change the
  histogram is recorded by `EvidenceAgentSupervisor` (`POST /evidence/ask`, `entry_point="evidence_ask"`) and by
  `EvidenceTool` (the chat `retrieve_evidence` tool, `entry_point="chat_tool"`). The rule and the p95 panel sum
  over `entry_point`, so they measure the stage; split by `entry_point` to see which path is slow, because a
  busy path can hold the stage p95 down while the quieter one is slow.
- **`AgentForgeHighExtractionFailureRate` now sees an extractor outage — closed.** The ingestion
  counter used to be written only where `IngestAsync` returned, so a VLM (or store) throw recorded nothing at
  all and an outage moved the ratio not at all. `DocumentIngestionService` now records `outcome="error"` on
  both throwing paths before rethrowing, and the rule's ratio counts `error` alongside `extraction_rejected`
  in both numerator and denominator — a dead extractor now crosses the same 20 %/≥5-attempt/`for: 10m`
  threshold a dead schema gate would, instead of staying invisible to it. The *Document Ingestion Rate by
  Outcome* panel going quiet is still the faster tell; the rule's `description` says to check both.
- **`AgentForgeHighExtractionFailureRate`'s 20 % is not a measured SLO**, nor is its ≥ 5 attempt floor or its
  30 m window - nothing on file measures a steady-state rejection rate, so all three are shape. The rule says
  so in its `description`, where a responder reads, not only in a YAML comment Prometheus never carries.
- **`AgentForgeEvalCategoryRegression`'s `0.05` is a second copy** of `evals/baseline.json`'s
  `max_regression`, because nothing in Prometheus can read that file. CI fails the build when the two differ.

### Testing the rules

Both halves of an alerting bug matter - a threshold nobody can reach, and one anybody can - and it takes
two files to hold them. `alerts/agentforge-alerts-tests.yml` feeds each Week 2 rule a synthetic series that
must fire it **and a near-miss that must not**; `alerts/alert-rules-gate.sh` is what stops the first half
being asserted against a stale copy, and separately pins every rule's `for:` duration (in either group)
against a table it carries itself - `for:` is invisible to both the synthetic series and the near-miss,
so widening it in the rules file alone used to silence an alert with nothing here noticing.
Run everything CI runs, with the same pinned Prometheus:

```bash
docker run --rm -v "$PWD:/work" -w /work --entrypoint sh prom/prometheus:v3.15.0 -c '
  sh observability/alerts/alert-rules-gate-selftest.sh
  sh observability/alerts/alert-rules-gate.sh
'
```

That is exactly what `alerts/alert-rules-gate.sh` does, and the
gate's own header describes its six checks one by one. **The thing to understand about this suite:** promtool
compares a firing alert's annotations for exact equality and these descriptions are paragraphs, so the
*firing* assertions read a **copy** of each rule's expression while only the silent ones read the real rule -
and the silent ones constrain a threshold from **below only**. On a separate change all three Week 2 thresholds were
raised out of reach and `promtool test rules` still printed `SUCCESS`. Check 4 of the gate is what closes
that: every `agentforge-week2` rule's `expr` must be, character for character once whitespace is collapsed,
an expression a `promql_expr_test` asserts on. Edit a threshold in one file and the gate is red on the
mismatch; edit it in both and promtool is red, because the synthetic series no longer satisfies it. The
self-test mutates throwaway copies through both directions and ends `SELF-TEST PASSED - N of N assertions`,
so a suite that never reached its cases cannot read as a pass.

## Readiness

`GET /ready` on the sidecar checks OpenEMR FHIR, the LLM provider, (if
`Observability__PrometheusHealthUrl` is configured) this Prometheus instance, and the vector index
(`VectorIndexHealthCheck`). `ObservabilityHealthCheck` reports `Degraded` rather than an
unconditional pass when that URL isn't set, as the vector-index check does when no
`AgentForgeData__ConnectionString` is set.

**Every one of those four probes is bounded** by `Readiness__ProbeTimeout` (2s by default), not by
`HttpClient`'s 100-second default. Without that bound, a Prometheus that accepts the connection and never
answers holds `/ready` open for ~100s on staging - long enough that any load balancer or uptime check has
already called the sidecar down. The bound changes when readiness answers, never what it answers.

**Who sets it, per wiring.** The overlay (mode **B**) sets `http://prometheus:9090/-/healthy` for you;
`.railway/railway.ts` sets the private-network equivalent, but only in an environment that actually declares
Prometheus. **This file - the separate stack, mode A - sets nothing**, because it does not run the sidecar:
give the host-run sidecar `Observability__PrometheusHealthUrl=http://localhost:9090/-/healthy` yourself, which
works because the container below publishes 9090 on loopback.

**Unset is not free, and not a 503 either.** `Degraded` is served as **HTTP 200**, so an uptime check reading
only the status code cannot tell "never contacted" from a full pass - read the body, which is JSON naming
every check and its status, so it says which one degraded.
Deliberate: this stack is optional infra, and a sidecar that serves clinicians perfectly well should not leave
rotation because a dashboard is down. The decision and its residual risk are
[`ARCHITECTURE.md`](../ARCHITECTURE.md) **D17**. A separate change

**Setting it, on the other hand, is a commitment.** For this check, `Degraded` covers *unconfigured* only
(the LLM-provider check has its own `Degraded`, a 429; `ARCHITECTURE.md` D17). Once the URL is
set, a Prometheus that is unreachable, silent or answering with an error status makes `/ready` **503
`Unhealthy`**, exactly as OpenEMR does - `NFR-REL-2` and `NFR-HEALTH-1` both name the observability backend
among the dependencies readiness fails for, and a separate change proposed relaxing that and was ruled against. So the
better-configured environment is the one that CAN fail readiness on this dependency: an environment with no
observability tier declared answers 200 unconditionally, while one that sets `Observability__PrometheusHealthUrl`
would answer 503 if its Prometheus became unreachable, silent or erroring. `.railway/railway.ts` sets that
variable automatically wherever `OBSERVABILITY_IMAGES` is filled, so **both** deployed environments set it now —
staging, production since its 2026-09-24 promotion. **Probed live on 2026-09-24: both
read 200 `Healthy`**, with `/ready`'s body naming `observability: Healthy, "Prometheus reachable."` — re-probe
rather than trust this line as time passes. Point the URL at a Prometheus you expect to answer, or leave it
unset. A separate change
