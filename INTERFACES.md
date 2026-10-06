# AgentForge Interfaces

This document defines every boundary between the AgentForge sidecar and the systems around it: how it
authorizes against OpenEMR, which FHIR data it reads, how OpenEMR audits that access, and the HTTP, SignalR and
tool surface the sidecar itself exposes. Use it when integrating a new OpenEMR instance, writing a client
against the sidecar, or changing any of these contracts.

## 0. Interface summary

| ID | Interface | Direction | Transport | Standard |
|---|---|---|---|---|
| A | Authorization and launch | sidecar to OpenEMR | HTTPS | OAuth2 + OIDC + SMART-on-FHIR v2 |
| B | FHIR R4 data API | sidecar to OpenEMR | HTTPS (`application/fhir+json`) | HL7 FHIR R4 / US Core |
| C | Audit boundary | inside OpenEMR | none | OpenEMR's `EventAuditLogger` (a property, not a called API) |
| D | Sidecar-exposed surface (backend-for-frontend) | browser and OpenEMR module to sidecar | HTTPS / WebSocket | HTTP + OpenAPI 3.0, SignalR, MCP tool contracts |

Properties common to A and B:

- **Base host** is the deployed OpenEMR instance, configured per environment (`OpenEmr__BaseUrl`). OpenEMR is
  multi-site, so paths carry a `{site}` segment (`OpenEmr__Site`, `default` on a demo instance).
- **TLS on every hop.** Bearer tokens are held server-side only. No PHI appears in URLs beyond unavoidable
  patient and resource identifiers, and none in diagnostic logs or telemetry; the access-audit trail is the
  one exception and names the patient by design (NFR-SEC-1, FR-AUTH-4).
- **Clinician identity only.** All data access uses the clinician's own OAuth token; there is no service
  account. OpenEMR enforces its ACLs and scopes server-side, so the sidecar can never exceed the user's own
  access. Access control sits below the language model, not in it.

The OpenEMR facts below were checked against the OpenEMR fork the sidecar is deployed with (OpenEMR v8 base).
See [ARCHITECTURE.md](ARCHITECTURE.md) for how these interfaces fit the overall design and
[DEPLOYMENT.md](DEPLOYMENT.md) for the configuration that wires them.

## A. Authorization and SMART EHR launch

### A.1 Discovery

| Purpose | Endpoint |
|---|---|
| OIDC metadata | `GET /.well-known/openid-configuration` |
| SMART metadata (capabilities, scopes, endpoints) | `GET /.well-known/smart-configuration` |

Resolve authorize, token, introspection and registration URLs from discovery rather than hard-coding them.

### A.2 Endpoints

All relative to `/oauth2/{site}`:

| Purpose | Endpoint |
|---|---|
| Authorize | `GET /authorize` |
| Token | `POST /token` |
| Token introspection | `POST /introspect` |
| Dynamic client registration | `POST /registration` |

The token, introspection and registration calls are the Refit interface `IOpenEmrAuthApi` in
[src/AgentForge.Integration.OpenEmr](src/AgentForge.Integration.OpenEmr/).

### A.3 Flow

1. **Register** each sidecar client once through `POST /oauth2/{site}/registration`. There are two clients:
   the single-patient launch (redirect `/agentforge/callback`) and the Daily Agenda launch (redirect
   `/agentforge/agenda/callback`). The [tools/RegisterSmartClients](tools/RegisterSmartClients/) project
   does this and reports any scope the registration dropped.
2. **SMART EHR launch.** The OpenEMR module (`oe-module-agentforge`) launches the sidecar with a `launch`
   token and `iss` from either the patient chart's **Launch AgentForge** button or the **Daily Agenda** tab. The
   sidecar opens in a top-level browser tab (default) or a same-site modal iframe, and runs the
   authorization-code flow with PKCE against `/authorize` and `/token`.
3. **Token response.** A Bearer `access_token` (plus a `refresh_token`) whose claims carry the user, the granted
   scopes and the launch patient context. The access token lives one hour. The refresh token is single-use
   and the sidecar does not use it.
4. **Validate and hold.** The sidecar validates the token with `POST /introspect` and keeps it server-side in
   the backend-for-frontend, never in the browser (decision **D11**: the token stays in the sidecar's session
   store and only an opaque session cookie reaches the browser). It stores the token's expiry instant with it
   (`exp` from introspection, else `expires_in`). The browser session's idle timeout slides with use and the
   token's lifetime does not, so without that instant an active session would outlive its token and every FHIR
   read would fail with 401.
5. **Re-launching is the only renewal.** An expired session is refused with a message saying so, never
   silently degraded.

Headless or batch access (for example a morning triage run) would need `client_credentials` with `system/*`
scopes or `offline_access` refresh tokens. Both grants exist in OpenEMR; neither is used.

### A.4 Scopes requested

Request only read scopes. `ClinicalScopeGuardrailTests` holds every `<context>/<Resource>` scope on both
clients to a `.read` suffix, so a write scope cannot be added silently. This is the outermost of the controls
behind non-goal **NG1** (the copilot does not place orders or write to the chart), and the only one an OpenEMR
administrator can see.

The registered supersets live in code, in `SmartLaunchScopes`
([src/AgentForge.Integration.OpenEmr/SmartLaunchScopes.cs](src/AgentForge.Integration.OpenEmr/SmartLaunchScopes.cs)).
What each launch actually requests is configuration: `OpenEmr__Scopes__N` for the single-patient launch and
`OpenEmrAgenda__Scopes__N` for the agenda launch (see [docker-compose.yml](docker-compose.yml) and
[.railway/railway.ts](.railway/railway.ts)). Requested scopes must stay within the registered superset.
These are contiguous 0-based lists; a gap silently truncates the list at the first missing index.

| Data need | Single-patient client | Agenda client |
|---|---|---|
| Launch and identity | `openid`, `fhirUser`, `launch`, `launch/patient` | `openid`, `fhirUser`, `launch` |
| FHIR API companion | `api:fhir` | `api:fhir` |
| Demographics | `patient/Patient.read` | `user/Patient.read` |
| Encounters | `patient/Encounter.read` | `user/Encounter.read` |
| Labs and vitals | `patient/Observation.read` | `user/Observation.read` |
| Problems | `patient/Condition.read` | `user/Condition.read` |
| Allergies | `patient/AllergyIntolerance.read` | `user/AllergyIntolerance.read` |
| Medications | `patient/MedicationRequest.read` | `user/MedicationRequest.read` |
| Procedures | `patient/Procedure.read` | `user/Procedure.read` |
| Reports and documents | `patient/DiagnosticReport.read`, `patient/DocumentReference.read` | `user/DiagnosticReport.read`, `user/DocumentReference.read` |
| Source bytes for click-to-source | `patient/Binary.read` | `user/Binary.read` |
| Clinic-day appointments and the relationship check | `patient/Appointment.read` | `user/Appointment.read` |
| OpenEMR standard API | `api:oemr` (registered only) | none |

The deployed agenda launch requests only the identity scopes, `api:fhir`, `user/Appointment.read` and
`user/Patient.read` from that superset.

Rules an integrator must know:

- **`api:fhir` is required** beside every resource scope; asking for a resource scope without it is rejected
  as `invalid_scope`.
- **Resource names are PascalCase** (`patient/Patient.read`). The lowercase form is rejected as
  `invalid_scope` at both `/authorize` and `/registration`.
- **OpenEMR silently drops** any requested scope that is not on the registered client
  (`ScopeRepository::finalizeScopes()` intersects the two). A missing registration surfaces much later as a 401
  on one FHIR call. The sidecar logs any requested resource scope its token was issued without, and
  [tools/BootstrapOpenEmr](tools/BootstrapOpenEmr/) reconciles a client registered before a scope existed.
- **The agenda needs its own client.** The agenda launch has no patient in context, so it uses `user/*.read`
  scopes under a separately registered client; reusing the single-patient `client_id` would silently omit
  them from the token.
- **`patient/Appointment.read` is an authorization need, not a data need.** The FR-AUTH-2 relationship check
  (B.2) searches `Appointment` on every launch and fails closed, so without this scope every single-patient
  launch is refused with 403.
- **Click-to-source confinement does not depend on the `Binary` scope.** `GET /evidence/document/{id}` serves
  only documents the sidecar's own ingest index attributes to the session's patient (D.1), so requesting
  `user/Binary.read` on the agenda launch would not widen what the overlay can read.
- **Not every FHIR resource has a grantable V1 scope.** `MedicationDispense` exists only in OpenEMR's V2
  catalog (`.rs` form), so `patient/MedicationDispense.read` refuses the whole authorization. The sidecar does
  not read `MedicationDispense`; medication adherence is carried by `MedicationRequest`. Adding the read back
  would first need proof, on a throwaway client, that a mixed `.read`/`.rs` registration survives.

## B. FHIR R4 data API (US Core)

- **Base path:** `/apis/{site}/fhir/` (demo: `/apis/default/fhir/`).
- **Media type:** `application/fhir+json`.
- **Profiles:** US Core 3.1.0 per OpenEMR's route file; confirm the profile on your instance.
- **Auth:** `Authorization: Bearer <access_token>` from Interface A.
- **Read-only.** The sidecar issues no FHIR writes.

The calls are the Refit interface `IOpenEmrFhirApi` in
[src/AgentForge.Integration.OpenEmr/Fhir](src/AgentForge.Integration.OpenEmr/Fhir/). Bearer and
correlation-id headers are attached by `DelegatingHandler`s, not per method.

### B.1 Resources consumed

| Cardiology need | FHIR resource | OpenEMR source | Notes |
|---|---|---|---|
| Demographics | `Patient` | `patient_data` | |
| Problems (AFib, HFrEF, CAD) | `Condition` | `lists` | |
| Medications | `MedicationRequest` | `prescriptions` / `drugs` | `MedicationDispense` is not read (A.4) |
| Labs (INR, K+, Cr, lipids, BNP) | `Observation` (category `laboratory`), `DiagnosticReport` | `procedure_result` / `procedure_report` | |
| Vitals (BP, HR) | `Observation` (category `vital-signs`) | `form_vitals` | |
| Allergies | `AllergyIntolerance` | `lists` | |
| Encounters (interval events) | `Encounter` | `form_encounter` | drives the "since last visit" diff |
| Procedures (PCI, ablation) | `Procedure` | `procedure_order` / `procedures` | |
| EF and echo findings | `DiagnosticReport`, `DocumentReference` | documents and narrative | unstructured; extracted values are labelled derived (FR-DATA-4) |
| Device (pacemaker/ICD) | `DocumentReference` | narrative | interrogation reports are narrative; there is no `Device` read |
| Source document bytes | `Binary` | documents | click-to-source overlay only |
| Clinic-day schedule (UC-6) | `Appointment` | `openemr_postcalendar_events` | also the input to the FR-AUTH-2 relationship check |

`Observation.effectiveDateTime` keeps the offset it arrived with; every other mapped date is normalised to UTC.
The verification layer shows the clinician the civil date a result was drawn, and normalising would move an
evening draw to the next day. The instant is unchanged, so comparisons and ranking behave identically.

### B.2 Operations

- **Read:** `GET /apis/{site}/fhir/{Resource}/{id}`.
- **Search:** `GET /apis/{site}/fhir/{Resource}?patient={id}&...` returns a searchset `Bundle`. Follow
  `Bundle.link[rel=next]`; never assume one page.
- **Observation search parameters:** `_id`, `patient`, `category`, `code`, `date`, `status`. Example:
  `GET /apis/default/fhir/Observation?patient=1&category=laboratory&date=ge2026-01-01`.
- **Appointment search** supports only `patient`, `_id`, `date` and `_lastUpdated`; there is no
  `practitioner` parameter. The sidecar sends `GET /apis/{site}/fhir/Appointment?date=eq{today}`, where
  `{today}` is the clinic's local date (`ClinicClock`, configured by `Clinic:TimeZone`, because OpenEMR stores
  appointment dates without an offset). It then keeps only appointments whose `participant[].actor` is
  `Practitioner/{sub}`, or `Person/{sub}` for a provider with no NPI, where `{sub}` is the introspected
  subject (it equals the FHIR `Practitioner.id`).
- **The same search authorizes access.** The FR-AUTH-2 relationship check (a clinician may only open a patient
  they have an appointment with in today's clinic) issues the identical query on every session, single-patient
  ones included, and matches both the patient and the provider participant. Because the check fails closed, an
  `Appointment` search that errors denies access rather than degrading a feature. Under
  `patient/Appointment.read` OpenEMR binds the search to the launch patient, which is all the check needs.
  The one-day window is a sidecar rule: `SearchAppointmentsAsync` takes a single `date` value, so widening it
  means changing that method and both callers.

### B.3 Data and error semantics

| Response | Sidecar handling |
|---|---|
| `200` with resource or `Bundle` | mapped into the tool result |
| `401` | no automatic refresh. A token already known to be expired is refused by `AuthHandler` before sending, with a re-launch message; any other 401 is reported as "this data source is unavailable". Never retried and never trips the circuit breaker |
| `403` | reported as an authorization refusal, with no detail leaked |
| `5xx` / timeout | Polly retry, then a deterministic degraded answer |
| `404` / empty bundle | the gap is reported; nothing is fabricated (UC-5) |

Errors carry a FHIR `OperationOutcome` body.

### B.4 Source-attribution contract

Every consumed resource carries `resourceType` and `id`. The pair `{resourceType}/{id}` is the citation key the
verification layer requires (FR-VERIF-1): any clinical claim in a response must resolve to a resource actually
returned by an Interface B call in that session, or the claim is dropped. Document facts and guideline
evidence use the same idea with `[Document/<id>]` and `[Guideline/<id>]` keys (D.3).

## C. Audit boundary

Not an API the sidecar calls, but a property of the contract: OpenEMR logs FHIR and API access server-side
through `EventAuditLogger`, with break-glass support. Because every call uses the clinician's own token, this
gives a per-user, EHR-side record of exactly what the copilot read, independent of the sidecar's own
access-audit trail (FR-AUTH-4). The sidecar sends `X-Correlation-Id` on every FHIR call (FR-OBS-1), but OpenEMR
does not currently read it, so only the sidecar's trail carries the correlation ID.

## D. Sidecar-exposed surface

Implemented in [src/AgentForge.Api](src/AgentForge.Api/) (`Program.cs`, `Launch/`, `Agenda/`, `Patient/`,
`Evidence/`, `Ingestion/`, `Chat/`, `Health/`) and the tool layer in [src/AgentForge.Mcp](src/AgentForge.Mcp/)
and [src/AgentForge.Agent/McpToolCatalog.cs](src/AgentForge.Agent/McpToolCatalog.cs).

Common properties:

- **Base path.** When `Bff__PathBase` is set (behind the nginx front door, as in every deployed stack) every
  route is served under it, normally `/agentforge`; unset, routes are served at the root. Paths below are shown
  without the prefix.
- **Auth is the session.** Browser-facing endpoints and the hub carry no bearer token; identity is the
  server-side session created by the SMART launch (D11).
- **An expired session is no session.** Every "401" below covers both "never launched" and "launched, then
  the token aged out".
- **Session cookie** is `SameSite=Lax` behind the proxy and `SameSite=None` in the root-hosted fallback.
- **Page binding.** `GET /patient` returns a `contextKey`, a one-way digest of session, site and patient. The
  chat hub and the `/evidence/*` endpoints require it, so a page rendered for one patient cannot act after
  another tab switched the session to a different patient; such a request is refused and the page reloads.

### D.1 HTTP endpoints and OpenAPI

| Method | Path | Purpose | Success | Errors |
|---|---|---|---|---|
| GET | `/launch` | Start the single-patient SMART launch (`iss`, `launch`). The PKCE verifier and `state` go into an encrypted cookie holding the browser's three most recent launches for 10 minutes | 302 to OpenEMR `/authorize` | |
| GET | `/callback` | OAuth redirect (`code`, `state`) | 302 to the chat page | 400 |
| GET | `/agenda/launch` | Start the Daily Agenda launch, same mechanism, own cookie | 302 to `/authorize` | |
| GET | `/agenda/callback` | Agenda OAuth redirect | 302 to the agenda page | 400 |
| GET | `/agenda` | Roster `{ rows[], asOf }` for the launched clinician. Each row's summary is generated now (one LLM turn charged to the session budget) or re-served from the summary cache for up to `Agenda:SummaryCacheTtl` (default 30 minutes) at no charge; `summaryAsOf` says which. A row the budget refuses is `failed` | 200 JSON | 401 |
| POST | `/agenda/select-patient` | Drill down from a roster row (`patientId` query) | 302 to the chat page | 401, 403 (not on this clinician's roster) |
| GET | `/patient` | Launched patient's context (demographics, problems, medications, allergies) and the `contextKey`. Access-audited (FR-AUTH-4) | 200 JSON | 401 |
| POST | `/documents/ingest` | Document ingestion, called by the OpenEMR module's ingestion job. Mapped only when the data tier is configured | 200 | 400, 422 (extraction rejected by the schema gate; nothing stored) |
| POST | `/evidence/ask` | Multimodal evidence question about the session's patient; the form's required `context` field is the `contextKey`. Mapped only with the data tier | 200 | 400, 401, 409 (page for another patient), 403 (no clinical relationship, FR-AUTH-2), 429 (session LLM budget spent) |
| GET | `/evidence/document/{documentId}?context=` | Stream a source document's bytes for the click-to-source overlay (FR-CITE-2), fetched as the clinician. Mapped only with the data tier | 200 file | 401, 409, 403 (not ingested for this patient, or no relationship), 404 (not in OpenEMR) |
| GET | `/health` | Liveness only; no dependency checks | 200 | |
| GET | `/ready` | Readiness (NFR-HEALTH-1, NFR-HEALTH-W2-1): OpenEMR, LLM provider, observability, vector index, reranker | 200 `Healthy` or `Degraded` | 503 `Unhealthy` |
| GET | `/metrics` | Prometheus scrape | 200 text | |
| GET | `/openapi/v1.json` | OpenAPI 3.0 document | 200 JSON | 404 in Production |

**Ingestion trust (W2-D17).** `POST /documents/ingest` carries no token: it acts with no clinician authority
and is trusted by its private-network origin. The sidecar must therefore not be published directly; it is
reachable only through the reverse proxy and the private network, and any private-network caller is trusted
to file documents under the right patient.

**Document responses are inert.** The 200 from `/evidence/document/{id}` sets `Content-Type` from the bytes'
signature (`application/pdf`, `image/png`, `image/jpeg`, `image/gif`, `image/webp`), never from the declared
type; anything else is `application/octet-stream` with `Content-Disposition: attachment`. Every 200 carries
`X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; sandbox`. Grants and
refusals are access-audited.

**`/ready` body** is part of the contract, `application/json`:
`{"status":"Healthy|Degraded|Unhealthy","checks":[{"name":…,"status":…,"description":…}]}`, ordered by name,
with no exception detail. `Degraded` means a dependency was never configured (`Observability:PrometheusHealthUrl`,
`AgentForgeData:ConnectionString` or `Cohere:ApiKey` unset) or the LLM provider answered 429. `Unhealthy`
means a configured dependency is unreachable, slow past the budget, answering an error, or (for Postgres)
missing the `vector` extension, the `guideline_chunks` table, its HNSW index, or this build's migrations. Each
probe is bounded by `Readiness:ProbeTimeout` (2 s), and the OpenEMR, LLM and reranker results are cached for
`Readiness:ResultCacheTtl` (30 s), so `/ready` can lag an outage or recovery by about 34 s.

**Where the machine-readable contracts come from.** This table is authoritative for the contract's meaning
(auth, status codes, the expired-session rule); the OpenAPI document is authoritative for shape (paths,
parameters, schemas), because it is generated from the routes the app maps. `/health`, `/ready`, `/metrics`
and `/openapi/v1.json` are middleware rather than mapped handlers, so they appear only here. There are two ways
to get the documents:

- **From a running sidecar:** `GET /openapi/v1.json`, served in every environment except Production (the
  document exposes endpoint and configuration detail).
- **Exported to a file:** the sidecar writes a contract and exits when given one of these flags.

| Flag | Writes | Needs configuration |
|---|---|---|
| `--export-openapi <path>` | OpenAPI 3.0 document of the HTTP surface | yes: the host starts on an ephemeral loopback port, reads the document and stops |
| `--export-tool-schemas <path>` | JSON Schemas of the MCP tools (D.3) | no |
| `--export-graph-schema <path>` | JSON Schema of the evidence supervisor/worker message contract | no |

The OpenAPI export needs placeholder configuration so the host will start. Nothing is contacted and no secret
is needed. Set `AgentForgeData__ConnectionString`, or the three data-tier routes are not mapped and the
document is valid but incomplete. A relative output path resolves against `src/AgentForge.Api/`, because
`dotnet run` runs there.

```bash
ASPNETCORE_ENVIRONMENT=Development \
OpenEmr__BaseUrl="https://openapi-export.invalid" \
OpenEmr__Site="default" \
OpenEmr__ClientId="openapi-export" \
OpenEmr__Scopes__0="launch" \
Bff__PublicBaseUrl="https://openapi-export.invalid" \
Llm__ApiKey="openapi-export" \
Llm__Model="openapi-export" \
Llm__InputPricePerMillionTokensUsd="0" \
Llm__OutputPricePerMillionTokensUsd="0" \
AgentForgeData__ConnectionString="Host=openapi-export.invalid;Database=agentforge;Username=agentforge;Password=openapi-export" \
Observability__LokiOtlpEndpoint="" \
Observability__TraceOtlpEndpoint="" \
  dotnet run --project src/AgentForge.Api -- --export-openapi out.json

dotnet run --project src/AgentForge.Api -- --export-tool-schemas mcp-tools.json
dotnet run --project src/AgentForge.Api -- --export-graph-schema evidence-graph.json
```

Output is normalised to LF line endings so the same command produces identical bytes on every platform; diff
two exports to review a contract change.

### D.2 SignalR chat hub: `/hubs/chat`

The browser connects to `/hubs/chat?context=<contextKey>` (on every reconnect too) and pins its transport to
WebSockets or Server-Sent Events. The server also accepts long polling. The hub never reads the HTTP session
directly; `ChatHubSessionMiddleware` resolves it for hub requests and hands it over, so behaviour is the same
on every transport. Nothing new reaches the browser.

Every method throws a `HubException` with a fixed message the page matches on:

| Condition | Message | Page behaviour |
|---|---|---|
| No authenticated session | *"No authenticated session - complete the SMART launch first."* | re-launch panel |
| Token expired | `ChatHub.SessionExpiredMessage` | re-launch panel |
| `context` missing or for another patient | `ChatHub.PatientChangedMessage` | reload |
| Session LLM budget spent | `ChatHub.TurnLimitMessage` | says so; no retry |

**Per-session LLM budget.** `RequestBrief`, `AskFollowUp`, `POST /evidence/ask` and each newly generated agenda
summary are charged to one budget: `ConversationBudget:MaxTurnsPerWindow` (800) within
`ConversationBudget:Window` (12 hours) of the first charge. `Resume` is free. Separately, the front door
rate-limits the hub route per client by HTTP request (see [DEPLOYMENT.md](DEPLOYMENT.md)).

Client to server:

| Method | Args | Returns | Purpose |
|---|---|---|---|
| `RequestBrief` | none | nothing (results via `ChatMessage`) | pre-visit brief (UC-1) |
| `AskFollowUp` | `question: string` | nothing (via `ChatMessage`) | follow-up question in the session (UC-2) |
| `Resume` | `lastSeenSequence: long` | `ChatMessage[]` | replay messages after that sequence for the session's current patient; idempotent across reconnects |

Server to client:

| Event | Payload | Notes |
|---|---|---|
| `ChatMessage` | `{ sequence: long, kind: string, payloadJson: string }` | `sequence` strictly increasing, never reused; `kind` is `"brief"` or `"answer"`; `payloadJson` is the serialized body |
| `ChatStatus` | `string` | interim tool-call progress while an answer is assembled; display only, never unverified content, may be dropped |

### D.3 MCP tool catalog

Eight read-only tools (`McpToolCatalog.AllTools`). Every tool validates its input against its contract before
any FHIR call (`McpToolContract.Validate`, NFR-CONTRACT-1). **`patientId` and `site` are never tool
arguments**: the orchestrator binds them from the authenticated launch, and the dispatcher forces them whatever
the model supplies (FR-CHAT-3).

The catalog is closed. A call naming any other tool gets a scope refusal (*"That tool is not available. This
copilot is read-only…"*), which covers both an invented name and an instruction injected through record
content (NFR-SEC-2). A new tool exists only once it is added to the catalog.

| Tool | Arguments | Result |
|---|---|---|
| `get_patient_summary` | none | `{ patient, activeProblems[], activeMedications[], allergies[] }` |
| `get_interval_changes` | `since_date` (required) | `{ medicationChanges[], newLabs[], intervalEncounters[] }` |
| `get_labs` | `since_date` (optional) | `{ labs[] }` of `ObservationRecord` (value, unit, reference range, date) |
| `get_vitals` | `since_date` (optional) | `{ vitals[] }`; null-valued placeholder observations are dropped |
| `get_recent_encounters` | `count` (int 1–20, default 3) | `{ encounters[] }` (date, type, reason) |
| `get_documents` | `document_type` (case-insensitive substring, optional) | `{ documents[] }`: `DiagnosticReport` and `DocumentReference` narratives |
| `get_document_facts` | none | facts extracted from the patient's ingested documents, citable as `[Document/<id>]` |
| `retrieve_evidence` | `query` (required) | clinical-guideline snippets, citable as `[Guideline/<id>]` |

`since_date` is a FHIR date with a comparator prefix (`McpDateFilter.Pattern`):

```text
^(eq|ne|gt|lt|ge|le|sa|eb|ap)[0-9]{4}(-[0-9]{2}(-[0-9]{2})?)?$
```

`[0-9]` is used instead of `\d` because the same literal is read by .NET (validation) and ECMAScript (the JSON
Schema `pattern`), and `\d` means different things in the two.

**Input schemas are generated.** For the six FHIR tools, the request record in `AgentForge.Mcp`
(`GetLabsRequest`, `GetVitalsRequest`, `GetIntervalChangesRequest`, ...) is the single definition: the server
validates its DataAnnotations, and `McpToolInputSchema` derives the model-facing JSON Schema (`required`,
`pattern`, `minimum`/`maximum`, length bounds, `enum`, descriptions) from the same attributes. An attribute it cannot express,
a type-level attribute or an `IValidatableObject` record makes it throw `NotSupportedException`, so the catalog
fails to initialise rather than advertising a weaker contract than the server enforces.
`McpToolSchemaContractParityTests` holds the generated schemas to the records field by field. Consequently,
editing a `Get*Request.cs` file changes what the model sees with no diff in `McpToolCatalog.cs`; review those
files, and compare `--export-tool-schemas` output before and after. `get_document_facts` and
`retrieve_evidence` have no request record; their rules are coded in `McpToolDispatcher` and their schemas are
literals in `McpToolCatalog`.

**Outputs are not schema-validated at run time.** Results (`PatientSummaryResult`, `LabsResult`, ...) are C#
records produced in-process and serialized with `System.Text.Json`; the compiler is the only check, backed by
per-resource mapper unit tests. Renaming or retyping a result record therefore changes what the model reads
without any contract test failing; treat such a change as a contract change.

Any change to an HTTP endpoint, a hub message or a tool's request or result shape must update this document
and be checked against freshly exported contracts.

## Non-functional contract

- **Versioning:** FHIR R4 (fixed) on OpenEMR v8. The capability statement is at
  `GET /apis/{site}/fhir/metadata`.
- **Idempotency:** every OpenEMR operation is a read.
- **Load:** reads are bounded and parallel, minimum-necessary rather than whole-chart pulls, and paginate.
- **Environments:** base URL and `{site}` are per-environment configuration (Options pattern; see
  [CONVENTIONS.md](CONVENTIONS.md)). Demo instances use `site=default` and synthetic data only.

## Open items to confirm on your instance

- The exact US Core profile and USCDI version (OpenEMR's route file says US Core 3.1.0).
- The scope-to-resource mapping for `Condition` (backed by `lists`) and labs.
- Extraction fidelity for EF, echo findings and device interrogation, which come from narrative sources.
- The contents of the `metadata` capability statement (resources, search parameters, interactions).
