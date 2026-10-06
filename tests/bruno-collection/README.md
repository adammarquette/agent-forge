# AgentForge Bruno collection

FR-EVAL-4: a runnable API collection covering the sidecar's core HTTP endpoints, so a grader can
exercise real workflows without reading source.

## Running it

**Bruno app (GUI):** install [Bruno](https://www.usebruno.com/), "Open Collection", point it at
this folder, select the `local` environment, run requests individually or via the collection
runner.

**Bruno CLI (scriptable, CI-friendly):**

```
npx @usebruno/cli run --env local --disable-cookies
```

Run from this directory (`tests/bruno-collection/`). Exits non-zero on any assertion failure -
directly usable as a single command producing pass/fail results (FR-EVAL-4). Without a
`sessionCookie` (see below), the Agenda/, Evidence/ and Patient/ requests correctly **fail** (401,
asserted against 200/302) rather than reporting green with no session behind them:

```
npx @usebruno/cli run --env local --disable-cookies --env-var sessionCookie=".AspNetCore.Session=<value>"
```

**`--disable-cookies` is not optional, and it is not about privacy - it is a correctness fix
.** Without it, the CLI's own cookie jar (`@usebruno/requests`, on by default) silently
overwrites every request's explicit `Cookie: {{sessionCookie}}` header with whatever `Set-Cookie` it
last captured for the same origin. `Launch/SMART Launch Redirect` mints a **fresh, unauthenticated**
`.AspNetCore.Session` on every call - and folders run alphabetically, so `Launch/` runs immediately
before `Patient/` in a full-collection run. The jar overwrites the real session with that fresh
anonymous one right before `Patient/Patient Context` sends its request, 401ing a request whose
explicit header was correct. Reproduced against a local header-echo listener: with the jar enabled,
`/patient` received the `Launch/`-minted cookie, not the one the request declared; with
`--disable-cookies`, it received exactly the declared one. This is a property of any run that
includes both `Launch/` and a session-gated folder in the same invocation, `local` and `staging`
alike - it is not staging-specific and not a server-side defect.

A session is necessary but **not sufficient** for a fully green run: `Documents/` and `Evidence/`
are the Week 2 endpoints and additionally need a database and a real LLM key - see "Week 2
endpoints" below. Everything else goes green on a session alone (`/ready` carries no assertion at
all - see "What's covered").

The `{{sessionCookie}}` variable holds the **whole `Name=Value` cookie pair**, not just the value -
each of the five session-gated requests sends it verbatim as `Cookie: {{sessionCookie}}`. Passing
only the value (no `.AspNetCore.Session=` prefix) sends a cookie literally named `.AspNetCore.Session` whose
value is missing its name - `SessionMiddleware` cannot unprotect it and treats the request as
unauthenticated, which 401s **identically to supplying no cookie at all**. This matches
`tools/LoadTestChat`'s own `LoadTest__SessionCookies` convention exactly (`tools/LoadTestChat/README.md`).

## Running it against staging

A `staging` environment points the collection at the deployed Railway QA stack instead of a bare
local sidecar - `baseUrl: https://reverse-proxy-staging-5c25.up.railway.app/agentforge`, no
secrets, same blank `sessionCookie` pattern as `local.bru`:

```
npx @usebruno/cli run --env staging --disable-cookies
npx @usebruno/cli run --env staging --disable-cookies --env-var sessionCookie=".AspNetCore.Session=<value>"
```

**This is a real advantage over `local`, not just a different host**: staging already carries Week
2 configuration and a real Anthropic key (confirmed live via `/ready` - `llm-provider`,
`vector-index` and `openemr` all report `Healthy`), so none of the "Week 2 endpoints" setup below
is needed to reach it. The gap the local recipe has - no database, no real LLM key on this
workstation - **does not exist against staging**.

**Verified live against staging, 2026-09-24** (commit `6001ab07`, the `develop` HEAD this branch
forked from), with no `sessionCookie` supplied - the fail-closed case:

| Request | Result |
|---|---|
| `Health/Liveness` | 200 - pass |
| `Health/Readiness` | 200, `Healthy` on every check (no assertion in the request itself) |
| `Launch/SMART Launch Redirect` | 302 - pass |
| `Chat/Hub Negotiate` | 200 - pass |
| `Metrics/Prometheus Scrape` | 200 - pass |
| `Agenda/Daily Agenda`, `Agenda/Select Patient`, `Patient/Patient Context`, `Evidence/Ask`, `Evidence/Ask With Document` | 401 - correctly fail closed, no session |
| `Documents/Ingest` | **404**, not 401 - confirms live, from outside, what the request's own `docs` block already said from source: the reverse proxy's public front door does not route `/documents/ingest` at all (W2-D17); it is reachable only from the private network, so this request cannot go green against any public `baseUrl` no matter what credential it is given |

5 of 11 requests pass with no credential at all; this is the first time that run was against a real
deployed stack rather than a bare local process or reasoned from source (compare the "What was
verified, and what was not" section below, dated 2026-09-20, which is about `local`).

**Getting the missing credential needs a real browser** completing
`https://reverse-proxy-staging-5c25.up.railway.app/agentforge/agenda/launch` (or `/launch` for a
single-patient session) against staging's live OpenEMR, logged in as `dr_cardio` or `admin` - and
that password is **not held by an agent**: `README.md`'s "Demo access" section says plainly that
these passwords are deliberately not committed and are supplied with the submission. Nothing in
this repository or its Railway project config resolves it either - `.railway/railway.ts` sets no
`OPENEMR_ADMIN_PASSWORD` for either environment, so the account was provisioned by hand, once,
outside of source. **Only the maintainer can supply it.** Pass it the same way as `local`'s
recipe: `--env-var sessionCookie=".AspNetCore.Session=<value>"` on the CLI (alongside
`--disable-cookies`, above), or pasted into the environment's `sessionCookie` variable in the
Bruno app for that run only - **never written into `environments/staging.bru`**, which is checked
in and carries no secret.

**Or script that login.** `tools/MintSessionCookie` drives the same browser login
headlessly - the real `/agenda/launch` (or `/launch`), OpenEMR's own login and consent pages, the
real `/callback` - with a credential the operator supplies from the environment or a no-echo prompt,
never an argument, and prints only the resulting pair on stdout:

```
SESSION=$(dotnet run --project tools/MintSessionCookie -- --base-url https://reverse-proxy-staging-5c25.up.railway.app/agentforge)
npx @usebruno/cli run --env staging --disable-cookies --env-var "sessionCookie=$SESSION"
```

It adds no endpoint and nothing to the deployed binary, so the AC4 ruling below stands unchanged;
it only removes the DevTools copy-paste, which is what made a fresh cookie per `develop` merge
impractical. The session lasts as long as the OpenEMR token behind it - about an hour - so mint one
per run. **It has been run only against a loopback stub** (its own self-test, `tools/MintSessionCookie/README.md`);
its first run against staging with a real credential is the maintainer's.

### Authenticated run, 2026-09-24 (AC1/AC2)

The maintainer completed a real patient-launch SMART login as `dr_cardio` against staging (build
`agent-forge-sha-6001ab0728d5`, the `develop` HEAD staging was running at the time) and supplied
the resulting `.AspNetCore.Session` value out-of-band. **Per this repository's own rule (no
secrets in source) that value was never seen by, or recorded in, any agent session or any tracked
file - it is not reproduced here or anywhere else.** The run itself, **exactly as it was actually
run** (`npx @usebruno/cli run --env staging --env-var sessionCookie=<redacted> --output ...` -
**without** `--disable-cookies`, which did not exist as a documented step yet), is:

| Request | Result |
|---|---|
| **`Evidence/Ask With Document`** | **200 - 9/9 assertions.** Answer 2575 characters, **7 citations** (the assertions check only that there is at least one - `citations.length gt 0` - plus `page` and `factId` on the first one; the 7 is observed, not asserted - see below), 5 handoffs. The strongest candidate a separate change named, and the one that closes AC1: a grounded, cited, answer-producing request observed green against the deployed stack |
| **`Evidence/Ask`** | **200 - 8/8 assertions.** Answer 845 characters, 4 handoffs |
| `Chat/Hub Negotiate`, `Health/Liveness`, `Health/Readiness`, `Launch/SMART Launch Redirect`, `Metrics/Prometheus Scrape` | pass, as in the fail-closed run above |
| `Agenda/Daily Agenda`, `Agenda/Select Patient` | 401 - **expected**: this session came from a single-patient launch, not an `/agenda/launch` one, so no `AgendaSessionContext` exists for it to read |
| `Documents/Ingest` | 404 - by design, as above; no session changes that |
| `Patient/Patient Context` | **401 in this run**, although a manual `GET /patient` with the same cookie returned 200 seconds earlier. **This is exactly why the run above did not use `--disable-cookies` - the flag did not exist as a documented step until this 401 was diagnosed.** Root-caused as the CLI's own cookie jar (on by default, which is why this run hit it): it overwrote the explicit `Cookie` header with the fresh, unauthenticated cookie `Launch/SMART Launch Redirect` mints, since `Launch/` runs immediately before `Patient/` alphabetically. This was a collection/tooling defect, not a server-side session bug. **The fix was reproduced and confirmed against a local echo listener, independent of any real credential - not by re-running `Patient/Patient Context` against staging.** `--disable-cookies` was added to every documented run command *after* this run, because of it, and is the documented form from now on |

No PHI, no answer text and no cookie value appear above or anywhere in this repository - character
counts, assertion counts and status codes only, per this run's own ground rule. "7 citations" is
the count the run observed; the assertions check only `citations.length gt 0` plus
`citations[0].page isNumber` and `citations[0].factId isString` - nobody inspected whether the quotes located `exact` or
`unlocatable`, so that is as far as this record goes.

### Is the cookie requirement itself a defect?

**Ruling: no, not in the collection or in the SMART launch design - but the combination with
staging's redeploy cadence is a real, separate piece of friction, tracked rather than built.**

A real, browser-mediated OAuth login producing a clinician-scoped session is the correct security
posture for these endpoints, not a shortcut this collection failed to take - `/callback`'s
single-use authorization code cannot be scripted into a static request any more than a real login
prompt can, and the in-process test-only bypass (`BffQaFixture.SeedAuthenticatedSessionAsync`,
`SeedSessionStartupFilter`) is deliberately confined to the integration-test host and must not be
extended into the real, internet-facing binary - doing so would be a new authentication-bypass
surface, not a testing convenience. On that basis, requiring a human to complete the login once is
not a defect.

**What changed the picture is a separate change: staging redeploys on every `develop` merge, and a redeploy
does not preserve the session store.** A cookie minted today is very likely invalid by the time
someone reaches for it tomorrow, so what is a one-time manual step for `local` becomes a
**recurring** one against staging - not a correctness problem (the collection still fails closed
correctly with a stale cookie, as `Evidence/Ask` and `Evidence/Ask With Document` did in every run
before this one), but real friction against ever having a *repeatable*, scheduled answer-producing
check on staging. That is worth solving, but not by building anything into this collection or the
deployed binary: a separate change was filed for a scripted, out-of-band way to mint a fresh cookie (a headless
browser driving the real login, run by whoever holds the credential), and built it as
`tools/MintSessionCookie` - see "Or script that login" above.

## Prerequisites

The sidecar must be running locally first:

```
dotnet run --project src/AgentForge.Api
```

This starts it on `http://localhost:5113` (the `local` environment's `baseUrl`), matching
`src/AgentForge.Api/Properties/launchSettings.json`'s `http` profile. That's sufficient for
Health/, Launch/ and Chat/ - none of them need a real OpenEMR, only configured-but-unreachable
`OpenEmr:*` values so the host passes `ValidateOnStart()`. **Four folders need more.** Agenda/,
Patient/ and Evidence/ need a real session (next section). Documents/ and Evidence/ need Week 2
configuration - a database and an LLM key ("Week 2 endpoints", further below). Documents/ needs
*only* that, and no session: see its own `docs` block for why that route is gated by network
topology rather than by a cookie.

## Getting a session cookie

`/agenda`, `/agenda/select-patient`, `/patient` and `/evidence/ask` need a real, authenticated BFF
session - the
same constraint `tools/LoadTestChat/README.md`'s "Why session cookies, not a scripted login"
already documents: a launch requires a genuine browser-mediated SMART OAuth redirect through a
real OpenEMR, and the test-only session-seed bypass the automated integration suite uses
(`BffQaFixture.SeedAuthenticatedSessionAsync`, `SeedSessionStartupFilter`) exists only in that
in-process test host, never in the real deployed binary. That's a solvable, one-time manual step,
not a protocol barrier like the WebSocket chat hub below - once you have the cookie it's just an
HTTP header on however many plain requests you want to replay, unlike `/callback`'s single-use
authorization code.

**This needs a real, reachable OpenEMR** - liveness/redirect probes don't, which is the only
reason Prerequisites above gets away with a bare, disconnected sidecar. The steps below name every
`OpenEmr__*`/`Bff__*` value they depend on, since none of it works by assumption.

### 1. A real OpenEMR

```
docker compose up -d
```

The repo root's zero-config tier (`README.md` "Run it") - `http://localhost:8080`, `admin` /
`LocalDev1!`. `admin` is enough to complete a launch and prove the recipe (below); it isn't a
*provider*, so its agenda roster is legitimately empty (see Select Patient.bru's `docs` block) -
follow `DEPLOYMENT.md` §4/§4a to seed the `cardio1` demo provider and cohort for a non-empty one.

### 2. Register this collection's own SMART clients

`tools/RegisterSmartClients` registers a redirect URI at the **same** host it contacts to
register (`<frontDoorBaseUrl>/agentforge/(agenda/)callback`) - by design, since in every deployed
topology OpenEMR and the sidecar share one front door (`DEPLOYMENT.md` §2, "the one-origin
invariant"). Running the sidecar as a bare process on its own port (above) means OpenEMR
(`:8080`) and the sidecar (`:5113`) are **not** that one door, so the tool can't be pointed at
either host alone: `-- http://localhost:8080` would register a redirect URI the bare sidecar never
answers on, and `-- http://localhost:5113` tries to reach OpenEMR's registration endpoint on the
sidecar's own port, which 404s. Register directly against OpenEMR's real address instead, with
redirect URIs pointed at the bare sidecar - same request shape `tools/RegisterSmartClients` sends,
same scope lists (`SmartLaunchScopes.PatientLaunch`/`AgendaLaunch`), just naming the two hosts
separately:

```powershell
$patientScopes = "openid fhirUser launch launch/patient api:fhir patient/Patient.read patient/Encounter.read patient/Observation.read patient/DocumentReference.read patient/Binary.read patient/Condition.read patient/AllergyIntolerance.read patient/MedicationRequest.read patient/Procedure.read patient/DiagnosticReport.read patient/Appointment.read api:oemr"
$agendaScopes  = "openid fhirUser launch api:fhir user/Appointment.read user/Patient.read user/Encounter.read user/Observation.read user/DocumentReference.read user/Binary.read user/Condition.read user/AllergyIntolerance.read user/MedicationRequest.read user/Procedure.read user/DiagnosticReport.read"
$sidecarBase = "http://localhost:5113/agentforge"   # where the bare sidecar (step 3) actually answers
$openEmrBase = "http://localhost:8080"               # where OpenEMR actually is (step 1)

function Register($name, $redirectPath, $scope) {
  $body = @{ application_type = "private"; client_name = $name
    redirect_uris = @("$sidecarBase$redirectPath"); grant_types = @("authorization_code","refresh_token")
    response_types = @("code"); token_endpoint_auth_method = "client_secret_post"; scope = $scope } | ConvertTo-Json
  Invoke-RestMethod -Uri "$openEmrBase/oauth2/default/registration" -Method Post -Body $body -ContentType "application/json"
}
$patient = Register "AgentForge Copilot (patient launch)" "/callback" $patientScopes
$agenda  = Register "AgentForge Copilot (roster/agenda launch)" "/agenda/callback" $agendaScopes
"OpenEmr__ClientId=$($patient.client_id)  OpenEmr__ClientSecret=$($patient.client_secret)"
"OpenEmrAgenda__ClientId=$($agenda.client_id)  OpenEmrAgenda__ClientSecret=$($agenda.client_secret)"
```

Then enable both and set OpenEMR's Site Address Override - `tools/RegisterSmartClients`'
unmodified next step, since this part **is** about OpenEMR's own state, not either host's:

```
MYSQL_ROOT_PASSWORD=rootpass dotnet run --project tools/BootstrapOpenEmr -- http://localhost:8080
```

(needs the MySQL-publishing overlay first - `docker compose -f docker-compose.yml -f docker-compose.bootstrap.yml up -d`, per root `README.md`). It matches clients by `client_name LIKE 'AgentForge%'`, so the pair just registered is included.

### 3. Run the sidecar with the values this recipe assumes

```
OpenEmr__BaseUrl=http://localhost:8080
OpenEmr__Site=default
OpenEmr__AllowInsecureHttpForLocalDevelopment=true
OpenEmr__ClientId=<from step 2>
OpenEmr__ClientSecret=<from step 2>
OpenEmr__Scopes__0..16=<$patientScopes above, one per index>
OpenEmrAgenda__ClientId=<from step 2>
OpenEmrAgenda__ClientSecret=<from step 2>
OpenEmrAgenda__Scopes__0..14=<$agendaScopes above, one per index>
Bff__PublicBaseUrl=http://localhost:5113/agentforge
Bff__PathBase=/agentforge
Bff__AllowInsecureHttpForLocalDevelopment=true
```

then `dotnet run --project src/AgentForge.Api` (Prerequisites, above) as usual. `Bff__PathBase`
here is **not** a reverse proxy - there isn't one in this recipe - it's `UsePathBase` self-hosting
the sidecar under `/agentforge` so the redirect URIs step 2 registered
(`http://localhost:5113/agentforge/(agenda/)callback`) resolve, exactly the mechanism a real proxy
also relies on. It changes nothing for the rest of the collection: `UsePathBase` passes through any
request path that doesn't start with `/agentforge` unchanged, so `{{baseUrl}}/health` and every
other existing request keeps hitting the same bare `http://localhost:5113` origin
(`local.bru`'s `baseUrl` is unchanged) - confirmed live, both forms return identical status codes.

### 4. Log in once, copy the cookie

**Or let `tools/MintSessionCookie` do this step**: it completes the same login in a
headless browser and prints the `Name=Value` pair. Against this recipe's bare sidecar, OpenEMR is on
a different origin from the sidecar, so name it - credentials are never typed into any other origin:
`dotnet run --project tools/MintSessionCookie -- --base-url http://localhost:5113/agentforge
--openemr-base-url http://localhost:8080`, with `MintSession__Username`/`MintSession__Password` set or
left unset to be prompted. Its README has the options. By hand:

Open `http://localhost:5113/agentforge/agenda/launch` in a real browser, log in (`admin` /
`LocalDev1!`, or a seeded provider) and approve - you land on the agenda SPA once the session is
established. Open DevTools → Application (Chrome) / Storage (Firefox) → Cookies, find the session
cookie (default ASP.NET Core session cookie name: `.AspNetCore.Session`), and copy **both the name
and the value** - the `Name=Value` pair, per "Running it" above.

### 5. Supply it

**Without ever writing it into `environments/local.bru`** (checked into git - no secrets in
source): `--env-var sessionCookie=".AspNetCore.Session=<value>"` on the CLI, or
paste the pair into the `local` environment's `sessionCookie` variable in the Bruno app for that
local run only, without saving the change back to the file.

One login covers every session-gated request: `Agenda/Select Patient.bru` runs right after
`Agenda/Daily Agenda.bru` in the same collection run and turns that same session into a
`PatientSessionContext` (`AgendaEndpoints.HandleSelectPatientAsync`), which both `Evidence/*.bru`
and `Patient/Patient Context.bru` then read - see each request's `docs` block for the exact
hand-off. Folders run in alphabetical order, which is why Agenda/ (the drill-down that establishes
the context) lands before Evidence/ and Patient/ (which consume it); a direct single-patient
`/launch` -> `/callback` establishes the same context without the agenda step.

**Verified live** (2026-09-20): steps 1-4 against a real `docker compose up -d` OpenEMR and a real
bare sidecar - the browser lands on the agenda SPA and its own `GET /agentforge/agenda` call
returns `200` with a real (`admin`-scoped, so empty) roster body. Extracting the cookie's raw value
programmatically wasn't possible here (`.AspNetCore.Session` is `HttpOnly` by design - invisible to
page JS, only readable through the DevTools UI a human uses in step 4), so step 5's exact byte
transmission was instead verified by pointing `Agenda/Daily Agenda.bru` at a local header-echo
listener with `--env-var sessionCookie=".AspNetCore.Session=SENTINEL"`: the listener recorded a
single well-formed `Cookie: .AspNetCore.Session=SENTINEL` line, confirming the fix in "Running it"
above (no name/value doubling) against the real, shipped request files. Between the two, every step
of the recipe has been exercised for real except a human eyeballing the DevTools cookie panel.

## Week 2 endpoints (`Documents/`, `Evidence/`)

`POST /documents/ingest`, `POST /evidence/ask` and `GET /evidence/document/{documentId}` are
**additive and optional** in the host: `Program.cs` maps them only when
`AgentForgeData__ConnectionString` is set, so that a Week 1 deployment boots without a database.
**With it unset these routes do not exist and every request here returns 404**, which fails the
same assertions a 401 does - a red run, a different cause. Check that first if `Evidence/Ask`
fails while `Patient/Patient Context` passes.

**`Evidence/` also reads `GET /patient` first**, in a pre-request script, and sends its `contextKey` as the
`context` form part: `POST /evidence/ask` answers a missing or stale key `409`, so a stale page is never answered
about a patient it does not show. An `Evidence/` request that fails `409` while
`Patient/Patient Context` passes means the script did not run - check the runner allows scripts.

They need three things beyond the session recipe above.

### 1. A database

Postgres with pgvector - the hybrid-RAG store uses a vector column and an HNSW index, and the EF
migrations create the extension, so stock Postgres will not do. The repo's own compose service is
`postgres` (`docker-compose.yml`, profile `copilot`); it is not port-published, so a **bare**
sidecar needs its own:

```
docker run -d --name agentforge-pg -p 55432:5432   -e POSTGRES_DB=agentforge -e POSTGRES_USER=agentforge -e POSTGRES_PASSWORD=agentforge   pgvector/pgvector:pg17
```

Then add to the step-3 environment:

```
AgentForgeData__ConnectionString=Host=localhost;Port=55432;Database=agentforge;Username=agentforge;Password=agentforge
```

Nothing else is needed - the host applies its own migrations and seeds the guideline corpus once it is
listening (W2-D14), retrying until the database answers, so the database starts empty and comes up ready.
Wait for `/ready`'s `vector-index` check to read `Healthy` before running the evidence requests: until the
migrations have completed it is 503, the seed lands just after, and a database started after the host is
waited for, not fatal.

### 2. A real LLM key

Both endpoints call the model on the request path and **neither has a deterministic fallback**:
`/documents/ingest` sends the PDF to the vision model, and `/evidence/ask` calls the composer. A
placeholder `Llm__ApiKey` is enough for the host to boot (it is `[Required]`, but nothing verifies
it), which is a trap worth naming: the routes then map and accept the request, and fail at the
provider instead - `/documents/ingest` was observed returning 500 off `api.anthropic.com`'s own
401 on a placeholder key. Set a real one:

```
Llm__ApiKey=<a real Anthropic key>
```

`Evidence/Ask With Document` extracts the fixture fresh on every run; `Documents/Ingest` is
content-hash idempotent (W2-D3) and so only extracts on its first run.

They are **not** the only requests here that spend model tokens, and not even the most expensive:
`GET /agenda` runs a full agent turn *per roster patient* on its first run, and again once the
30-minute summary cache has expired (a re-run inside that re-serves the summaries free)
(`AgendaRosterService` fans
`AgendaPatientSummaryRunner`, which calls `IAgentOrchestrator.StartAgendaSummaryAsync` - the same
`RunTurnAsync` the chat brief uses), so on a seeded roster it is the heaviest request in the
collection. `GET /patient` makes no model call at all - it is FHIR reads only.

### 3. The fixture, which is already here

`fixtures/synthetic-lab-report.pdf` - a one-page synthetic cardiology panel (potassium 5.4,
creatinine 1.8, eGFR 38, INR 3.6, NT-proBNP 1450, all flagged abnormal). **Synthetic throughout: no
PHI, not derived from any real report**. It is deliberately written
*uncompressed*, so `grep` on the file shows its text without a PDF viewer - a reviewer can confirm
what is in it without trusting this paragraph. Its text layer is real (PdfPig reads 131 words off
it), which is what lets the server-side quote matcher score its citations `exact` rather
than `unchecked`.

### What each new request exercises

| Request | Use case | Requirement | What it proves |
|---|---|---|---|
| `Documents/Ingest` | **UC-9 (`REQUIREMENTS.md` / `REQUIREMENTS.md` §5)** - document-derived pre-visit brief; extends `REQUIREMENTS.md` UC-1/UC-3 | FR-DOC-1, FR-DOC-3 | the pre-visit path: upload -> vision extraction -> strict schema gate -> derived facts persisted and citable |
| `Evidence/Ask` | **UC-10 (`REQUIREMENTS.md` / `REQUIREMENTS.md` §5)** - evidence-grounded answer; extends `REQUIREMENTS.md` UC-2/UC-3 | FR-GRAPH-1..3 | that the graph ran all four nodes and returned a non-empty answer. **It does not detect a citation regression:** `citations: isArray` and `suppressedClaimCount: isNumber` are both satisfied by `[]` and `0`, because with nothing ingested for the session's patient those *are* the correct values. `Ask With Document` is the row that pins citations (FR-CITE-2) |
| `Evidence/Ask With Document` | **UC-9 + UC-10 (`REQUIREMENTS.md` / `REQUIREMENTS.md` §5)** | FR-CITE-2 | all of the above plus in-turn extraction, returning click-to-source citations with page and quote |

> **The numbering used to collide; it no longer does.** `REQUIREMENTS.md` originally numbered its own Week 2 use
> cases from 6, so its **UC-6 was not `REQUIREMENTS.md`'s UC-6** (the day's agenda, which the `Agenda/` requests
> cite). Both docs now number the Week 2 set **UC-9..UC-11** instead, so there is one numbering,
> not two — the requests above were updated to match.

### What was verified, and what was not

Run against a real Week 2-enabled sidecar (a real pgvector container, real EF migrations, real
guideline seeding) on 2026-09-20: the routes map, `/evidence/ask` returns **401 before any
retrieval or model work** on a missing, malformed and absent-multipart request alike, and
`/documents/ingest` accepts this collection's exact multipart body - file part, `patientId`,
`documentReferenceId`, `docType` - and reaches the extractor's real `api.anthropic.com` call. The
multipart envelopes Bruno puts on the wire were captured off a header-echo listener and checked
part by part, and the fixture was read by the shipped `PdfPigWordReader` (1 page, 131 words, every
quoted value locatable).

**A green 200 from either endpoint was not reproduced here**, because it needs an Anthropic key
this workstation does not hold - and, for `Evidence/`, a browser SMART login on top. The
assertions are written against the response contracts in `EvidenceResponsePayload` and
`IngestionResponsePayload` and were exercised against recorded-shape responses, not against a live
answer. That was this collection's one untested link as of 2026-09-20, against a bare local
sidecar. **It no longer is: see "Authenticated run, 2026-09-24" under "Running it against
staging" above** - both `Evidence/Ask` and `Evidence/Ask With Document` have since returned 200
against the deployed staging stack, with citations on the document leg.

## What's covered

- **Health/** - `/health` (liveness) and `/ready` (real dependency checks - REQUIREMENTS.md §13.1).
- **Launch/** - `/launch`, the SMART EHR launch redirect (INTERFACES.md A.3).
- **Metrics/** - `/metrics`, the Prometheus scrape endpoint (FR-OBS-3).
- **Chat/** - the SignalR chat hub's negotiate handshake.
- **Agenda/** - `/agenda` and `/agenda/select-patient`, the day's roster and its per-patient
  drill-down (**UC-6**, `REQUIREMENTS.md`; ARCHITECTURE.md §19).
- **Patient/** - `/patient`, the launched patient's identity + FHIR reachability behind the
  per-visit landing confirmation (**UC-1**, `REQUIREMENTS.md`, and the tail of the UC-6 drill-down).
- **Documents/** - `/documents/ingest`, the pre-visit document-ingestion call
  (**UC-9**, `REQUIREMENTS.md` / `REQUIREMENTS.md`; FR-DOC-1).
- **Evidence/** - `/evidence/ask`, twice: question-only, and question + attached lab PDF. The
  answer-producing flow (**UC-10**, and **UC-9 + UC-10** with the document; `REQUIREMENTS.md` / `REQUIREMENTS.md`).

Together those close FR-EVAL-4's "graders must be able to run any workflow from this collection"
for the evidence path (NFR-API-W2-1), which the collection could not reach before - note that it
is the *evidence* path that was missing, not agent behaviour as such: `GET /agenda` already fans
the full agent loop, citation and verification gate over the roster per patient
(`AgendaPatientSummaryRunner` -> `IAgentOrchestrator.StartAgendaSummaryAsync` -> the same
`RunTurnAsync` as the chat brief). The sidecar's
Week 2 HTTP surface is exactly three routes - `/documents/ingest`, `/evidence/ask` and
`/evidence/document/{documentId}` - so the collection now reaches all of it except the document
fetch, whose exclusion is argued below rather than assumed. NFR-API-W2-1's *other* half has since
moved on without this collection: the sidecar now exports an OpenAPI 3.0 spec
(`--export-openapi`, INTERFACES.md §D.1), and it
does cover the document fetch, because the runnability argument below is about a caller being able
to produce an id and a spec documents a route either way. The spec is generated from the
implementation, so it cannot describe a different surface.

## What's not covered, and why

The actual chat conversation (`/hubs/chat`) is a WebSocket-based SignalR protocol, not REST - a
plain HTTP collection can prove the hub is mapped and reachable (via negotiate) but can't drive a
real multi-message exchange the way `tests/AgentForge.IntegrationTests/Api/*` already
does with a real `HubConnection`. The SMART launch's `/callback` step isn't included either: it
requires a real, live authorization code from an actual OpenEMR login, which can't be scripted
into a static collection request - see `tools/LoadTestChat/README.md`'s note on why session
cookies stand in for a scripted login, and `BffLaunchFlowTests`/
`BffQaFixture.SeedAuthenticatedSessionAsync` for how the automated test suite gets an
authenticated session without a live browser login. Unlike `/callback`'s single-use authorization
code, the session cookie a live login produces *is* reusable across plain requests - which is
exactly what makes the Agenda/, Evidence/ and Patient/ requests above possible.

**`GET /evidence/document/{documentId}` is out of scope, deliberately.** It is the production
click-to-source fetch: it streams a source PDF's bytes out of OpenEMR as FHIR `Binary`, so its
`documentId` must name a document that actually exists **in OpenEMR**. Nothing in this collection
can produce one. `Documents/Ingest` looks like it should - it takes a `documentReferenceId` - but
that is an *input* it merely records and echoes back, pointing at a document the front desk
already uploaded natively; the sidecar never writes to OpenEMR and never mints an id (FR-DOC-3,
"one authority per data type"). Chaining the two would therefore ask OpenEMR for
`SYNTH-LAB-0001`, which it has never heard of, and get a 404 that says nothing about
click-to-source. Exercising it for real needs a document id read off a live OpenEMR
`DocumentReference` search, which is a different collection than this one; the route itself is
covered by `EvidenceEndpoints`' own tests, and it was confirmed here to be mapped and
session-gated (401 without a cookie, not 404).
