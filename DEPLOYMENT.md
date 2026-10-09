# Deployment and operations

How AgentForge and the OpenEMR it plugs into are built, configured, started, checked and rolled back.
Everything runs as Docker containers: the repository-root [docker-compose.yml](docker-compose.yml) is the
reference stack, and [.railway/railway.ts](.railway/railway.ts) describes the same stack for Railway as
infrastructure as code. The hosted environments are built and deployed by GitHub Actions in the supplier's
repository; those workflows are not part of this delivery, so every step below is a command an operator runs,
and §9 says what the automation does in each hosted environment so you can reproduce it on your own CI.

**Where it runs today.** Staging is live at <https://staging-agent-forge.marqspec.com>, on synthetic data
(§9 *The hosted environments*). Production is **not yet deployed in the new Railway project**; it follows in a
later delivery.

The architecture behind these containers is in [ARCHITECTURE.md](ARCHITECTURE.md) (core copilot) and
[ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md) (document pipeline and data tier); the OpenEMR
interface is in [INTERFACES.md](INTERFACES.md).

## 1. What runs

| Service (compose name) | Image | Role | Port |
|---|---|---|---|
| `reverse-proxy` | built from [reverse-proxy/](reverse-proxy/) (nginx) | **The front door.** The one origin everything enters through: OpenEMR at `/`, the sidecar under `/agentforge`. It also owns the network namespace the sidecar runs in (§2). | `${DEMO_PORT:-8080}`, published |
| `openemr` | pulled, a forked OpenEMR image pinned as `openemr-sha-1b5c2d659fb9` | OpenEMR with the `oe-module-agentforge` module and the fork's SMART-launch patches baked in. Stock `openemr/openemr` does not produce a working launch. | 80, internal |
| `mysql` | `mysql:9.4` | OpenEMR's database. | 3306, internal |
| `agent-forge-api` (profile `copilot`) | built from the root [Dockerfile](Dockerfile) | The .NET 10 sidecar (backend-for-frontend, agent, document pipeline). Runs inside `reverse-proxy`'s network namespace, so other containers reach it at `reverse-proxy:${SIDECAR_PORT:-8081}`. | `${SIDECAR_PORT:-8081}`, internal |
| `postgres` (profile `copilot`) | `pgvector/pgvector:pg17` | The sidecar's data tier: guideline corpus, derived facts, vector index. Must be pgvector, not stock Postgres. | 5432, internal |

**Two tiers.** `docker compose up -d` starts OpenEMR, its module and the front door and needs no secrets.
`docker compose --profile copilot up -d` adds the sidecar and Postgres, which need an LLM API key (Anthropic
by default, or Gemini) and a registered SMART client. The sidecar's `Llm:ApiKey` is required and validated at start, so it cannot boot
without a key; the profile split keeps the keyless tier from crash-looping.

**Only the proxy publishes a port.** OpenEMR, the sidecar and both databases are reachable only on the compose
network. That shape is what the system expects anywhere it is deployed: one front door, everything else private.

**The OpenEMR image is pulled, not built.** It comes from a separate fork of OpenEMR whose core patches (the
launch-bridge cookie, the authorization controller) the SMART launch depends on. Building it is a 10-minute PHP
build and belongs to that fork; this repository only pins the image. The pin lives in **two** files,
`docker-compose.yml` and `.railway/railway.ts`, and they must name the same tag, or local and deployed stacks
run different OpenEMR builds. Always pin an `openemr-sha-<12>` tag (it names the fork commit it was built from),
never a moving `-latest` tag. To read what a running container actually holds:
`docker inspect --format '{{index .RepoDigests 0}}' <container>`.

### Local development

```bash
cp .env.example .env            # set ANTHROPIC_API_KEY; client id/secret come from §4
docker compose up -d            # OpenEMR + module + front door
docker compose logs -f openemr  # wait for "Setup Complete!" / "Starting apache!" on first boot
docker compose --profile copilot up -d
```

Open `http://localhost:8080` (OpenEMR) and `http://localhost:8080/agentforge/health`.

- **Default file vs dev overlay.** `docker-compose.yml` names a fixed sidecar build, so a pull is reproducible.
  [docker-compose.dev.yml](docker-compose.dev.yml) is an overlay that only switches the sidecar to a moving
  `agent-forge-latest` tag, for following the newest build:
  `docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile copilot up -d`.
- **Running your own build.** `docker compose --profile copilot up -d --build` builds the sidecar from source
  **and tags it with whatever `SIDECAR_IMAGE` names**, which by default looks like a published tag. Set
  `SIDECAR_IMAGE=agent-forge-api:local` (and likewise `PROXY_IMAGE`, `OPENEMR_IMAGE`) in `.env` so a local build
  never wears a published name. Never push a local build.
- **Running the sidecar on the host** with `dotnet run --project src/AgentForge.Api` is a supported
  development loop, but it is not the reference stack (§2 explains why the container matters).
- **Bootstrap overlay.** [docker-compose.bootstrap.yml](docker-compose.bootstrap.yml) publishes MySQL on
  `127.0.0.1:${MYSQL_HOST_PORT:-3306}` for the first-run bootstrap (§4) and changes nothing else.

### The optional observability stack

Prometheus, Loki, Tempo and Grafana are not in the base file. Which wiring you use depends on where the
sidecar runs:

| Sidecar runs | Use | Prometheus scrapes |
|---|---|---|
| in a container | the overlay: `docker compose -f docker-compose.yml -f docker-compose.observability.yml --profile copilot up -d` | `reverse-proxy:${SIDECAR_PORT}` |
| on the host | the separate project [observability/docker-compose.yml](observability/) | `host.docker.internal:5113` |

The separate project lands on its own network and cannot reach a containerized sidecar; run it against one
and every panel stays blank. The overlay builds `observability/prometheus/Dockerfile` (not stock
`prom/prometheus`) because its entrypoint renders the scrape target from `SIDECAR_PORT`; a static config would
keep scraping the old port after `SIDECAR_PORT` moves. The overlay also sets the three sidecar variables the
base file leaves unset (`Observability__LokiOtlpEndpoint`, `Observability__TraceOtlpEndpoint`,
`Observability__PrometheusHealthUrl`). Both wirings publish their ports on `127.0.0.1` only and take Grafana's
admin login from `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` (default `admin`/`admin`, acceptable only on
loopback). The overlay reads the root `.env`; the separate project reads `observability/.env`. Details in
[observability/README.md](observability/README.md).

### The security platform's container

[security-platform/Dockerfile](security-platform/Dockerfile) builds a small Python image (context:
`security-platform/`). Its default command, `serve`, answers `GET /health` on `${PORT:-8080}` with an idle
status and sends nothing; `POST /run` does not exist, so a run cannot be started over HTTP. It holds no
database credential and no model API key, and it is not a compose service. A replay is run explicitly:

```bash
SECURITY_PLATFORM_TARGET_URL=https://copilot-staging.example.org/ \
SECURITY_PLATFORM_CASES_FILE=/path/to/cases.json \
SECURITY_PLATFORM_SPEND_CEILING_USD=1 \
SECURITY_PLATFORM_USD_PER_REQUEST=0.001 \
PYTHONPATH=security-platform python -m security_platform run
```

The committed `security-platform/allowlist.json` names the only front doors it may target; edit it to your own
non-production front door. `SECURITY_PLATFORM_ALLOWLIST` may only narrow it. A ceiling of `0` sends nothing and
exits 3; a case that would pass the ceiling is not started. Results may go to `SECURITY_PLATFORM_RESULTS`; the
trace is one JSON object per line on stdout, and response bodies are not stored. Never point it at a production
deployment. See [SECURITY-PLATFORM.md](SECURITY-PLATFORM.md).

## 2. The one-origin invariant

Everything is reached through one published origin (`http://localhost:8080` by default): OpenEMR at `/`, the
sidecar under `/agentforge`. Two mechanisms break if that is violated:

1. **Launch cookie.** A launch whose `/launch` and `/callback` land on different hosts loses its pending-launch
   cookie and fails with *"No pending SMART launch"*.
2. **OAuth `aud`.** OpenEMR's Site Address Override (`site_addr_oath`) must equal the sidecar's
   `OpenEmr__BaseUrl`. The sidecar builds the `aud` and the authorize URL from `BaseUrl`; OpenEMR validates
   `aud` against `site_addr_oath`. Any drift and every launch fails *"Aud parameter did not match authorized
   server"* before a login form appears.

**The rule:** in every environment, `OpenEmr__BaseUrl` equals OpenEMR's `site_addr_oath`, and both are the
front door, never a container's own hostname. Each OAuth client is registered with its own flow's redirect URI:
`/agentforge/callback` for the patient launch, `/agentforge/agenda/callback` for the roster (agenda) launch.

**Why the sidecar has no network of its own on compose.** `OpenEmr__BaseUrl` has two consumers: the browser
follows the authorize URL built from it, and the sidecar uses it as the base address for the token exchange,
introspection and every FHIR read. Over plain HTTP only a loopback origin keeps OpenEMR's OAuth session cookie,
so the value must be `http://localhost:8080`, and inside an ordinary container `localhost` is the container
itself. So `agent-forge-api` declares `network_mode: "service:reverse-proxy"`: inside the proxy's namespace
`localhost:${DEMO_PORT}` is nginx, and one written origin means the same machine on both sides. Kestrel moves to
`${SIDECAR_PORT:-8081}` and the proxy reaches it at `127.0.0.1:${SIDECAR_PORT}`. (Without this, the token
exchange posts to the sidecar itself, gets a 404, and the callback fails with a `Refit.ApiException` after a
successful login.)

Consequences:

- **`DEMO_PORT` must not equal `SIDECAR_PORT`.** Both bind in the same namespace. The proxy image refuses to
  start on that collision (`reverse-proxy/15-assert-sidecar-port.sh`), naming both settings.
- **`SIDECAR_PORT` has three consumers:** Kestrel's `PORT`, the proxy's `SIDECAR_UPSTREAM`, and the Prometheus
  scrape target under the observability overlay. All three derive from the one variable.
- **The sidecar's lifetime is tied to the proxy's.** Recreating the proxy alone strands the sidecar (§7).
- The published/internal boundary is unchanged: `${SIDECAR_PORT}` is not published, and the proxy answers 404
  for `/agentforge/documents/` (the private ingestion path, W2-D17: ingestion authenticates by trusted
  private-network origin rather than a token, so it must never be reachable through the front door).

None of this applies to Railway, where the front door is a public domain that resolves the same inside and
outside a container and `agent-forge-api` is an ordinary separate service (§9).

## 3. Sidecar configuration

Options pattern; `__` separates sections. The compose file sets most of this; rows marked **not in compose**
are the exceptions.

| Variable | Value and meaning |
|---|---|
| `OpenEmr__BaseUrl` | The front door, e.g. `http://localhost:8080`. Must equal OpenEMR's `site_addr_oath` (§2), and must resolve to the front door from inside the container too. |
| `OpenEmr__Site` | `default` |
| `Bff__PathBase` | `/agentforge`: the path the proxy serves the sidecar under. Drives the path base, the session-cookie path and the post-launch redirect prefix. |
| `Bff__PublicBaseUrl` | Front door plus path base, e.g. `http://localhost:8080/agentforge`. Never a container hostname; that breaks the OAuth `redirect_uri`. |
| `DataProtection__KeyRingPath` | `/keys`, on a volume. The pending-launch cookie and the session cookie are encrypted with this key ring; an in-memory ring cannot decrypt them after a restart ("No pending SMART launch"). |
| `OpenEmr__ClientId` / `OpenEmr__ClientSecret` | The patient-launch confidential SMART client, enabled by an admin (redirect `/agentforge/callback`). From `.env` as `OPENEMR_CLIENT_ID` / `OPENEMR_CLIENT_SECRET`. |
| `OpenEmr__Scopes__0..15` | FHIR scopes. Casing matters (`patient/encounter.read` is rejected). The list is 0-based and must be contiguous. Index 15, `patient/Appointment.read`, is required for authorization, not data: the FR-AUTH-2 relationship check (a clinician may open only a patient on their own schedule for the clinic day) reads the day's appointments on every launch, and without the scope every per-patient launch is refused 403. |
| `OpenEmrAgenda__ClientId` / `OpenEmrAgenda__ClientSecret` | **Not in compose.** The roster/agenda client (redirect `/agentforge/agenda/callback`). |
| `OpenEmrAgenda__Scopes__0..5` | **Not in compose.** `openid`, `fhirUser`, `launch`, `api:fhir`, `user/Appointment.read`, `user/Patient.read`. |
| `Llm__Provider` | `Anthropic` (default) or `Gemini` (`LLM_PROVIDER` in `.env`). Any other value stops the sidecar booting. Gemini's free tier may use prompts and responses to improve Google's products, so it is acceptable only with synthetic demo data, never with real PHI and never in production. |
| `Llm__ApiKey` | The key for that provider, from the environment only (`ANTHROPIC_API_KEY` in `.env`, whichever provider). Required. Gemini's key is sent only as the `x-goog-api-key` header. `/ready` checks it with one token-free model lookup on the configured provider, cached for `Readiness__ResultCacheTtl`; a wrong key or unknown model is a 503. |
| `Llm__Model` | `claude-sonnet-5` on Anthropic; on Gemini, a model id from Google's models page, without the `models/` prefix |
| `Llm__InputPricePerMillionTokensUsd` / `Llm__OutputPricePerMillionTokensUsd` | Defaults `2.00` / `10.00`, the model's list price, so the cost metric reports real spend; `0` / `0` on a free tier. Change them whenever the provider or model changes, in `docker-compose.yml`, `.railway/railway.ts` and `.env.example` together; nothing validates the pair. |
| `AgentForgeData__ConnectionString` | Postgres/pgvector. Optional: the document and evidence flows are additive and the host boots without it. **Setting it is a commitment**: it enables the startup migrations (§5) and makes the vector index a readiness dependency, so an unreachable store, or one missing the `vector` extension, the `guideline_chunks` table or its HNSW index, makes `/ready` 503. Unset, `/ready` reports the vector index `Degraded` (HTTP 200). |
| `DataStoreStartup__InitialRetryDelay` / `__MaxRetryDelay` | **Not in compose.** Optional, `00:00:01` / `00:00:30`. Backoff for the startup migration and seed retries; no attempt limit. Invalid values refuse to boot. |
| `Observability__LokiOtlpEndpoint` | **Not in compose; set by the observability overlay.** OTLP/HTTP log push, fail-open (unset means console logging only). |
| `Observability__TraceOtlpEndpoint` | **Not in compose; set by the overlay.** Full OTLP/HTTP traces URL. Unset means no trace exporter; an invalid or unreachable value drops spans and changes nothing else. Every span is scrubbed of identifiers before export. |
| `Observability__TraceConsoleExporter` | Optional, `false`. `true` prints spans to stdout for local debugging. Leave it off anywhere stdout is retained. |
| `Observability__PrometheusHealthUrl` | **Not in compose; set by the overlay** (and by `.railway/railway.ts` only where Prometheus is declared). Unset, `/ready` reports observability `Degraded` (HTTP 200). Set, an unreachable Prometheus makes `/ready` 503. Point it only at a Prometheus you expect to answer. This is decision D17: an optional dependency that was never configured degrades readiness; one that was configured and fails takes the sidecar out of rotation. NFR-HEALTH-1 names the dependencies `/ready` must check. |
| `Readiness__ProbeTimeout` | Optional, `00:00:02`. Per-dependency budget for a `/ready` probe, so a hung dependency answers fast instead of after `HttpClient`'s 100-second default. |
| `Readiness__ResultCacheTtl` | Optional, `00:00:30`. How long `/ready` reuses the answers for the LLM provider, OpenEMR's SMART discovery and Cohere (`/ready` is public). An outage or recovery shows up to one TTL plus two probe durations late (about 34 s by default). Must be positive. |
| `ConversationBudget__MaxTurnsPerWindow` / `__Window` | Optional, `800` / `12:00:00`. The per-session LLM budget (below). |
| `Agenda__SummaryCacheTtl` / `Agenda__MaxCachedSummaries` | Optional, `00:30:00` / `1000`. In-memory cache of the Daily Agenda's per-patient summaries, keyed by site, clinician, patient, appointment and day. A reload within the TTL re-serves a summary with no LLM call; each row shows `summaryAsOf`. It is a TTL, not invalidation, so a summary can lag the chart by up to the TTL. The TTL must be positive and at most 12 hours. |
| `EvalResults__ResultsPath` / `__BaselinePath` | Optional; default `evals/results.json` / `evals/baseline.json`, which the image's build writes (§5). Published as `agentforge_eval_*` metrics. A blank results path turns them off. |
| `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` | Not sidecar config: the `.env` inputs for Grafana's admin login in both observability wirings. |

**The Daily Agenda is not wired on compose.** `docker-compose.yml` passes no `OpenEmrAgenda__*`, and the agenda
options have no fallback to the patient client. They are validated lazily, so the sidecar boots and the
per-patient flow works, but the first roster launch throws `OptionsValidationException`. To use the agenda on
compose, add the `OpenEmrAgenda__*` variables to the `agent-forge-api` service yourself. The Railway definition
does pass them.

### Edge rate limits and the per-session LLM budget

Two limits, one per layer, each covering what the other cannot see.

**At the front door, per client**, in one block at the top of
[reverse-proxy/nginx.conf.template](reverse-proxy/nginx.conf.template):

| Route (case-insensitive) | Sustained | Burst | Over the limit |
|---|---|---|---|
| `/agentforge/hubs/chat*` (SignalR negotiate, upgrade, SSE sends) | 60/min | 30 | 429 |
| `/agentforge/evidence/*` (evidence ask, source-document fetch) | 20/min | 10 | 429 |
| `/agentforge/agenda` (the roster) | 10/min | 5 | 429 |
| `/agentforge/ready` | 30/min | 10 | 429 |
| `/agentforge/documents/*` | — | — | always 404 |
| `/agentforge/metrics`, any dotfile path except `.well-known` | — | — | always 404 |

Everything else, including SMART launches and all of OpenEMR, is unlimited. The client is keyed on `X-Real-IP`
only when the TCP peer is inside `REAL_IP_TRUSTED_CIDR` (a proxy environment variable, default
`100.64.0.0/10`, Railway's edge range); otherwise on the peer address. Set it to your own edge's range on
another host. One clinic behind one NAT shares every bucket; evidence binds first. A busy multi-clinician site
needs higher limits, and that block is where to raise them.

**Post-deploy check: does your edge overwrite `X-Real-IP`?** From outside, send 12 `/ready` requests, each with
a different header:

```sh
for i in $(seq 1 12); do
  curl -s -o /dev/null -w '%{http_code}\n' -H "X-Real-IP: 203.0.113.$i" "https://<front door>/agentforge/ready"
done
```

If the 12th answers 429, the edge overwrites the header and the limit holds. If all 12 pass, a client can
rotate the header to get fresh buckets; find what identifies the client reliably on your edge.

**In the sidecar, per session:** every LLM-bearing unit a session starts (each chat turn, each evidence ask,
each agenda summary not served from the cache) is charged against `ConversationBudget__MaxTurnsPerWindow`
within `ConversationBudget__Window` of the first charge. Past the cap, a chat turn gets a fixed refusal message,
an evidence ask a 429, and an agenda row is listed as failed with the reason. Counts are in memory and reset on
restart. The default of 800 per 12 hours is sized from a 25-patient clinic day (about 345 units with the summary
cache), leaving more than half spare while still capping a runaway or hostile session.

**Local-development escape hatches.** `OpenEmr__AllowInsecureHttpForLocalDevelopment` and
`Bff__AllowInsecureHttpForLocalDevelopment` allow plain HTTP. They exist for the local stack and are not safe
anywhere else.

**The sidecar refuses to start on a bad dependency-injection composition, in every environment.** The log
reads `AggregateException: Some services are not able to be constructed`, naming the service. That failure is
identical on every restart of the same image, so the remedy is rolling back (§6), not setting a variable. The
other way the image refuses to boot is a missing `Llm:ApiKey`.

## 4. First-run bootstrap

A fresh stack is a bare OpenEMR plus the module. The settings below are **state in OpenEMR's database**, not
configuration, so they must be redone on any fresh stack or after a volume reset. Two idempotent tools do it.

**APIs first.** OpenEMR ships with the REST, FHIR and system-scope APIs off. Both stack definitions here turn
them on with `OPENEMR_SETTING_rest_api`, `OPENEMR_SETTING_rest_fhir_api` and
`OPENEMR_SETTING_rest_system_scopes_api` on the `openemr` service, which the image applies on every boot. On a
stack built some other way, set those variables or run:

```sql
UPDATE globals SET gl_value='1' WHERE gl_name IN ('rest_api','rest_fhir_api','rest_system_scopes_api');
```

`OPENEMR_SETTING_*` only updates globals that already have a row, so it cannot set the module's own
`agentforge_*` globals; the bootstrap tool does.

**The order is APIs, then register, then bootstrap.** Registration needs the APIs on, and the bootstrap tool
refuses to run before the clients exist.

```bash
docker compose -f docker-compose.yml -f docker-compose.bootstrap.yml up -d   # exposes MySQL on 127.0.0.1

# 1. register both SMART clients; prints their ids and secrets
dotnet run --project tools/RegisterSmartClients -- http://localhost:8080

# put the patient client's id/secret in .env as OPENEMR_CLIENT_ID / OPENEMR_CLIENT_SECRET, then:

# 2. write the globals and enable the clients
MYSQL_ROOT_PASSWORD=rootpass dotnet run --project tools/BootstrapOpenEmr -- http://localhost:8080

docker compose --profile copilot up -d
```

**Read what step 1 prints to stderr.** OpenEMR silently drops any requested scope the client is not registered
for. The tool compares what it asked for with what came back and names every dropped scope. A dropped
`patient/Binary.read` breaks click-to-source (the overlay shows a misleading 404); a dropped
`patient/Appointment.read` makes every per-patient launch 403. A dropped scope means the launch will not work.

**What the tools write (steps 1 to 3):**

1. **Site Address Override** (`site_addr_oath`) = the front door, and the global
   `oauth_ehr_launch_authorization_flow_skip`. An empty `site_addr_oath` makes every OAuth URL a bare path,
   which breaks app registration ("Failed to fetch") and JWT client authentication.
2. **The OAuth clients.** `RegisterSmartClients` registers both against the front door with a deliberate
   **superset** of the scopes a launch requests (the lists live in
   `AgentForge.Integration.OpenEmr.SmartLaunchScopes`). New clients land disabled; `BootstrapOpenEmr` enables
   every `AgentForge%` client, sets its skip-authorization flag, and appends any superset scope an existing
   client lacks (a client registered before a scope was added never gains it otherwise; relaunch afterwards).
   The ids and secrets are the one thing neither tool can carry: they are generated at registration and must be
   copied into the sidecar's configuration.
3. **The module's four globals:** Launch URI `<front door>/agentforge/launch`, Agenda Launch URI
   `<front door>/agentforge/agenda/launch`, Issuer `<front door>/apis/default/fhir`, Launch Mode `tab`. The two
   launches need separate URIs; `tab` mode is required because a cross-origin iframe loses the session cookie.

`BootstrapOpenEmr` talks to MySQL directly, reading `MYSQL_HOST` (default `127.0.0.1`), `MYSQL_PORT` (`3306`),
`MYSQL_DATABASE` (`openemr`), `MYSQL_USER` (`root`) and, for the password, `MYSQL_ROOT_PASS` /
`MYSQL_ROOT_PASSWORD` for root or `MYSQL_PASS` / `MYSQL_PASSWORD` otherwise. It waits for OpenEMR's schema,
prints `ok`, `SET` or `ENABLED` per setting and a `Done: N changed` line, and refuses a service-internal
hostname as the front door. A converged stack is all `ok` and `Done: 0 changed`. If `3306` is taken, set
`MYSQL_HOST_PORT` for the overlay and the same value as `MYSQL_PORT` for the tool.

**On Railway** the database is on a private network a workstation cannot reach. Either add a temporary TCP
proxy to the `mysql` service, point the tool at it, and **delete the proxy afterwards** (it is a public route to
the database); or do the two halves by hand: the globals on the module's config page and Admin → Config →
Connectors, then on each client's page under Admin → System → API Clients press **Enable Client** and **Disable
EHR Launch Authorization Flow** (that label is correct: the sidecar needs the flow skipped). Set the global skip
flag first, or the per-client control does not appear. The checkboxes on that page are read-only and change
nothing. Type `site_addr_oath` with care; it is where §2's invariant usually breaks.

**Why the client is confidential.** Decision D11 calls for a public client (no client secret held anywhere in
the browser). Against this OpenEMR fork an empty-secret client still fails introspection in the real launch
sequence, so the deployed client is confidential, with a real secret held by the sidecar only.

**Step 4: demo data (manual).** A fresh stack has only the `admin` login; there is no demo clinician and no demo
patient. Seed the synthetic cardiology cohort with the fork's `seed_cardiology_demo.php`, inside the `openemr`
container. It writes patients `AF-DEMO-01` to `AF-DEMO-07` with problems, allergies, prescriptions, two
back-dated encounters with vitals, a coded lab panel and one same-day appointment each.

- **Create the provider first**, in Admin → Users: authorized, with calendar access. The script resolves
  `--provider=<username>` to the user id it writes on each appointment, which is what the Daily Agenda filters
  on. With no such user the seed aborts; with the wrong one the roster stays empty.
- **Run it as the web user with an explicit timezone**, because appointment times are computed from "now":

  ```bash
  docker compose exec openemr sh
  cd /var/www/localhost/htdocs/openemr
  SEED=interface/modules/custom_modules/oe-module-agentforge/scripts/seed_cardiology_demo.php
  su -s /bin/sh apache -c "php -d date.timezone=America/Chicago $SEED --provider=<username> --dry-run"
  # then again without --dry-run
  ```

  Set OpenEMR's own `gbl_time_zone` to match. Running as `root` is refused.
- **Expect** duplicate-key errors from OpenEMR's vitals listener on every encounter; they are noise and the seed
  continues. Seeded appointments show on the calendar only when you view that provider's column and when they
  fall inside `schedule_start`–`schedule_end` (default 8–17).
- **Verify by row count, not exit code** (a validation failure produces an empty chart, not a crash). On a stack
  holding only this cohort, before §4a: 7 patients (`pubpid LIKE 'AF-DEMO-%'`), 20 `lists` rows, 22
  `prescriptions`, 14 `form_encounter`, 14 `form_vitals`, 28 `procedure_result`, 7 calendar events.

[tools/SeedDemoPatients](tools/SeedDemoPatients/) is a different tool: it creates 20 patients with
demographics only, over the API, for volume in the patient list. A brief over one of them is empty by design.

**Step 5: assert the result.**

```bash
bash scripts/post-deploy-verify.sh http://localhost:8080
MYSQL_ROOT_PASSWORD=rootpass bash scripts/post-deploy-verify.sh http://localhost:8080 --globals
```

The first form checks the front door: `/` redirects to login, `/agentforge/health` 200,
`/apis/default/fhir/metadata` 200 (fails when the API toggles are off) and `/agentforge/ready` 200, naming any
check that is not `Healthy` (`Degraded` passes and is named). `--globals` re-runs `BootstrapOpenEmr` and fails
unless it reports `Done: 0 changed`; it repairs whatever it finds, so it is not read-only. Without `--globals`
the script prints `globals NOT CHECKED`.

**Document ingestion needs one more setting per environment.** The module's background service (every 2
minutes) forwards new patient documents to the sidecar's private `POST /documents/ingest` and silently does
nothing unless two settings resolve. Save them on **Modules → Manage Modules → Agent Forge Copilot → config**:

| Setting | Value |
|---|---|
| Ingest URI (`agentforge_ingest_uri`) | The sidecar's **private** address: `http://agent-forge-api.railway.internal:8080/documents/ingest` on Railway, `http://reverse-proxy:8081/documents/ingest` on compose. Never the front door, which 404s the path (W2-D17). |
| Ingest category map (`agentforge_ingest_category_map`) | `{"2":"lab_pdf"}`: category 2 is the stock install's `Lab Report`. There is no intake-form category; intake forms go through the evidence page. |

The environment variables `AGENTFORGE_INGEST_URI` / `AGENTFORGE_INGEST_CATEGORY_MAP` (declared in
`.railway/railway.ts`) do **not** reach the spawned background job under this image, and the config page can
display their value while the job cannot see it. Only a **saved** global works. To verify: upload a synthetic
lab PDF (for example `tests/fixtures/documents/lab-inr-warfarin-multipage.pdf`) to a demo patient's Documents →
Lab Report, wait one interval, look in the **sidecar's** log for `Document ingested: N derived fact(s)
persisted`, then ask the copilot about the value and look for a *Source document* citation. The first run after
the setting is correct forwards every document already on file in a mapped category, each one a model
extraction call.

### 4a. Keeping a demo instance non-empty: the appointment window

The cohort seed gives each patient one appointment, dated the day of seeding, and the Daily Agenda lists only
**future** appointments on the **current** date. A demo is therefore empty by the next morning, and the agenda
empties during the day. The fork's `seed_demo_appointments.php` books a forward window onto patients that
already exist. It never creates patients or touches charts, and it is idempotent on (patient, provider, date):
a re-run fills missing days and does not reshape existing ones.

```bash
cd /var/www/localhost/htdocs/openemr
SEED=interface/modules/custom_modules/oe-module-agentforge/scripts/seed_demo_appointments.php
su -s /bin/sh apache -c "php $SEED --provider=<username> --days=90 --per-day=6 --start-hour=8 --end-hour=16 --dry-run"
# then again without --dry-run
```

That books 08:00, 09:30, 11:00, 12:30, 14:00 and 15:30 every day for 90 days, rotating through the cohort.
Without `--end-hour` you get a back-to-back morning block, and the agenda is empty from late morning.

- **Check that your image supports the spread options without running the script.** PHP's `getopt()` silently
  discards unknown long options, and the script has no `--help` (`--help` seeds with the defaults). Check
  instead: `docker compose exec openemr grep -c -- '--end-hour' .../scripts/seed_demo_appointments.php`.
  Non-zero means supported; the pinned image supports them.
- **A wrong run is not undone by a right one** (existing days are skipped). Delete the cohort's future
  `openemr_postcalendar_events` rows for the affected dates, then re-run.
- **Appointments must be created by this script**, not by hand-written SQL or recurring events: the product
  reads them over FHIR, which returns only stored rows that carry a `uuid`, and the fork exposes no FHIR create
  for appointments.
- **Cost.** Each rostered appointment is one agent turn (several LLM calls) every time its summary is generated,
  on first load and again after `Agenda__SummaryCacheTtl`. `--per-day` is a density decision.
- **Timezone caveat.** The agenda derives "today" from UTC while appointment dates are local clinic time, so in
  US time zones the roster rolls to the next day in the early evening.

Verify with the calendar and the agenda; useful counts are events with a non-null `uuid`, distinct future
dates, and per-day `COUNT(*)`, `MIN(pc_startTime)`, `MAX(pc_startTime)` (a spread seed ends at 15:30).

## 5. Images and versions

Every deployed component is a container image. Build each one once per version, tag it, push it, and deploy
those exact tags everywhere; never rebuild for a different environment. Images carry no secrets; configuration
comes from the environment (§3).

**Tag convention.** One registry repository holds every component, and the tag says which component it is:
`agent-forge-`, `proxy-`, `prometheus-`, `grafana-`, `loki-`, `tempo-`, `security-platform-`, `openemr-`.
Tag each build twice: with the release version (`agent-forge-1.0.0`) and with the 12-character commit it was
built from (`agent-forge-sha-<12>`). The sha tag is what `.railway/railway.ts` and its self-test expect (§9).
Treat both as immutable: never push a different build over an existing tag, and do not rely on moving `-latest`
tags for anything that matters.

**The published builds.** The supplier's GitHub Actions build every commit on the development line and publish
it to the public GitHub Container Registry package **`ghcr.io/marqspec/agent-forge`**, one tag per component
(`agent-forge-sha-<12>`, `proxy-sha-<12>`, `prometheus-sha-<12>`, `grafana-sha-<12>`, `loki-sha-<12>`,
`tempo-sha-<12>`, `security-platform-sha-<12>`), plus the moving `agent-forge-latest` and `proxy-latest` tags,
which nothing that matters should follow. The package
is public, so Railway and `docker pull` fetch it without registry credentials. Earlier builds, including the
ones production's pins and `docker-compose.yml`'s defaults still name, are on Docker Hub
(`docker.io/amarquette/gauntletai`); they move to GHCR only when production is promoted to a build staging ran.
OpenEMR is built in its fork and stays on Docker Hub. To own your images, copy the tags you run into your own
registry (below) and point `REGISTRY` and the compose defaults at it.

**Before building a release**, run the gates from the repository root:

```bash
dotnet build AgentForge.slnx
dotnet format --verify-no-changes
dotnet test tests/AgentForge.UnitTests
dotnet test tests/AgentForge.EvalTests
npm ci && npm run iac:typecheck && npm run iac:selftest   # only if you deploy with Railway
```

**Build, tag and push:**

```bash
VERSION=1.0.0
REPO=registry.example.org/agentforge          # your registry repository
SHA=$(git rev-parse --short=12 HEAD)
tags() { echo "-t $REPO:$1-$VERSION -t $REPO:$1-sha-$SHA"; }

docker build -f Dockerfile $(tags agent-forge) .
docker build -f reverse-proxy/Dockerfile $(tags proxy) reverse-proxy
for c in prometheus grafana loki tempo; do
  docker build -f observability/$c/Dockerfile $(tags $c) .
done
docker build -f security-platform/Dockerfile $(tags security-platform) security-platform

for c in agent-forge proxy prometheus grafana loki tempo security-platform; do
  docker push "$REPO:$c-$VERSION"; docker push "$REPO:$c-sha-$SHA"
done
```

Build contexts matter: the proxy and the security platform build from their own directories, everything else
from the repository root. Loki and Tempo are needed only if you run them.

**The sidecar image runs the evaluation suite during its build.** The `evals` stage of the Dockerfile runs the
deterministic eval gate against the image's own source and copies `evals/results.json` and the baseline into
the image, which the sidecar then publishes as `agentforge_eval_*` metrics. A failing eval gate (exit 1) does
not fail the build: the image carries the failed run, and the `AgentForgeEvalGateFailed` alert fires wherever
it runs. Broken eval assets (exit 2) do fail the build. Read the build log before shipping.

**OpenEMR.** This repository does not build OpenEMR. To own your copy of the pinned image, pull the tag the
compose file names and push it to your repository under the same `openemr-sha-<12>` tag. To move to a new fork
build, change the tag in both `docker-compose.yml` and `.railway/railway.ts` in one change.

**Database migrations.** The sidecar owns one database, Postgres (OpenEMR sets up its own MySQL schema on first
boot). EF Core migrations in [src/AgentForge.Data/Migrations](src/AgentForge.Data/Migrations/) are the only
schema-change mechanism (decision W2-D14), and **the sidecar applies them itself at startup**; there is no
separate migration step. When `AgentForgeData__ConnectionString` is set, a background service starts once the
host is listening, runs EF Core's `MigrateAsync` (creating the `vector` extension and tables as needed), then
seeds the guideline corpus. Both steps retry with backoff until Postgres answers. While migrations are pending
or failing, `/health` is 200 and `/ready` is 503 naming `vector-index`, and the log carries one `Data store
startup migrations attempt N failed` warning per attempt; `/ready` turns 200 without a restart once they
complete. Migrations only move forward; nothing migrates down (see §6). The data project carries a design-time
factory for the EF Core tools, reading the same connection-string variable, if you want to inspect the SQL a
version will apply before deploying it.

**Deploying a version.**

- **Compose:** put the version's tags in `.env` (`SIDECAR_IMAGE=$REPO:agent-forge-1.0.0`,
  `PROXY_IMAGE=$REPO:proxy-1.0.0`, `OPENEMR_IMAGE=$REPO:openemr-sha-<12>`, and `PROMETHEUS_IMAGE` if you run the
  overlay), then `docker compose --profile copilot pull && docker compose --profile copilot up -d`.
- **Railway:** pin the version's `-sha-<12>` tags in `.railway/railway.ts` and apply (§9).
- **`scripts/deploy.sh <environment> <version>`** is a placeholder for your own hosting: as shipped it only
  prints a notice that no deployment target is configured. Replace its body with your platform's command.

**Smoke check after every deploy.**

```bash
bash scripts/post-deploy-verify.sh https://copilot.example.org --wait 120
# optional: --globals (needs MySQL reachable), --grafana (where Grafana runs: proves admin/admin is refused)

PRODUCTION_URL=https://copilot.example.org HEALTH_PATH=/agentforge/health bash scripts/smoke-check.sh production
```

`post-deploy-verify.sh` is the full front-door check from §4 step 5; `--wait` retries until the system answers,
and it refuses a service-internal hostname. `smoke-check.sh <staging|production>` is a simpler probe: it reads
`STAGING_URL` or `PRODUCTION_URL`, requires a 2xx on `HEALTH_PATH` (default `/`) within about a minute, and
passes with a notice if the URL variable is unset. A red result after a deploy means roll back (§6).

## 6. Rolling forward and back

A rollback is **deploying the previous version's image tags again**, not a rebuild. Keep a record of which tags
each environment ran.

```bash
# compose: point .env (or the shell) at the previous version, then recreate
SIDECAR_IMAGE=$REPO:agent-forge-0.9.0 PROXY_IMAGE=$REPO:proxy-0.9.0 \
  docker compose --profile copilot up -d
```

On Railway, set production's entries in `.railway/railway.ts` back to the previous `-sha-<12>` tags and run the
full plan, guard, apply and verify sequence of §9. A rollback plan should contain image rows only; anything else
in it is a reason to stop.

**What a rollback does not undo:**

- **Schema migrations.** The older image finds newer migrations already applied and applies none of its own.
  Additive changes are harmless to it; a dropped or renamed column or table breaks it. **Read the failed
  version's migrations before rolling back.** If one is not additive, an image rollback is not enough: restore
  the Postgres volume from the snapshot taken before the deploy (§10) together with the older image.
- **Data written in between** (ingested documents, derived facts) stays.
- **Other configuration changes** the version brought (variables, services, volumes). Revert them explicitly.
- **OpenEMR's live state** (§4): OAuth clients, globals and module settings are not versioned with the image.
  If the breakage is configuration, fix the configuration; rolling the image back will not help.

A volume restore, rather than an image rollback, is the remedy when data or schema is what broke: a destructive
apply, a lost volume, or a non-additive migration. See §10. The `openemr` and `mysql` containers are independent
of the sidecar, so a bad sidecar build never risks OpenEMR's data.

**Detecting the need to roll back.** Watch `/agentforge/health` (the process is up) and `/agentforge/ready`
(real dependency checks). Read the JSON body, which names every check and its status:

- `Degraded` (HTTP 200) on `observability` or `vector-index` means that dependency was never configured. Not a
  rollback signal.
- `Degraded` on `llm-provider` means the provider answered 429: a throttle on the account's key. Rolling back
  will not clear it.
- A configured dependency that does not answer is a 503. The cached checks (`openemr`, `llm-provider`,
  `reranker`) can lag a recovery by about 34 s.

## 7. Known quirks

- **`SWARM_MODE=yes` is required on `openemr`.** Named volumes mount empty; the image restores its `sites/`
  skeleton and runs first-boot setup only in swarm mode as the leader. Without it the container crash-loops on a
  missing `sites/default/sqlconf.php`. Never run two `openemr` containers on one volume.
- **OpenEMR's first boot takes several minutes**, and the container reports healthy before setup finishes. Wait
  for *"Setup Complete!"* / *"Starting apache!"* in its log.
- **MySQL 9.4** runs clean. If authentication-plugin problems ever appear, pin MySQL 8.4 and set up on a fresh
  volume.
- **`/agentforge/*` answers 502 until the `copilot` profile is up.** The proxy resolves upstreams at request
  time, so it starts without the sidecar.
- **Recreating the proxy alone strands the sidecar.** The sidecar lives in the proxy's network namespace. After
  `docker compose up -d --force-recreate reverse-proxy`, a proxy pull, or an `.env` change touching only the
  proxy, the sidecar still shows `Up` but has only a loopback interface, and `/agentforge/*` 502s. Recreate the
  pair: `docker compose --profile copilot up -d`. (`docker compose down` removes both and is safe.)
- **Demo patient ids change on reseed.** Anything holding a FHIR patient id must be refreshed after a reseed or
  volume reset.
- **Anonymous registry pulls may be rate-limited per source IP** (Docker Hub does this). The failure is a
  `toomanyrequests` / 429 that looks like a missing tag and clears by itself. Authenticate the puller or use
  your own registry.
- **A local build can wear a published name** (§1). Nothing re-pulls a tag you already hold, so the stack keeps
  running your build. `docker image rm` the tag, or use `:local` names.
- **Spans never carry exception details.** On .NET 10 the runtime's HTTP activity adds an `exception` event to a
  failed outbound call regardless of instrumentation options; `SpanPhiScrubber` removes those events. If one ever
  appears in Tempo, the scrubber is not registered ahead of the exporter.
- **`tempo -config.verify` needs a value**: use `-config.verify=true`, or it prints the usage text as if the
  config were wrong.
- **Railway quirks** are in §9.

## 8. Production: the capability contract

Everything above is a synthetic-data development posture: plain HTTP, local escape hatches on, throwaway
credentials. What makes it acceptable is that no real patient data ever enters it, not its controls. A
production deployment runs the **same images** in a **compliance-capable environment you supply** (a
HIPAA-eligible cloud account, a managed platform under a BAA, or your own datacentre), with TLS at the edge,
secrets from a real secret store, the escape hatches off, and §2's invariant pointed at the real front door.

What that environment must provide is a set of capabilities, not a vendor. Each row names the seam in this
code it binds to.

| The environment must provide | Why | Seam |
|---|---|---|
| **Encryption at rest** for the derived-fact store | Citations hold verbatim text from clinical documents; derived facts hold extracted clinical values | [AgentForgeDbContext](src/AgentForge.Data/AgentForgeDbContext.cs) and its Postgres volume. Encryption is the platform's (encrypted volume, managed-database encryption, KMS-backed disk), not the application's. |
| **Key material outside the data volume** | Keys on the same filesystem as the data they protect defeat the point | The sidecar's DataProtection key ring: the `/keys` volume locally, a platform key store or secret manager in production. |
| **An encrypted session store with a TTL, or session affinity** | Conversation history is the densest patient data the system holds | [IConversationStateStore](src/AgentForge.Api/Session/IConversationStateStore.cs) (two methods, one registration), or the load balancer's affinity. The dev stack has neither: state is in-process, so run one sidecar replica until one is supplied. |
| **A BAA-covered inference endpoint** | Every agent turn discloses patient data to the model provider; embedding and rerank calls likewise | [ILlmProvider](src/AgentForge.Llm/ILlmProvider.cs): a hosted model under an executed BAA, or an in-environment model behind the same interface. |
| **Audit retention on your schedule, in a sink separate from the application log** | FR-AUTH-4: every patient-data access is recorded with the patient it touched; HIPAA expects audit controls and six-year retention | [AccessAuditLog](src/AgentForge.Mcp/AccessAuditLog.cs), category `AgentForge.AccessAudit`, which is filtered out of the OpenTelemetry pipeline and written to stdout only. Route that stream to access-controlled audit storage; stdout alone does not meet this row. |
| **Network isolation** between the sidecar and anything public | The sidecar is reachable only through the front door, and ingestion authenticates by trusted private-network origin (W2-D17) | The topology: [docker-compose.yml](docker-compose.yml), [nginx.conf.template](reverse-proxy/nginx.conf.template), [.railway/railway.ts](.railway/railway.ts), or your network policy. |
| **No patient data in diagnostic telemetry, the audit trail excepted** | NFR-SEC-1: logs, metrics and traces must be free of patient identifiers | Diagnostic loggers carry no patient id; spans are scrubbed before export; metrics carry no identifiers. Keep the audit sink access-controlled and the diagnostic sink identifier-free. |
| **Recoverability: a stated RPO per stateful store and an exercised restore** | A backup nobody has restored from is not a recovery plan | Four stateful volumes: the `/keys` key ring (losing it ends every active session and pending launch); `mysql-data` (OpenEMR, including encrypted blobs); `openemr-sites` (uploaded source documents **and** OpenEMR's encryption keys for `mysql-data`, so the two must be backed up together); `postgres-data` (derived facts and guideline index, re-derivable by re-running ingestion only while the source documents exist). Observability volumes hold history, not patient data. |

Also in production: the sidecar is single-replica unless the session-store or affinity row is met; Prometheus
must scrape each sidecar replica directly, not through the proxy; and Prometheus, Loki and Tempo have no
authentication of their own, so only Grafana, behind its login, may be exposed. A worked cloud example and its
costs are in [ARCHITECTURE.md](ARCHITECTURE.md).

## 9. Railway infrastructure as code

[.railway/railway.ts](.railway/railway.ts) is the whole Railway project in source: the services
(`reverse-proxy`, `openemr`, `mysql`, `agent-forge-api`, `postgres`, and optionally `prometheus`, `grafana`,
`loki`, `tempo`, `security-platform`), their volumes, and every non-secret variable. Only `reverse-proxy` gets a
public domain. `OpenEmr__BaseUrl` and `Bff__PublicBaseUrl` are derived from
`${{reverse-proxy.RAILWAY_PUBLIC_DOMAIN}}`, so §2's invariant follows the domain; `site_addr_oath` is database
state and still has to be set by the §4 bootstrap. The deprecated `railway.json` / `railway.toml` format is not
used.

### The hosted environments

The hosted system is one Railway project with two environments, `staging` and `production`, both declared by
`.railway/railway.ts`. It is a new project: the hosts named in earlier documents and in
`security-platform/allowlist.json` belong to the project it replaced.

| Environment | Status | Images |
|---|---|---|
| `staging` | **Live** at <https://staging-agent-forge.marqspec.com>. `/agentforge/health`, `/agentforge/ready` and the front-door check pass; the synthetic cardiology demo cohort (§4 step 4) is seeded. | The GHCR build of the commit it deploys (`tracks-develop`, below) |
| `production` | **Not yet deployed in the new project.** It follows in a later delivery, after staging has run green and production's pins are decided. | Literal pins, today the earlier Docker Hub builds |

**How the supplier's automation drives them** (GitHub Actions only; the workflows are not delivered). Each
Railway environment has its own Railway project token, and each token is held as a secret of a GitHub
environment, so a job receives a token only by declaring that environment:

| GitHub environment | Holds | Used for |
|---|---|---|
| `Staging` | the staging token | applying staging on every push to the development line |
| `Production` | the production token, behind required reviewers | applying production, only on a merge into the main line, after a reviewer approves |
| `Production-plan` | the same production token, no reviewer | planning production for a pull request that changes `.railway/`, and the scheduled drift check |

Every apply, in either environment, runs the sequence in *Plan and apply* below: plan, destructive guard,
snapshot, apply, **apply result check**, deploy-identity, stays-up, front-door check. Staging is fixed forward:
a red check after an apply leaves staging on the build it tested, and the fix is the next change. If you run
your own CI, keep the same split: one token per environment, a reviewer in front of production applies, and
never the production token in a job that a development-line push starts.

### Bootstrapping a fresh Railway project

`railway.ts` creates services, volumes and variables, but a brand-new project needs a few things it cannot
express. This is the order the current project was brought up in; on another fresh project, do the same:

1. **Make the image package public**, or give each image service Registry Credentials (a paid Railway plan
   feature that the file cannot declare). A new GHCR package is private on its first push, and Railway pulls
   anonymously.
2. **Remove service records a failed apply left behind.** If an earlier apply failed part-way, the project can
   hold services with no instance in any environment; delete them before the first real apply.
3. **Set the environment's shared variables** (*Secrets* below) **before** the first apply: `MYSQL_ROOT_PASSWORD`,
   `MYSQL_PASSWORD`, `POSTGRES_PASSWORD`, `COHERE_API_KEY`, `GRAFANA_ADMIN_USER`, `GRAFANA_ADMIN_PASSWORD`.
   Grafana accepts `admin/admin` without the last two.
4. **Apply** (*Plan and apply*). The apply result check names any change Railway rejected; fix it and apply
   again. Two limits a fresh project meets: volumes cannot exceed the Railway plan's size cap (5000 MB on the
   current plan; `npm run iac:selftest` holds every declared volume under it), and every volume must be in
   `REGION`, where Railway places the services.
5. **Set the service-scoped secrets** once the services exist: `Llm__ApiKey` on `agent-forge-api` (it
   crash-loops without one) and `OE_PASS` on `openemr` (without it the first boot installs the image's default
   admin password; change it afterwards).
6. **Generate the front door's domain** (*The manual steps after an apply*, step 1), then make a new
   deployment of `agent-forge-api` and `grafana` so they resolve it.
7. **Run §4's bootstrap** against that front door: `tools/RegisterSmartClients`, read its scope report, set the
   four OAuth values on `agent-forge-api`, then `tools/BootstrapOpenEmr`, and deploy the sidecar again.
8. **Seed** the demo data if the environment is a demo (§4 step 4, §4a).
9. **Verify:** `/agentforge/health` and `/agentforge/ready` answer 200 at the front door, and
   `bash scripts/post-deploy-verify.sh <front door> --grafana` passes.

### Adapting the definition to your project

The file knows two environment names, `production` and `staging`, and resolves images per environment:

- **Registry.** `REGISTRY` and `OPENEMR_IMAGE` name the image repository. Point them at yours.
- **Production images** are literal pins in `SIDECAR_IMAGE_BY_ENV`, `PROXY_IMAGE_BY_ENV` and
  `OBSERVABILITY_IMAGES` (Prometheus and Grafana). Set them to the version's `-sha-<12>` tags. The self-test
  refuses an empty or moving production entry.
- **Staging images** are the marker `tracks-develop`, which resolves to `<component>-sha-<12>` of the commit
  named by the `STAGING_BUILD_SHA` environment variable. A staging plan throws without it. Set it to the
  12- or 40-character commit of the version you built.
- **Unknown environments throw** for the sidecar and proxy rather than borrowing another environment's image.
  To add an environment, add its entries.
- **Sites volume.** Every environment mounts the declared `openemr-sites` volume (2048 MB, in `REGION`).
- **Optional services.** Loki, Tempo and the security platform are declared only where their maps
  (`LOKI_IMAGE_BY_ENV`, `TEMPO_IMAGE_BY_ENV`, `SECURITY_PLATFORM_IMAGE_BY_ENV`) have an entry; none has a
  production entry, and the self-test refuses one.
- **Region.** `REGION` is `us-east4-eqdc4a`, where Railway places the project's services; volumes cannot change region in place.

`npm run iac:selftest` (`.railway/sites-volume-selftest.mjs`) renders the graph in memory, with no token and no
network, and checks the conventions above: per-environment volumes, no two environments sharing a volume, every
dependent variable present exactly when its service is (for example `Observability__PrometheusHealthUrl` only
where Prometheus exists), every service an image, and production pinned. If you change a convention, change the
self-test with it.

### Secrets

Set these once per environment **before the first apply**, or the data services will not initialise and the
sidecar will crash-loop. They are Railway variable names, not the compose `.env` names.

| Environment-level shared variable | Read by |
|---|---|
| `MYSQL_ROOT_PASSWORD` | `mysql` and `openemr` (`MYSQL_ROOT_PASS`) |
| `MYSQL_PASSWORD` | `mysql` and `openemr` (`MYSQL_PASS`) |
| `POSTGRES_PASSWORD` | `postgres` and the sidecar's connection string |
| `COHERE_API_KEY` | the sidecar (`Cohere__ApiKey`). Unset degrades silently to sparse-only retrieval, with `/ready` reporting `reranker: Degraded`. |
| `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` | `grafana`, wherever it runs. Set these **before** the apply that creates Grafana: it is reachable at `<front door>/grafana` from the moment it exists, and unset it accepts `admin/admin`. Prefer a user name other than `admin`. Read the resolved values back off the service. |

Shared variables are used where two services must hold the same value. Service-scoped secrets are declared
`preserve()` ("keep what is set in Railway"): `OE_PASS` on `openemr`; `Llm__ApiKey`, `OpenEmr__ClientId`,
`OpenEmr__ClientSecret`, `OpenEmrAgenda__ClientId`, `OpenEmrAgenda__ClientSecret` on `agent-forge-api`.
`preserve()` supplies nothing on a first apply; set them by hand. The four OAuth values exist only after §4's
registration.

### Plan and apply

```bash
npm ci                                   # installs the Railway SDK the file is evaluated with
npm run iac:typecheck && npm run iac:selftest
railway login && railway link
export RAILWAY_TOKEN=<project token for the target environment>
export STAGING_BUILD_SHA=<commit>        # staging only

npm run iac:plan -- --out railway-plan.json 2>&1 | tee railway-plan.txt    # preview; changes nothing
bash scripts/railway-destructive-guard.sh railway-plan.json railway-plan.txt
# STOP unless exit 0 (10 destructive, 11 undecidable)

bash scripts/railway-deploy-identity.sh run --state railway-deploy-identity.json --plan railway-plan.json \
  -- bash scripts/railway-apply-result.sh run --out railway-apply-result.json \
     -- bash scripts/railway-snapshot-guard.sh run --plan railway-plan.json \
          -- railway config apply --plan railway-plan.json --yes --json

bash scripts/railway-stays-up.sh --state railway-deploy-identity.json --plan railway-plan.json
bash scripts/post-deploy-verify.sh https://<front door> --wait 120
```

What each step does:

- **The plan** is computed client-side by the Railway CLI from the evaluated file and the live environment
  config. `--out` pins it, so the apply executes exactly what you read. Read `jq -r .cliVersion
  railway-plan.json`: different CLI versions can produce different plans from the same inputs. The hosted
  environments install the CLI pinned at 5.63.4 (`npm install -g @railway/cli@5.63.4`); pin yours the same way.
- **[railway-destructive-guard.sh](scripts/railway-destructive-guard.sh)** reads the plan and exits 0 only
  when it proves nothing is removed. Exit 10 means a resource or variable would be deleted; 11 means it could
  not decide (including a plan that changes a service's build config, which redeploys the service while Railway
  calls the row safe). With `--require-clean` it also returns 12 for pending but safe changes, which makes it a
  drift check. A deletion you intend is decided outside this sequence: bring the live state into the file, or
  remove it by hand after a verified snapshot (§10).
- **[railway-deploy-identity.sh](scripts/railway-deploy-identity.sh)** records every service's deployment id,
  runs the command, and fails (exit 10) if the environment produced no new deployment, so an apply that reports
  success but ships nothing is caught. It must wrap the snapshot guard, not the other way round, because the
  snapshot guard refuses a command line carrying two `--plan` arguments.
- **[railway-apply-result.sh](scripts/railway-apply-result.sh)** is the apply result check. `railway
  config apply --plan` prints "Applied pinned Railway configuration." and exits 0 for every result except a
  no-op, including `failed` and `partially_applied`, so its message and exit code cannot be trusted. The
  script runs the apply with `--json` and passes only when Railway answers `applied` with no failed change (or
  `noop` with no changes); anything else exits 12 (11 when the output cannot be read), printing each change's
  kind, path, status and Railway's diagnostics. It sits inside deploy-identity, so a rejected apply is reported
  as itself rather than as "nothing moved", and outside the snapshot guard, which must see exactly one
  `--plan`. `railway-apply-result.sh check <file>` judges a saved result.
- **[railway-snapshot-guard.sh](scripts/railway-snapshot-guard.sh)** takes and verifies a volume backup
  before the apply unless the plan is provably harmless (§10).
- **[railway-stays-up.sh](scripts/railway-stays-up.sh)** watches every service the apply moved for about 60
  seconds (status, stopped flag, instance status, crash markers in the log) and exits 10 on a crash loop, 11 when
  it cannot tell. Railway can show a crash-looping deployment as `SUCCESS` for over an hour.
- **post-deploy-verify.sh** then checks the front door, including `/ready`.

These scripts need `jq` and `curl`, and run under Git Bash on Windows (each sources
[scripts/lib/jq-crlf.sh](scripts/lib/jq-crlf.sh) to handle `jq`'s CRLF output there). The one exception is
the plan line: on Windows run it from PowerShell with `$env:_` set to the native `railway.exe`, because the SDK
re-runs the CLI through `process.env._` and otherwise fails with a misleading "requires Railway CLI x or newer"
message.

**Tokens.** A project token is scoped to one environment, is read from `RAILWAY_TOKEN` and authenticates with
`Project-Access-Token`; a personal or team token is read from `RAILWAY_API_TOKEN` and uses
`Authorization: Bearer`. A stale `RAILWAY_API_TOKEN` in your environment shadows a logged-in session and makes
every command fail `Unauthorized`; unset it for the command (`env -u RAILWAY_API_TOKEN railway ...`) rather
than logging in again.

### The manual steps after an apply

The file cannot express everything:

1. **Generate the front door's domain** (first apply only): `reverse-proxy` → Settings → Networking →
   Generate Domain. Railway does not create one automatically and the IaC cannot declare a generated domain.
   Until then the derived base URLs are empty and no launch works. Then make `agent-forge-api` deploy again so it
   resolves the reference. **Not with `railway redeploy`**, which replays the previous deployment's captured
   configuration, reports `SUCCESS` and changes nothing; use a variable change or a re-apply, and confirm the
   deployment id moved. Grafana needs no domain; it is served at `/grafana` through the proxy. The generated
   domain is a live edit the drift check cannot see; declaring `domains: ["<host>"]` on the proxy instead
   brings it under source control at the cost of owning DNS.
2. **Run §4's bootstrap** against the new front door, copy the client ids and secrets into the `preserve()`
   variables, and save the ingestion settings on the module's config page (§4). All of it is OpenEMR database
   state.
3. **Verify** with `post-deploy-verify.sh --wait 120`, adding `--grafana` where Grafana runs and `--globals`
   when MySQL is reachable.

### Operating it

- **Drift.** `npm run iac:drift` (`railway config plan --detailed-exit-code`) exits 2 when the live environment
  no longer matches the file; run it on a schedule. A clean drift run speaks only about the resources the plan
  compared.
- **Read the running environment back** rather than trusting the file. For example, the scope list:
  `railway variables -e production -s agent-forge-api | grep -oE 'OpenEmr__Scopes__[0-9]+' | sed 's/.*__//' |
  sort -n | paste -sd' '` should read `0 1 2 … 15`. A missing trailing index looks exactly like a shorter list.
  A scope present in both file and environment can still be dropped at launch if the registered client lacks
  it; the sidecar logs a warning naming it.
- **`mysql`'s image tag is not compared by the plan.** Its variables and volume are; its `source.image` is not.
  After changing that tag, confirm it against the live environment config.
- **Platform facts the file already handles.** The proxy derives its DNS resolver from `/etc/resolv.conf`
  (Docker's `127.0.0.11` does not exist on Railway) and resolves with IPv6 off (`RESOLVER_IPV6`), because the
  upstreams listen on IPv4 only. Postgres sets `PGDATA=/var/lib/postgresql/data/pgdata`, a subdirectory,
  because a Railway volume's `lost+found` makes the mount root non-empty and `initdb` refuses it (the symptom is
  a sidecar `Npgsql` connect timeout and `/ready` 503 on `vector-index`, while Railway shows Postgres online).
  Prometheus runs with `RAILWAY_RUN_UID=0` because volumes mount root-owned and the image runs as `nobody`;
  check `USER` before giving any new service a volume. Grafana's `LOKI_URL` and `TEMPO_URL` are emptied rather
  than removed where those services are absent, because removing a variable plans as a destructive row.
- **Volumes vs volume instances.** A volume is project-level; a volume instance is its per-environment copy and
  is what holds data and backups. `railway volume list --json` prints volume ids; backup calls take instance ids,
  and the wrong one answers `Not Authorized`. Names are project-scoped and can resolve in the wrong environment.
- **Prometheus storage.** Retention is `15d` or `400MB`, whichever comes first (`PROMETHEUS_RETENTION_TIME` /
  `PROMETHEUS_RETENTION_SIZE`), on a 1024 MB volume in production; the size cap stays under half the volume so a
  manual backup fits (§10).
- **Backup schedules.** [railway-backup-schedules.sh](scripts/railway-backup-schedules.sh) holds each
  environment at the schedule its declaration says (§10): `check` (0 matches, 3 drift or missing, 2 refused),
  `apply` (adds a DAILY schedule and re-reads to prove it), `remove` (takes DAILY away, only where the
  declaration says `none`).

## 10. Snapshot before a destructive operation

Railway volumes have no automatic backups, and re-running an apply recovers nothing. The snapshot guard makes a
verified backup a precondition of any operation that could destroy data.

```bash
scripts/railway-snapshot-guard.sh classify [--plan FILE] -- <railway argv...>
scripts/railway-snapshot-guard.sh snapshot [--label TEXT] [--volume NAME]
scripts/railway-snapshot-guard.sh run [--plan FILE] [--label TEXT] -- <railway argv...>
```

`run` classifies the operation, snapshots every volume instance in the target environment unless the operation
is provably harmless, verifies each snapshot, prints its backup id, and only then executes the command.
`snapshot` alone is useful before a hand edit in Railway's web console.

**What counts as destructive is enumerated:** a `config apply` whose plan marks any change `destructive`; any
verb that removes or re-points storage (`volume delete/remove/rm/detach/update`, `service` or `environment`
`delete/remove/rm`, `down`, any `restore`); and any environment-scoped command naming a project-level object
(`-v <name>`). Anything not recognised as read-only is undecidable and treated as destructive. A `config apply`
without `--plan` re-evaluates the file against the live environment, so the guard refuses when it was given a
plan and the apply names none.

**It refuses (exit 2) and runs nothing** when it cannot do its job: no credential or no `jq`; no ready volume
instance; a volume name matching zero or several instances; `RAILWAY_ENVIRONMENT_ID` disagreeing with the
token's scope; the backup failing or not appearing; a label already in use; the apply naming a different plan,
or `--plan` twice; the plan file changing between check and apply; or a volume past Railway's **50% cap on a
manual backup** with no earlier backup to be incremental against (grow the volume first).

**How it proves a backup.** Backup creation is asynchronous, and project tokens cannot read the workflow status,
so the guard waits for a backup carrying **this run's label** to appear in the volume instance's backup list.
The label must be unique per run (the default is `guard-<UTC timestamp>`). The wait is up to twice
`RAILWAY_SNAPSHOT_GUARD_TIMEOUT` (default 900 s) **per volume instance**, and instances are snapshotted one after
another.

**Data-refreshable environments.** [scripts/railway-data-refreshable.json](scripts/railway-data-refreshable.json)
lists environments by **id** with two independent answers: `refreshable` (may the snapshot guard skip its
backup, because the data can be re-seeded rather than restored) and `schedules` (`"none"` or `"daily"`, read only
by the backup-schedules script). The shipped entries describe the environments of the Railway project the current one
replaced, both synthetic and refreshable; the current project's environments are not listed. **Your environments are not listed, so the strict answers apply: a verified snapshot
is required, and a daily schedule is expected.** Any malformed or missing answer reads as strict. Add an entry
for an environment only when its data truly can be thrown away; for real patient data it cannot. A refreshable
environment still runs every other check, but nothing on the apply path then stops a destructive plan except
`railway-destructive-guard.sh`, so always run it first.

**Restoring.** Restores are per volume instance and arrive as a staged change: Railway mounts a new volume named
for the backup's date at the same path and leaves the old one unmounted; inspect it and press Deploy. A backup
restores only into the same project and environment, and **wiping a volume deletes all its backups**, which is
why the guard snapshots immediately before the operation. Restore `mysql-data` and the OpenEMR sites volume
together (the sites volume holds the keys for `mysql-data`'s encrypted fields), and restore `postgres-data` with
the image version that matches its schema (§6).

**Scheduled backups** fund Railway's 50% cap for later manual backups and protect against loss between
deploys. For any environment holding real data, set a schedule with `railway-backup-schedules.sh apply`, keep
`refreshable` false, and exercise a restore before relying on it (§8's recoverability row).
