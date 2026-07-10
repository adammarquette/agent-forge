# ARCHITECTURE — AgentForge Clinical Co-Pilot

**Author:** Adam Marquette
**Date:** 2026-07-07 (system architecture) · 2026-07-09 (EHR-launch plan, §9)
**Status:** Draft for MVP (Architecture Defense / Tuesday)
**Companion docs:** [`AUDIT.md`](./AUDIT.md) (findings this plan responds to) · [`USERS.md`](./USERS.md) (target user & use cases) · [`PRD.md`](./PRD.md) (source of truth for requirements) · `agent-forge-copilot` repo's `CONTRIBUTING.md` / `docs/openapi/sidecar-api.yaml` / `docs/interfaces/ICD.md` (sidecar-side engineering standards and interface contract, external repo)

> **Trace requirement.** Per the case study, `USERS.md` is the source of truth. Every agent capability in this document is tagged `[→ UC-n]` and must resolve to a use case there.

---

## 1. High-Level Summary

The Clinical Co-Pilot is built as a **standalone .NET sidecar service**, not as code inside OpenEMR's PHP request path. It runs as its own container on the same Docker network as OpenEMR and talks to it **exclusively over OpenEMR's OAuth2-secured FHIR/REST API** — never against the database directly. This is the single most important architectural decision, and it falls straight out of the audit: OpenEMR already ships a mature auth stack, an ACL engine, and a checksummed audit log, but its access control is by feature, not by patient panel, and it will not automatically log a new service account's reads. Running as an API client under a **per-clinician SMART-on-FHIR token** means the agent inherits the user's existing authorization and every read flows through OpenEMR's audit path — the trust boundary is enforced by infrastructure we already validated, not reimplemented.

**Language & libraries.** C#/.NET 10 with a typed **Refit** client for the FHIR/REST surface, **Polly** for transient-fault resilience, `Microsoft.Extensions.Options` for configuration, and `Microsoft.Extensions.Logging.Abstractions` for provider-agnostic logging. Testing is **xUnit + FakeItEasy + FluentAssertions**. There is no WebSocket/SignalR layer; the sidecar is request/response REST.

**Agent surface.** A **session-based REST API** (`POST /conversations`, `POST /conversations/{id}/messages`, `GET /conversations/{id}`, plus separate `/health` and `/ready`). Server-side sessions carry multi-turn context so the client only sends the next message [→ UC-1]. The agent orchestrates LLM tool-calls against a small set of typed tools that wrap FHIR reads (demographics, problems, allergies, medications, recent labs, encounters). The entry point into that surface — how a clinician actually opens a session in-context — is specified in §9.

**Verification.** Every response passes a post-generation gate before the clinician sees it. Two checks: **source attribution** — each factual claim must carry a citation to a FHIR resource id, or it is withheld — and **domain-constraint enforcement** — dosage/threshold and interaction rules that reject or flag unsupported statements. This directly answers the audit's data-quality risks: medications live in two OpenEMR tables and clinical codes are frequently blank, so the agent reconciles both medication sources and never promotes a free-text drug string to a coded fact.

**Observability.** A correlation ID is assigned per agent invocation and stamped on every log line, tool call, and LLM interaction, so a full trace reconstructs from logs alone. Request counts, error rate, p50/p95 latency, tool-call/retry counts, verification pass/fail, and token cost are surfaced on a dashboard with alerts.

**Key tradeoffs.** (1) *Sidecar over in-process module* — a network hop and a second deployable, bought in exchange for isolation, an independent audit identity, and a non-PHP toolchain. (2) *FHIR/REST over direct DB* — higher per-read latency than SQL, bought in exchange for automatic ACL + audit enforcement and a stable contract. (3) *Session state over stateless* — a session store to operate, bought in exchange for a thin client and clean multi-turn context. (4) *PHI to an LLM* — the central compliance obligation: minimum-necessary fields only, every disclosure logged, under an assumed BAA, demo data only.

**Biggest open risk:** there is currently almost no data to reason over (audit §5.2 — one placeholder patient). Loading realistic demo data is prerequisite to everything downstream.

---

## 2. Deployment & Runtime Topology

```
┌────────────────────────── Docker network (dev-easy) ──────────────────────────┐
│                                                                                │
│   Clinician browser ──HTTPS──▶  OpenEMR (Apache/PHP)  ◀──REST/FHIR (OAuth2)──┐  │
│         │                          │  :80/:443            over internal net  │  │
│         │  Co-Pilot UI (custom     │                                         │  │
│         │  module iframe/panel)    ▼                                         │  │
│         └───────────────▶  Sidecar (.NET 10)  ──────────────────────────────┘  │
│                              :8080  /conversations  /health  /ready            │
│                                │            │                                   │
│                                ▼            ▼                                   │
│                        LLM provider     Observability backend                  │
│                        (BAA, TLS)       (traces/metrics/logs)                   │
│                                                                                │
│   MySQL/MariaDB ── owned by OpenEMR only; sidecar has NO direct DB access ──    │
└────────────────────────────────────────────────────────────────────────────────┘
```

- **Sidecar** is a separate container/image with its own lifecycle, scaled independently of OpenEMR.
- **UI integration** is via OpenEMR's first-class **custom module** mechanism (`interface/modules/custom_modules/`, precedent: `oe-module-*`), surfacing the co-pilot as a panel. The module only bootstraps the UI and passes the user's SMART token; it holds no agent logic. §9 specifies this module concretely.
- **No direct database access** from the sidecar — this is a hard rule, so that ACL and audit are never bypassed (audit §2.5).

---

## 3. Data Access & Authorization Boundaries

### 3.1 Identity flow (trust boundary)
1. Clinician authenticates to OpenEMR normally (existing session/SSO/MFA).
2. The co-pilot module obtains a **SMART-on-FHIR access token for that user** (authorization-code flow) from `/oauth2/default/authorize` + `/oauth2/default/token`; the sidecar is a **registered OAuth2 client** (`/oauth2/default/registration`).
3. The sidecar calls FHIR **as the clinician**, so OpenEMR enforces the clinician's ACL and records the read under their identity.

**Boundary the agent must add (audit §2.2):** OpenEMR does not, by default, restrict a clinical user to only their assigned patients. The sidecar therefore enforces a **patient-scoping check** on every tool call — the requested `pid`/patient id must be on the clinician's panel/schedule — and refuses otherwise. Authorization is modeled with typed principals, not bare ints (see `CONTRIBUTING.md`).

### 3.2 Tools (typed FHIR reads) — Refit interfaces are the contract
| Tool | OpenEMR FHIR resource | Notes |
|---|---|---|
| `GetDemographics` | `Patient` | select minimum-necessary fields |
| `GetProblems` | `Condition` | |
| `GetAllergies` | `AllergyIntolerance` | |
| `GetMedications` | `MedicationRequest` **+** reconcile `lists[type=medication]` | two-source reconciliation (audit §5.1) |
| `GetRecentLabs` | `Observation` (category=laboratory) | bounded by date |
| `GetEncounters` | `Encounter` | |

Base FHIR: `https://<host>/apis/default/fhir`. Full endpoint/scope/auth detail lives in the ICD (`agent-forge-copilot` repo).

---

## 4. Verification System

Verification is a **gate after generation, before delivery** — the model may draft, but nothing reaches the clinician unverified.

- **Source attribution.** The agent must return claims as `{statement, sourceResourceType, sourceResourceId}`. Any claim without a resolvable citation is withheld and surfaced as "not found in record," never asserted. This is the primary hallucination defense.
- **Domain-constraint enforcement.** A rules layer checks dosage/threshold sanity and known interaction/allergy conflicts against the retrieved data; violations are flagged or the response is rejected. Rule set and its known gaps are documented alongside the code.
- **Data-quality awareness.** Because codes are often blank and meds are fragmented (audit §5), the verifier distinguishes *coded* facts from *free-text* facts and reconciles both medication sources before answering.
- **Known limitations** (documented, not hidden): verification confirms *grounding in the record*, not clinical correctness of the record itself; free-text-only data limits code-based checks; the rule set is finite.

**Failure behavior:** missing record → explicit "not on file"; tool failure → the agent degrades gracefully, states which data it could not retrieve, and never fabricates to fill the gap (audit "Failure Modes").

---

## 5. Observability

- **Correlation ID** per `POST …/messages` invocation, propagated to every log entry, tool call, and LLM request (engineering requirement).
- **Structured logging** via `ILogger` with PSR-style context objects; **no PHI in logs** (field-level redaction).
- **Metrics/traces:** request count, error rate, p50/p95 latency, tool-call and retry counts, verification pass/fail rate, tokens + cost per request.
- **Dashboard + ≥3 alerts:** p95 latency over threshold, error rate over threshold, tool-failure rate — each with a documented on-call response.
- `/ready` performs **real dependency checks** (OpenEMR reachable, LLM reachable, observability backend reachable), distinct from `/health` liveness.

---

## 6. Risks Carried From the Audit → Mitigations

| Audit finding | Architectural mitigation |
|---|---|
| §5.2 Empty demo DB (1 placeholder patient) | Load realistic demo data before eval; treat as prerequisite gate |
| §2.1/§2 Dev credentials, exposed side-channel ports | Harden before any public deploy; sidecar never uses DB creds |
| §2.2 Access control not per-patient | Sidecar enforces per-patient scoping on every tool call |
| §6.1 Agent reads not auto-audited | Every tool call runs under the clinician's token → OpenEMR audits it; sidecar also logs a disclosure event |
| §6.4 PHI to LLM (BAA) | Minimum-necessary fields, per-disclosure logging, assumed BAA, TLS, demo data only |
| §5.1 Two medication sources, blank codes | Reconcile both sources; separate coded vs free-text facts in verification |
| §3 No app cache; latency from LLM not DB | Cache the assembled patient snapshot per encounter; parallelize tool fetches |
| §8 EHR-launch not yet implemented (see §9) | Ship `oe-module-agentforge`, deploy the fork, then switch the sidecar to consume the module-originated launch |

---

## 7. Technology Stack (finalized)

| Concern | Choice | Min version |
|---|---|---|
| Language / runtime | C# / .NET | 10.0 |
| OpenEMR REST/FHIR client | Refit | 7.0.0 |
| Resilience (retries, timeouts, circuit breaker) | Polly | 8.6.5 |
| Configuration | Microsoft.Extensions.Options | (9.x) |
| Logging abstraction | Microsoft.Extensions.Logging.Abstractions | (9.x) |
| Unit testing | xUnit | 2.9.0 |
| Mocking | FakeItEasy | 8.0.0 |
| Assertions | FluentAssertions | 6.12.0 |

LLM provider/orchestration SDK is a deliberate open decision (candidates: `Microsoft.Extensions.AI` for .NET-native tool calling, or a provider SDK) to be fixed in Early Submission; the verification and observability layers are provider-agnostic by design. Versions are pinned centrally in the sidecar repo's `Directory.Packages.props`.

---

## 8. Open Decisions

- LLM provider + orchestration SDK and the associated cost model (per the AI Cost Analysis deliverable).
- Session store (in-memory for MVP vs. distributed for scale).
- Exact domain-constraint rule set and its data sources.

---

## 9. EHR-Launch Implementation Plan (AF-1 / AF-2 / CP-1)

*(Concrete build plan for the launch entry point sketched in §2/§3.1. Tracked as three linked GitLab
issues: `agent-forge#1` (AF-1), `agent-forge#2` (AF-2), `agent-forge-copilot#34` (CP-1) — filed
2026-07-09, each `Blocked by` the previous per the free-tier issue-linking convention.)*

### 9.1 Goal

OpenEMR must expose an in-chart **SMART EHR launch** entry point, and the sidecar must consume that
launch rather than self-starting. The work is split across two repos:

- **agent-forge** (this repo) owns the OpenEMR-side module and the fork's Railway deployment.
- **agent-forge-copilot** owns the sidecar/BFF launch consumer.

Dependency chain: **AF-1** (ship `oe-module-agentforge`) blocks **AF-2** (deploy the fork with the
module baked in) blocks **CP-1** (sidecar consumes the module-originated launch). The OpenEMR-side
work is the upstream blocker for the sidecar integration work.

### 9.2 Launch flow

1. A clinician opens the patient chart in OpenEMR.
2. The custom module registers a launch affordance in the chart UI (via the `RenderEvent` hook
   already used elsewhere in OpenEMR's own `SmartLaunchController`).
3. The module performs a SMART EHR launch with `launch` and `iss`, scoped to the current patient.
4. The sidecar/BFF receives the launch, completes auth-code + PKCE, and resolves `launch/patient`.
5. The sidecar is then scoped to the selected patient with no sidecar-side patient selection —
   consistent with §3.1's "clinician's own OAuth identity, never a service account."

### 9.3 OpenEMR-side responsibilities (AF-1, AF-2)

- A custom module under `interface/modules/custom_modules/oe-module-agentforge`, installable via
  the OpenEMR module manager (`oe-module-*` precedent).
- A visible, ACL-scoped launch action in the patient chart.
- A SMART EHR launch target that passes the clinician's identity and patient context to the sidecar,
  with no PHI in URLs beyond unavoidable resource identifiers.
- A deployable fork image (Railway) that includes the module and any seeded demo data required for
  integration testing — see `docs/DEPLOYMENT.md`.

### 9.4 Sidecar-side responsibilities (CP-1, tracked in `agent-forge-copilot`)

- Expose the launch endpoint the module targets; validate `iss` against the configured OpenEMR base.
- Complete auth-code + PKCE against OpenEMR; resolve `launch/patient` as the source of patient
  context (no sidecar-side patient selection in the EHR-launch path).
- Keep tokens server-side only; preserve correlation-id/disclosure logging (§5).

### 9.5 Current implementation status

The OpenEMR fork has an initial module scaffold at
`interface/modules/custom_modules/oe-module-agentforge`:

- `AgentForgeLaunchService::buildLaunchUrl()` — a launch URL builder emitting `launch`, `iss`, `aud`,
  and optional `patient` parameters, covered by a unit test.
- `Bootstrap` — registers on OpenEMR's `RenderEvent::EVENT_SECTION_LIST_RENDER_AFTER` (the same hook
  `SmartLaunchController` uses) and renders a "Launch AgentForge" button using a real
  `SMARTLaunchToken`.
- `public/launch.php` — a redirect stub from the module's launch URL to the sidecar.

**Not yet done:** the button is not wired into a live patient-chart render path end-to-end against a
running instance; the module is not yet installed/enabled on a deployed target; AF-2 (fork
containerization/Railway deploy, §11) has the Dockerfile and Railway config in place but hasn't been
validated with this module baked in; CP-1 (sidecar consumption) is untouched from this repo's side.

### 9.6 Alignment notes

This matches the PRD's core trust requirements: patient-scoped context must be explicit and
validated (FR-CHAT-3); the clinician's own OAuth identity is used, not a service account
(FR-AUTH-1); the launch path must be auditable and observable (FR-OBS-1); the system must degrade
clearly if the module, deployment, or sidecar launch path is unavailable (NFR-REL-1).

---

*Companion `AUDIT.md` §8 tracks the residual risk on this dependency chain.*
