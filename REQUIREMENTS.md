# AgentForge Requirements

This document defines who AgentForge is for, what it must do and how well. It covers the core copilot
(sections 1–17), the document and evidence pipeline (section 18) and the security platform (sections 19–20).
Every requirement has a stable ID (`FR-` functional, `NFR-` non-functional, `UC-` use case), and the code
and tests cite these IDs and section numbers, so both are kept stable here.

Where a requirement is only partly met by the shipped code, the gap is stated next to it as **As built**.
The metrics that measure these requirements (M1–M5 and the rest) are defined in [METRICS.md](METRICS.md).

---

## 1. Users

### 1.1 Who

The primary user is an established-practice **outpatient cardiologist** running a clinic day of about
16–20 scheduled patients. Roughly two-thirds are longitudinal follow-ups last seen weeks or months ago, with
chronic cardiac conditions: atrial fibrillation on anticoagulation, heart failure with reduced ejection
fraction (HFrEF) on guideline-directed therapy, coronary artery disease after a stent on antiplatelet
therapy, hypertension and hyperlipidemia.

They have deep domain expertise, read a chart faster than most software loads, and have near-zero
tolerance for a tool that is confidently wrong. The bar to earn a single click is high.

**The defining constraint is the ~90 seconds between rooms.** The cardiologist walks out of Room A, glances
at the schedule and has only a name and a reason for visit in mind. The Room B patient is already roomed,
vitals taken, waiting. There is no time to read the last three notes, the lab flowsheet and the medication
list separately. Everything the copilot does must fit inside that window, on a shared hallway workstation
or tablet.

| Moment | What happens |
|---|---|
| T-minus ~20 s | Hallway; a name and a reason for visit; no chart open. |
| T-zero | One action opens the copilot already scoped to the Room B patient, launched from the OpenEMR chart. No patient picker. |
| T+0 to ~75 s | Reads one prioritized, source-cited brief; asks at most one follow-up. |
| Exit | Walks in oriented: opens with the right question, checks the med list against what the patient says, acts on any safety flag before writing today's plan. |
| Pre-clinic sweep | The same user runs the brief across the day's schedule before clinic to see who needs pre-reading. Tolerates a few seconds more latency. |

**What the brief must surface**, prioritized and each item cited to a record:

1. **Interval events** since the last visit: ED visit, hospitalization, new echo and its EF, device
   interrogation, new outside records.
2. **Medication changes**: started, stopped or titrated, and any adherence signal.
3. **New or out-of-range cardiac labs**: INR against the range for its indication, potassium and creatinine
   on ACE inhibitor / ARB / diuretic, lipids on statin, BNP / NT-proBNP in heart failure.
4. **Active safety flags**: a domain-constraint hit (QT-prolonging combination, renally contraindicated dose,
   INR out of range) that must not be missed.

The brief leads with the one or two things that change today's plan. Anything the copilot cannot ground in
the record is reported as a gap, never inferred.

This user constrains everything downstream: the window sets the latency budget (NFR-PERF-1), "trust at a
glance" makes source attribution mandatory (FR-VERIF-1), the safety-flag need drives constraint checking
(FR-VERIF-2), and the shared hallway workstation makes "who is asking?" a real question (FR-AUTH-1..4).

### 1.2 Who it is not for

- Inpatient hospitalists or rounding teams (OpenEMR is ambulatory).
- ED clinicians doing intake on unknown patients (the value depends on an existing longitudinal record).
- Patients, students, or anyone wanting generic medical Q&A.
- Coding and billing staff.

### 1.3 Secondary users

- **Cardiology fellow**: supervised, may have narrower access.
- **Clinic nurse / medical assistant**: touches the chart for intake, vitals and med reconciliation with
  different entitlements.

They exist so that authorization is a real, enforced question. **As built**, neither is served: the
copilot only admits the provider on the patient's appointment for the current clinic day, and a fellow or
nurse is never that provider, so both are refused at launch (see FR-AUTH-2).

### 1.4 What "useful" means for this user

- **Trustable at a glance**: every claim cited; no need to check the tool against the chart.
- **Fits the window**: the answer arrives within the latency budget, or it loses to reading the chart.
- **Prioritized**: leads with what changes today's plan.
- **Honest under failure**: says "I can't verify that" rather than guessing.

---

## 2. Summary

A cardiologist has about 90 seconds to reconstruct a patient they may not have seen in three months. The
AgentForge copilot is a conversational agent embedded in OpenEMR that does that reconstruction and surfaces
only what is relevant, with **every claim traceable to a source in the patient's record**. It is not a
generic medical chatbot: it knows this patient, and it refuses to state as fact anything it cannot ground in
the chart.

The design thesis is **trust under failure**: source attribution, domain-constraint checking,
authorization, graceful degradation and observability are built in, not added later.

---

## 3. Problem

Outpatient cardiology is longitudinal and data-dense. Between visits INR drifts, potassium and creatinine
move on ACE inhibitors, medications are titrated, and events happen. The problem is not missing data but
too much data, poorly prioritized, at the moment the clinician has the least time.

Why this is hard:

- A confidently stated hallucination is a patient-safety event. "On metoprolol 50 mg BID" must be true and
  cited, or not said.
- Records conflict or go stale. The copilot must show the uncertainty rather than paper over it.
- Several roles touch the chart, so no requester can be assumed trusted.

Why an agent and not a static summary screen: such a screen shows everything and prioritizes nothing, and cannot answer a
follow-up. The need is multi-turn, tool-calling synthesis with memory ("What changed?" → "Is her INR in
range?" → "Why did we stop the diltiazem?").

---

## 4. Goals and non-goals

### 4.1 Goals

| ID | Goal |
|---|---|
| G1 | Reconstruct a patient's current state in seconds, prioritized and source-cited. |
| G2 | Every asserted clinical fact is attributable to a specific record or explicitly flagged as unverified. |
| G3 | Enforce cardiology domain constraints (dosing, ranges, interactions, contraindications) on responses. |
| G4 | Enforce role- and relationship-based authorization on all patient-data access. |
| G5 | Observable and evaluable: every request traceable, every regression catchable. |
| G6 | Degrade gracefully and visibly when tools fail or data is missing. |

### 4.2 Non-goals

| ID | Out of scope |
|---|---|
| **NG1** | Diagnosis, treatment recommendations or autonomous orders. The copilot **surfaces and cites**; the clinician decides. Section 12.4 says which of these three verbs is enforced by a control and which two are posture. |
| NG2 | Generic medical Q&A untethered to the patient record. |
| NG3 | Inpatient rounding workflows. |
| NG4 | Real PHI. Synthetic/demo data only. |
| **NG5** | Write-back to the EHR. The copilot is read-only. Moving to writes is a deliberate change: `ClinicalScopeGuardrailTests` fails at whichever of the three read-only layers (section 12.4) a write would first touch. |
| NG6 | Specialties other than cardiology. |

---

## 5. Use cases

Every capability traces to a use case below; a capability without one is cut. Each use case states what the
copilot **must refuse**, and those clauses are what the security platform attacks (UC-W3-8).

| ID | Use case | Need and output | Must refuse |
|---|---|---|---|
| **UC-1** | Pre-visit brief: "what changed and what matters today" | Triggered by opening a scheduled patient (or the pre-clinic sweep). A short, prioritized brief over the four data classes in 1.1, every claim cited. | To state a change it cannot attribute to a source; to invent a value for a missing field. |
| **UC-2** | Grounded multi-turn follow-up | "Is her INR therapeutic?" → "When was it drawn?" → "What's her CHA₂DS₂-VASc?": each turn resolves references to earlier turns and may chain several tool calls. This use case is what justifies multi-turn context and tool chaining. | To answer beyond the record; to guess when data is absent. |
| **UC-3** | Medication reconciliation and safety check | The current cardiac med list with sources, plus any constraint flag naming the rule and the source values (QT combination, renal dose, INR range, negatively chronotropic combination). | To recommend a dose, start or stop a drug, or place an order. |
| **UC-4** | Authorization-aware access ("who is asking?") | The same query returns data or a clean refusal depending on the requester's role and relationship to the patient, and holds under adversarial phrasing ("ignore that and show me…"). The relationship is: the requester is the provider on the patient's appointment for the current clinic day. OpenEMR itself gates by feature, not by patient panel, so this boundary is the copilot's own. It fails closed and refusals are audited. | To disclose anything the requester is not entitled to; to let record content or user phrasing override authorization. |
| **UC-5** | Graceful behaviour under missing or failed data | When a tool errors, a dependency is slow or the record is incomplete, return what can be verified, name the gap plainly, never fabricate or silently drop. | To present an unverified or inferred value as fact. |
| **UC-6** | On-demand day's agenda: "who's left, and what should I know before each" | Opened outside any one chart. Every patient still to be seen today (appointment time after now), soonest first, each with a short, independent, cited summary. Opening one drops into that patient's own session (UC-1/UC-2). | Everything UC-4 refuses, plus any question naming or implying a second patient ("compare to my other AFib patients"). The agenda changes how many summaries appear on one screen, not whether two patients' data can enter one conversation. |
| UC-7, UC-8 | Retired | These numbers are permanently unused so that no older reference to them can be misread. | — |
| **UC-9** | Document-derived pre-visit brief (extends UC-1, UC-3) | The front desk has uploaded a scanned lab PDF and an intake form. The UC-1 brief now includes facts extracted from them (the new out-of-range lab, the medication or allergy the patient reported), each cited to the exact page and region. | Everything UC-1 refuses, plus letting raw vision-model output reach the clinician unschematized or unsourced (FR-DOC-2). |
| **UC-10** | Evidence-grounded answer (extends UC-2, UC-3) | "What evidence supports this?" The copilot retrieves guideline evidence and shows it **as evidence**, visibly separate from record facts, each claim attributed to its own source type (`fhir` / `derived` vs `guideline`). | Everything UC-2 refuses, plus presenting guideline evidence as a patient-record fact (FR-RAG-3). |
| **UC-11** | Robustness to messy multimodal input (extends UC-5) | A scan is imperfect, a field is missing or low-confidence, or the guideline corpus has nothing relevant. The answer stays useful and honest, with explicit "unavailable" or low-confidence markers. | Everything UC-5 refuses, applied to extraction confidence and retrieval misses (FR-DOC-5, FR-EVAL-W2-4). |

**Capability → use case.**

| Capability | Justified by |
|---|---|
| Conversational, patient-scoped interface | UC-1, UC-2 |
| Multi-turn context | UC-2 |
| Tool chaining | UC-2, UC-3 |
| Prioritized synthesis across sources | UC-1 |
| Source attribution on every claim | UC-1, UC-2, UC-3 |
| Domain-constraint safety flags | UC-3 |
| Role/relationship authorization below the model | UC-4 |
| Graceful degradation and uncertainty signalling | UC-5 |
| Day's agenda (one isolated summary per patient, never a cross-patient session) | UC-6 |
| Document ingestion, extraction and click-to-source | UC-9, UC-11 |
| Guideline retrieval, kept separate from record facts | UC-10 |

---

### 5.2 Secondary users

The roles the authorization requirements are written against: the attending cardiologist (the primary
user, §1.1), the cardiology fellow and the clinic nurse or medical assistant (§1.3). Only the provider on
the patient's appointment for the current clinic day is admitted; the other roles are refused at launch
(FR-AUTH-2).

## 6. Product scope

The copilot is a **read-only conversational agent** surfaced inside the OpenEMR patient context, backed by
a separate .NET service (the sidecar). In scope: the chat and agenda views, patient-data tools over OpenEMR's
FHIR API, the verification layer, authorization, observability, the eval suite, and the document and
evidence pipeline of section 18. Out of scope: write-back, diagnosis and orders, other specialties, real
PHI.

---

## 7. Functional requirements

Priorities: **Must** / **Should**. "AC" is the acceptance criterion.

### 7.1 Conversational interface

- **FR-CHAT-1 (Must) — Multi-turn agent.** The agent keeps context across turns within one patient session.
  *AC:* in a three-turn exchange, turn 3 resolves a reference from turn 1 ("she", "that lab"). Serves UC-1,
  UC-2.
- **FR-CHAT-2 (Must) — Tool invocation and chaining.** The agent fetches patient data through defined tools
  and chains them when needed. *AC:* a question needing two or more tool calls gets a correct, cited answer,
  and the chain is visible in the logs.
- **FR-CHAT-3 (Must) — Patient-scoped context.** The agent works within one patient's record at a time and
  cannot pull another patient's data into the conversation. *AC:* "compare to your other AFib patients" is
  refused or scope-blocked, and logged. Serves UC-4, UC-6.
- **FR-CHAT-4 (Should) — Uncertainty communication.** When records conflict or are stale, the agent shows
  the conflict rather than resolving it silently. *AC:* two disagreeing medication sources are both shown
  with dates and sources.

### 7.2 Data access and tools

- **FR-DATA-1 (Must) — Canonical tool contracts.** Every tool has a strict input/output schema that is the
  source of truth. *AC:* invalid input is rejected at the boundary with a structured error; schemas are
  exported. See also NFR-CONTRACT-1.
- **FR-DATA-2 (Must) — Core cardiology data tools.** At minimum: demographics, problem list, medications,
  allergies, labs, vitals, encounters/notes and procedures, each returning source identifiers (FHIR
  resource ids) usable for citation. **As built:** no tool reads procedures. The FHIR client has a procedures
  read with no caller, although both SMART launches still request `Procedure.read`.
- **FR-DATA-3 (Must) — Access path.** Use OpenEMR's FHIR R4 API over OAuth2 / SMART; fall back to a
  read-only, least-privilege database path only where FHIR is insufficient, documented in
  [ARCHITECTURE.md](ARCHITECTURE.md). The shipped copilot uses FHIR only.
- **FR-DATA-4 (Should) — Semi-structured extraction.** Values that live in documents rather than fields
  (ejection fraction from an echo report, device results) are cited to the source document and labelled
  *derived*. **As built:** holds for facts from the document pipeline (citation `sourceType: derived`). For an
  EF read from a report returned by `get_documents`, the citation is `[DiagnosticReport/<id>]` or
  `[DocumentReference/<id>]`, and the tool description (asks for a "derived" label) and the system prompt
  (asks for no provenance label) disagree. See [PROMPTS.md](PROMPTS.md).

### 7.3 Verification (the trust core)

- **FR-VERIF-0 (Must) — Mandatory gate.** Every response passes through the verification layer before it
  reaches the user. **As built:** one path does not: the deterministic fallback (section 13.1, LLM failure
  rows) returns raw, cited tool data without passing the verifier, by design.
- **FR-VERIF-1 (Must) — Source attribution.** Every asserted clinical fact must be attributable to a
  specific record; unattributable claims are not stated as fact. *AC:* no response asserts a medication, lab
  or problem without an attached, resolvable source id. **As built:** the verifier suppresses a line whose
  citation does not resolve, and suppresses an *uncited* line only when it contains one of fourteen
  `ClinicalFactKeywords`. An uncited medication name or problem phrased without a keyword ships; the eval
  case `answer-m1-keyword-boundary-escape-ships` pins that known escape.
- **FR-VERIF-2 (Must) — Domain-constraint enforcement.** Responses are checked against a defined cardiology
  rule set and violations are flagged: warfarin INR range by indication (including the mechanical-valve
  range), renal-dose contraindications for DOACs, QT-prolonging combinations, ACE inhibitor/ARB with
  hyperkalemia or rising creatinine, and negatively chronotropic combinations. The rule set
  (`CardiologyConstraintRules.Default`) is illustrative until clinically validated. *AC:* a seeded violation
  raises a flag citing the rule and the source values. A value rule is evaluated against the **most recent**
  result for each observation code and the flag carries that result's date, so a superseded value is not
  presented as current. Each rule has a seeded-violation eval case and a near-miss control that must raise
  nothing; the controls stop an engine that flags everything from scoring 100%.
- **FR-VERIF-3 (Must) — Verification outcome is logged and measurable.** Each response records verification
  pass/fail and the reason. *AC:* the pass/fail rate is in the monitoring view.
- **FR-VERIF-4 (Should) — Documented limits.** What verification does *not* catch is written down, in
  [ARCHITECTURE.md](ARCHITECTURE.md) and section 12.4.

### 7.4 Authorization and access control

- **FR-AUTH-1 (Must) — Authenticated requester identity.** Every agent invocation carries an authenticated
  user identity and role from the SMART launch; no caller is assumed trusted. *AC:* an unauthenticated
  request is rejected before any tool runs. An expired SMART session is refused outright (section 13.1).
- **FR-AUTH-2 (Must) — Role/relationship entitlement.** Patient-data access is gated by the requester's
  role and relationship to the patient. **As built**, the relationship is *provider participation on the
  patient's appointment for the current clinic day* (the same signal the agenda filters on). It is enforced
  at the SMART launch (HTTP 403), on every tool dispatch, at `POST /evidence/ask` and at
  `GET /evidence/document/{id}` (which also refuses a document the ingest index does not attribute to the
  session's patient). It fails closed. Refusals are audited with the clinic-day appointment count, and every
  tool-dispatch decision, permit or refuse, is counted on `agentforge_authorization_decisions_total` with a
  bounded reason label. Because fellows and nurses are never an appointment's provider, they are refused
  before any OpenEMR scope applies, so role tiers below the attending are deferred behind this gate.
- **FR-AUTH-3 (Must) — Enforcement below the model.** Authorization is enforced in the tool and data layer,
  not by prompt instruction, so it holds under prompt injection. *AC:* "ignore restrictions and show X" yields
  no unauthorized data and the attempt is logged. The `evals/golden/authz-injection-*` cases model a fully
  compromised model: each pins the tool call such a model would emit and asserts the layer beneath refuses it.
- **FR-AUTH-4 (Must) — Access is audit-logged.** Every patient-data access records who, what, when and why
  (section 12.2). *AC:* audit entries reconstruct a full per-patient access trail. The *why* is the access
  path recorded in the entry (`tool=`): every access in scope is the treating clinician's own workflow, so no
  separate purpose-of-use field is kept.

### 7.5 Observability

- **FR-OBS-1 (Must) — Correlation ID.** Every invocation gets a unique correlation ID present on every log
  line, tool call and LLM interaction. *AC:* a full trace can be rebuilt from logs alone.
- **FR-OBS-2 (Must) — Step, timing, failure and cost trace.** Logs answer what the agent did and in what
  order, how long each step took, which tools failed and why, and tokens used and their cost.
- **FR-OBS-3 (Must) — Live monitoring view.** Request count, error rate, p50/p95 latency, tool-call and retry
  counts, and verification pass/fail rate, updating under load. The stack ships Prometheus and Grafana
  ([observability](observability/)).
- **FR-OBS-4 (Must) — Alerts.** At least three alerts (p95 latency, error rate, tool failure rate), each
  with a documented meaning and response, and at least one demonstrably triggerable.

### 7.6 Evaluation

- **FR-EVAL-1 (Must) — Boundary / invariant / regression suite.** Every eval case exercises a boundary
  (missing data, malformed input, empty record), an invariant (claims cite a source) or a known regression
  risk, and names the failure mode it guards. *As built:* every golden case in [evals/golden](evals/golden/)
  has a required `guards` string; the loader refuses a case without one, and the console gate prints it
  beside a failing case.
- **FR-EVAL-2 (Must) — Adversarial and authorization cases.** Cases attempt to extract data the requester is
  not entitled to, and to inject instructions. *AC:* they assert refusal and logging. *As built:* 13 `authz-*`
  cases (eight role-confusion, five injection), four of which are **permits**, because a suite where
  everything is refused would pass while refusing everyone.
- **FR-EVAL-3 (Should) — Runnable and repeatable.** One command produces pass/fail results:
  `dotnet run --project tests/AgentForge.Evals -- evals`.
- **FR-EVAL-4 (Should) — Runnable API collection.** A Bruno collection
  ([tests/bruno-collection](tests/bruno-collection/)) covers the core endpoints so an engineer can run any
  workflow without reading source.

---

## 8. Non-functional requirements

- **NFR-PERF-1 (Must) — Interactive latency.** p95 ≤ 26 s end-to-end for a single-patient brief turn at up
  to 50 concurrent users. The time goes to sequential LLM tool-calling and external API latency, not the
  sidecar's own compute (under 1% CPU at 50 users). The planned remedy is a *speed-vs-completeness* policy
  (return a fast verified core first, defer or stream the rest), not implemented. **As built:** the 26 s
  figure was measured on a run in which four of the five FHIR tools failed authorization, so it describes a
  partial brief; a full-brief re-measure has not been taken. See [METRICS.md](METRICS.md).
- **NFR-PERF-2 (Should) — Baseline profiles.** CPU, memory, latency and throughput baselines under load,
  recorded in [METRICS.md](METRICS.md).
- **NFR-PERF-3 (Should) — Load behaviour.** p50/p95/p99 and error rate at 10 and 50 concurrent users.
- **NFR-SEC-1 (Must) — PHI handling.** Encryption in transit and at rest, secrets from configuration never
  code, least-privilege access, and **no PHI in diagnostic logs, traces or the observability backend**. The
  one deliberate exception is the access-audit trail, which must name the patient (FR-AUTH-4); it is
  protected by access control and retention instead, and a production environment must route it to a
  separate, access-controlled sink ([DEPLOYMENT.md](DEPLOYMENT.md)).
- **NFR-SEC-2 (Must) — Prompt-injection resistance.** Record content (notes, documents) is data, never
  instructions; neither it nor user phrasing can override authorization or tool access. Covered by the
  FR-AUTH-3 and FR-EVAL-2 cases.
- **NFR-REL-1 (Must) — Graceful degradation.** Tool failure, incomplete records or unexpected model output
  produce a visible, predictable partial result, never a crash or a silent fabrication. Section 13.1 is the
  contract.
- **NFR-REL-2 (Must) — Health vs readiness.** Separate `/health` (process alive) and `/ready` (dependencies
  reachable). `/ready` genuinely checks OpenEMR, the LLM provider and the observability backend.
- **NFR-SCALE-1 (Should) — Scale reasoning.** How the design scales toward 300 concurrent clinical users and
  where it breaks first is documented in [ARCHITECTURE.md](ARCHITECTURE.md) and section 15.1.
- **NFR-A11Y — Accessible contrast.** Every text/background pair in the chat and agenda views meets WCAG AA
  (4.5:1) in both the light and dark themes. `ViewThemeTokenTests` checks every pair in
  `wwwroot/theme.css`.

### 8.1 Engineering and operational requirements

- **NFR-TRACE-1 (Must) — Correlation ID across boundaries.** The correlation ID propagates ingress → agent →
  each tool call → each LLM call → the observability backend, so every downstream entry for a request can be
  retrieved and ordered. Implements FR-OBS-1.
- **NFR-CONTRACT-1 (Must) — Strict schema contracts.** All tool inputs and outputs are defined by strict
  schemas that are the source of truth; a malformed tool input is rejected before execution with a
  structured error, and the rejection is logged.
- **NFR-HEALTH-1 (Must) — Separate liveness and readiness.** With any one dependency unreachable, `/ready`
  fails while `/health` still succeeds; `/ready` never returns 200 unconditionally. Each probe is bounded by
  `Readiness:ProbeTimeout`, and the response body names every check and its status.
- **NFR-PERF-4 (Must) — Load and stress baselines.** p50/p95/p99 latency and error rate at 10 and 50
  concurrent users, recorded with CPU, memory and throughput so later changes can be compared.

---

## 9. System overview

A dedicated agent service (the sidecar) runs beside OpenEMR behind one front door. The model never touches
data directly; it calls **typed tools** that wrap OpenEMR access, authorization checks and source-id capture.
Each turn:

1. **Ingress**: authenticated request with identity, role and patient context; correlation ID assigned.
2. **Authorization**: entitlement checked in the tool layer before any retrieval.
3. **Reasoning**: the model plans and calls tools; results carry source ids.
4. **Verification**: source attribution and domain constraints checked before release.
5. **Response**: a cited answer, or a visible partial result or refusal, with the trace logged.

Design and decisions: [ARCHITECTURE.md](ARCHITECTURE.md); the document pipeline:
[ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md); interfaces: [INTERFACES.md](INTERFACES.md).

---

## 10. Data and integration

- OpenEMR is ambulatory; the design follows that grain (NG3).
- **Structured, citable sources:** demographics, problems, medications, allergies, labs, vitals, encounters.
  These anchor attribution.
- **Semi-structured sources:** EF, echo findings and device interrogations often live in documents and need
  extraction with an explicit *derived* label (FR-DATA-4, section 18).
- **Data quality:** demo data is uneven (many demographics-only patients, missing fields, stale values,
  possible duplicates). These are treated as agent failure modes, not edge cases, and feed UC-5 and the eval
  boundary cases. A synthetic cardiology cohort is seeded for demo and evaluation.

---

## 11. Deployment and infrastructure requirements

- **Containers.** The OpenEMR fork and the sidecar ship as images and come up together from
  [docker-compose.yml](docker-compose.yml) behind one nginx front door ([reverse-proxy](reverse-proxy/)).
  The images that run locally are the images that would run in production.
- **Environments.** One runnable stack anyone can bring up from the repository, plus hosted environments
  defined as code in [.railway/railway.ts](.railway/railway.ts). Configuration lives in source and image
  tags are pinned to an explicit `-sha-<12>` build by default, so the environments are the same system. See
  [DEPLOYMENT.md](DEPLOYMENT.md).
- **Operations.** `/health` and `/ready` (NFR-REL-2), automated build of agent updates, a documented rollback
  path, and the monitoring views and alerts (FR-OBS-3/4) wired to the deployed service.
- **HIPAA posture.** This software has only ever run on synthetic data, so no BAA was needed; the local
  stack runs over plain HTTP with development escape hatches, which is safe only because no PHI touches it.
  **With real PHI, the host carries the compliance burden:** no PHI may touch any environment, database, log
  or backup without a signed BAA. The production target is a compliance-capable environment **you supply**
  (a HIPAA-eligible host under BAA, or the practice's own OpenEMR environment) running these same images. What
  that environment must provide is a vendor-neutral capability contract, seam by seam, in
  [DEPLOYMENT.md](DEPLOYMENT.md).

---

## 12. Compliance and regulatory

### 12.1 HIPAA as an architectural constraint

Encryption in transit and at rest, least-privilege access, no PHI in diagnostic telemetry (the 12.2 audit
trail excepted, per NFR-SEC-1), and enforced authorization (FR-AUTH-1..4).

### 12.2 Audit logging

Every patient-data access and agent action is audit-logged (who, what, when, why, correlation ID) and
retained per policy. **As built**, the sidecar writes an `ACCESS AUDIT` record for every patient-data tool
call, every refusal, and every grant on the four reads outside the tool layer: `GET /patient`
(`patient_context`), `POST /evidence/ask` (`evidence_ask`), `GET /evidence/document/{id}`
(`evidence_document`) and the chat citation loader (`chat_document_citations`). Each record names the
clinician, the patient and the correlation ID. The record carries no timestamp of its own; *when* is the
timestamp the container runtime puts on the stdout line, so your log collector must keep it. *Why* is the
access path (FR-AUTH-4).

### 12.3 BAA implications of sending PHI to an LLM

Sending PHI to an LLM provider makes it a business associate. Production use requires a real, executed,
no-training BAA with the provider, and PHI minimization (send only what is needed) applies regardless.

### 12.4 Clinical-AI transparency and scope

**Scope guardrail (NG1).** The copilot surfaces and cites existing record data and does **not diagnose,
recommend treatment or place orders**. Keeping every statement independently checkable through its citation
keeps it in clinician-in-the-loop information retrieval rather than autonomous decision-making.

**Which of the three verbs is a control — the "third verb".** Only one of the three is enforced by code:

- **"Does not place orders" is a control.** Placing an order requires *calling* something, so it is decided
  by the action surface. `McpToolCatalog` is that surface and the dispatcher gates on it, so what the model is
  offered and what it can run are one list; every entry is a read. The FHIR client interface declares only
  `GET`. Every `<context>/<Resource>` scope either SMART launch registers is `.read`, so OpenEMR itself refuses
  a write the layers above somehow allowed. `ClinicalScopeGuardrailTests` pins all three layers; this is the
  bar NG5 has to clear.
- **"Does not diagnose" and "does not recommend treatment" are posture, not controls.** They live in the
  system prompt (`CardiologyProfile.SystemPrompt`) and nothing deterministic backs them. That is deliberate:
  no test separates surfacing a recorded diagnosis ("she has AFib [Condition/7]", which is UC-1 working) from
  making one, and a keyword filter for recommendations would suppress the reconciliation UC-3 depends on.
  [PROMPTS.md](PROMPTS.md) carries the reasoning.
- **Citations are the compensating control, and their limit is known.** A recommendation can be perfectly
  grounded: "Given the INR of 3.8 [Observation/123], hold the warfarin dose" passes verification and ships.
  Grounding constrains whether a claim is *supported*, not what kind of act it *performs*. The eval case
  `answer-m1-scope-escape-cited-recommendation-ships` asserts this ships, and the eval gate reports it beside
  M1 on every run.

**Transparency direction.** US rules for certified health IT (ONC HTI-1) require decision-support
interventions to expose plain-language "source attributes". AgentForge is not certified health IT, but its
design intent (source attribution, documented limits, observable performance) aligns with that direction.
This is posture, not a claim of certification.

---

## 13. Risks and mitigations

| ID | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | A hallucinated clinical fact reaches the clinician | Patient safety, trust | Verification as a hard gate; attribution invariant in eval (FR-VERIF-1, FR-EVAL-1) |
| R2 | Unauthorized exposure across roles or patients | HIPAA breach | Authorization below the model; adversarial eval cases (FR-AUTH-3, FR-EVAL-2) |
| R3 | Sparse or inconsistent data breaks answers | Wrong or empty output | Graceful degradation, missing-data evals, seeded realistic patients (NFR-REL-1) |
| **R4** | **Latency too high for the 90-second window** | **The clinician abandons the tool** | **Fast-core-then-defer policy; baselines and load tests (NFR-PERF-1..4)** |
| R5 | PHI leaks into logs or the observability backend | Compliance failure | Redacted telemetry; log-inspection checks (NFR-SEC-1) |
| **R6** | **Prompt injection through note or document content** | **Data exfiltration, unsafe output** | **Record content treated as data; injection eval cases (NFR-SEC-2, FR-AUTH-3)** |
| R7 | Domain constraints wrong or incomplete | False reassurance | Rules flagged illustrative until clinically validated; limits documented (FR-VERIF-2/4) |
| R8 | PHI/BAA gap at production | Legal exposure | Synthetic data only; documented production capability contract (section 11) |
| R9 | LLM and tool cost scales non-linearly | Unit economics break | Cost metered from the start; tiered analysis (section 15.1) |

### 13.1 Failure modes and graceful degradation

A clinical tool that crashes or silently fails is worse than no tool. This table is the operating contract
for behaviour under failure. Two invariants govern every row:

- **Never fabricate to fill a gap.** Missing or failed data is reported, never invented (FR-VERIF-1).
- **Never fail silently.** Every degradation is visible to the user *and* logged with the correlation ID
  (FR-OBS-1/2). Partial-but-honest beats complete-but-untrustworthy.

**Defaults:** per-tool timeouts and bounded retries with backoff; a per-request deadline sized to the
90-second window so the agent returns something verified rather than hanging; independent tool calls, so one
failure degrades one section; typed tool results (NFR-CONTRACT-1), so malformed output is caught at the
boundary.

| Failure condition (trigger) | Detection | Required behaviour | User sees | Logged | Guarded by |
|---|---|---|---|---|---|
| **Tool call fails / errors** | Non-2xx, exception, contract-invalid output | Skip that section, continue with the tools that succeeded, mark the section unavailable | Partial brief with an explicit "Labs unavailable — could not retrieve" banner on that section | Tool, error, correlation ID, retry count | FR-EVAL-1, NFR-REL-1 |
| **Tool slow / hits deadline** | Per-tool timeout; per-request deadline | Return the verified core now; mark the slow section deferred rather than blocking | Fast core answer plus a note that a section was deferred | Timeout event and latency | NFR-PERF-1, NFR-REL-1 |
| **LLM timeout / provider error** | Timeout, 5xx, SDK error | Bounded retry with backoff; then a **deterministic, non-LLM fallback**: raw source-cited tool data with no synthesis | "Summary unavailable right now — here is the source data", with citations | LLM error, retries, fallback-taken flag | NFR-REL-1, FR-EVAL-1 |
| **LLM rate-limit / quota** | HTTP 429 | Backoff and retry; if sustained, the same deterministic fallback; the alert fires | As above | 429 count, feeding the error-rate alert | NFR-PERF-1, FR-OBS-4 |
| **Missing / incomplete patient data** | Empty result, null required field | State the gap; return what is present; never infer the missing value | "No INR on file since 2025-11 — cannot confirm therapeutic range" | Which field or source was missing | UC-5, FR-CHAT-4, FR-EVAL-1 |
| **Conflicting records** (e.g. med list vs note) | Cross-source mismatch | Show **both** with dates and sources; never silently pick one | "Med list shows diltiazem; last note says discontinued 2026-05 — please verify" | The conflict and both sources | FR-CHAT-4, UC-1 |
| **Ambiguous query** | Low-confidence intent or several referents | Ask one focused clarifying question, or state the interpretation taken; never guess silently on a clinical question | "Do you mean her most recent INR or the trend?" | Ambiguity flag and resolution | UC-2, FR-EVAL-1 |
| **Unexpected / unparseable model output** | Schema validation fails | Reject it; one repair attempt; else the deterministic fallback | Core data without the malformed section | Validation error and a reference to the raw output | NFR-CONTRACT-1, NFR-REL-1 |
| **Claim can't be grounded** (verification fails) | The FR-VERIF-0 gate | Suppress the unattributable claim; return only what passed | Verified statements only; suppressed items noted | Verification failure and reason (FR-VERIF-3) | FR-VERIF-1, FR-EVAL-1 |
| **Authorization denied** | The FR-AUTH gate | Refuse cleanly; return no data; no leakage through error text or timing | "You don't have access to this patient's record" | The denied attempt (FR-AUTH-4) | UC-4, FR-EVAL-2 |
| **SMART session expired** (access token past its one-hour lifetime) | The session read, and the auth handler before any call is sent | **Refuse the turn outright; do not degrade around it.** Every source would fail the same way and no retry can succeed without a new launch, so a partial answer would be a brief written from an empty chart | "Session expired — please re-launch AgentForge from the patient's chart", in the view's own panel | A correlation-scoped refusal line and the `agentforge.expired_session_refusals` counter (exported as `agentforge_expired_session_refusals_total{surface}`); never the token, session key or expiry time | NFR-REL-1, FR-AUTH-1 |
| **Dependency down** (OpenEMR, observability backend, vector index, reranker) | `/ready` (NFR-HEALTH-1, NFR-HEALTH-W2-1), each probe bounded by `Readiness:ProbeTimeout`; the reranker check runs when `Cohere:ApiKey` is set | Fail readiness so traffic is not routed to a broken instance; show a clear unavailable state. **The same at boot as later:** a vector store that is down at startup leaves the sidecar running with `/ready` at 503, and `/ready` recovers without a restart. A dependency that was **never configured** degrades instead of failing: the observability backend, the vector index when no `AgentForgeData:ConnectionString` is set, and the reranker when no `Cohere:ApiKey` is set (decision D17 in [ARCHITECTURE.md](ARCHITECTURE.md)) | "The Copilot is temporarily unavailable", never a blank or a wrong answer | Readiness failure and which dependency; `/ready`'s body names every check and its status | NFR-REL-2, FR-OBS-4 |

**The line not crossed:** when the system cannot produce a trustworthy answer, the output is an honest "I
can't verify that right now", never a plausible guess.

**Eval coverage of this table: eight of the twelve rows.** Tool fails, tool slow, LLM timeout, rate limit,
missing data and unparseable output have `answer-m5-*` fault-injection cases (seven cases; the
unparseable-output row has a repaired and an exhausted-repair case). Ungrounded claims are covered by the
`answer-m1-*` cases and authorization denial by the `authz-*` cases. **Not covered by an eval case:**
conflicting records and ambiguous query (they need an answer-composition judge), dependency down (a `/ready`
behaviour with no eval seam) and SMART session expired. The last is the one row that says *do not degrade*,
so a regression there matters more than most.

---

## 14. Success metrics and acceptance criteria

The product metrics are defined, with their instruments and latest readings, in [METRICS.md](METRICS.md):

- **M1** groundedness, **M2** constraint recall, **M3** authorization integrity, **M4** latency, **M5**
  degradation, for the core copilot.
- **M-W2-1..6** for the document and evidence pipeline (extraction integrity, evidence separation, routing
  inspectability, eval-gate efficacy, robustness, PHI hygiene).
- The security platform's metrics.

M1, M2, M3 and M5 are produced by `dotnet run --project tests/AgentForge.Evals -- evals`; M4 comes from load
tests. Each eval gate fails when its case population is empty or one-sided, because "zero failures" over
cases nobody inspected reads exactly like a result.

**Acceptance lens.** A release is acceptable when the use cases in section 5 behave as stated, every
section 13.1 row behaves as its contract says, and the user test in section 1.4 holds: trustable at a glance,
inside the window, prioritized, honest under failure.

---

## 15. Cost

### 15.1 Cost economics and scaling

**Rule:** every figure is either a measurement (a counter delta from a named run) or explicitly labelled a
projection. The instrument is `agentforge_llm_tokens_total` and `agentforge_llm_cost_usd_total` (FR-OBS-2),
read from the sidecar's metrics endpoint before and after each flow. Pricing is set by
`Llm__InputPricePerMillionTokensUsd` / `Llm__OutputPricePerMillionTokensUsd` (defaults 2.00 / 10.00 USD per
million tokens, the list rate of the default model `claude-sonnet-5`); set them to your model's real rate or
the cost counter will be wrong. See [.env.example](.env.example).

**Measured cost per query** (local stack, synthetic cohort, two-document guideline corpus):

| Flow | Input tok/turn | Output tok/turn | LLM calls/turn | $/turn | Input share of cost |
|---|---|---|---|---|---|
| Pre-visit brief (UC-1) | 10,443 | 1,986 | ≥ 2 (not counted) | 0.0407 | 51.3% |
| Evidence turn, `POST /evidence/ask` (UC-10) | 1,007 | 414 | 1 | 0.0061 | 32.8% |
| Document ingest, lab PDF (UC-9) | 2,384 | 718 | 1 | 0.0119 | 39.9% |

The brief measured p50 32.4 s / p95 35.5 s at concurrency 1 (above NFR-PERF-1's budget, though not at its
population). The brief made four tool calls per turn when measured and now makes seven, so its input figure
reads low until re-measured. The critic (FR-VERIF-0) is rules-based: 19 ms, no LLM call.

**What the measurements show.** The brief is 6.6× the evidence turn and about 77% of the projected bill, and
it is the one flow that is not interactive (it runs before the visit). Neither input nor output is small:
input-side levers (prompt caching, context minimization) reach about half the bill at most, so the output side
needs batching and model tiering.

**Projection** (assumed: 18 patients per clinician-day, one brief and one evidence follow-up each, 0.5
ingests each; ≈ $0.95 per clinician-day, ≈ $20 per clinician-month at 21 clinic days):

| Clinicians | Unoptimized LLM spend / month (projection) | Change this tier forces |
|---|---|---|
| 100 | ≈ $2K | **Prompt caching** on the stable prefix (system prompt, tool schemas, accumulated tool results). The sidecar sets no `cache_control` today. Quality-neutral, so first. One instance still suffices (~2 concurrent turns). |
| 1K | ≈ $20K | **Batching and model tiering.** Move the brief to an asynchronous overnight batch (provider batch pricing is about half), and route the intake-extractor to a cheaper model, since its correctness is enforced by the extraction schema, not the model. |
| 10K | ≈ $200K | **Committed-use pricing, a telemetry sampling policy, read replicas for the RAG store** (for connection fan-out only; no latency claim). A queue with a completion deadline so 180K overnight briefs land before clinic. |
| 100K | ≈ $2M | **Evaluate a self-hosted small model for the intake-extractor** (fixed schema, high volume, failures visible), a dedicated cache tier, an enterprise contract, rate-limit headroom, a HIPAA-eligible host under BAA. |

**Cheaper vs safest.** Pull first: caching, overnight batching, a cheaper extractor model. Refuse: routing
verification or the answer-composer to a cheaper model, or shortening output by dropping citations. In the
metered run the brief's verification gate fired on three of four turns and suppressed two, so it is doing
real work.

**Not measured:** non-LLM infrastructure cost (no production bill exists; production is a customer-supplied
environment), LLM calls per chat turn (no call counter), observability ingest cost (traces are exported to
the console and not collected), and cache-hit rate (zero by construction until caching is implemented).

---

## 16. Traceability

| Concern | Requirements | Use cases |
|---|---|---|
| Authorization and access control | FR-AUTH-1..4, FR-CHAT-3, NFR-SEC-2 | UC-4, UC-6 |
| Verification and trust | FR-VERIF-0..4, FR-DATA-1, FR-DATA-4 | UC-1, UC-2, UC-3 |
| Speed vs completeness | NFR-PERF-1..4, FR-CHAT-4 | UC-1, UC-2 |
| Data security and HIPAA | NFR-SEC-1, FR-AUTH-4, section 12 | UC-4 |
| Failure modes | NFR-REL-1..2, FR-CHAT-4, FR-EVAL-1 | UC-5 |
| Observability | FR-OBS-1..4, NFR-TRACE-1 | all |
| Evaluation | FR-EVAL-1..4 | all |
| Document and evidence pipeline | section 18 | UC-9, UC-10, UC-11 |
| Security platform | section 19 | UC-W3-1..8 |

---

## 17. Decisions and known limits

Settled decisions are recorded with their reasons in [ARCHITECTURE.md](ARCHITECTURE.md) and
[ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md). Points that remain open in this release:

1. **Production host.** Which HIPAA-eligible environment runs the images under BAA is yours to choose
   (section 11).
2. **Domain-constraint validation.** The cardiology rule set is illustrative until a clinician validates it.
3. **Ingest trust model.** `POST /documents/ingest` authenticates by trusted private-network origin, with no
   token (decision W2-D17 in [ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md)). Keep it unreachable
   from the public internet; a shared-secret header is the deferred hardening.
4. **Ingest and retrieval SLOs** (NFR-SLO-W2-1) are set from baselines but not yet verified against a
   measured p95.
5. **Recovery procedure** (NFR-BACKUP-W2-1) is documented but has never been exercised.

---

## 18. Document and evidence pipeline requirements

The pipeline lets the copilot read clinical **documents** as well as structured data: a scanned lab PDF and
a front-desk intake form are ingested, structured facts are extracted **without inventing any**, guideline
evidence is retrieved to put those facts in context, and the answer cites every claim. The user is the same
cardiologist (section 1). Design: [ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md).

### 18.1 Goals and non-goals

Goals: ingest the two document types into strict schemas with page-and-region citations; never let raw
vision-model output reach the user; ground answers in a small guideline corpus with hybrid retrieval and
rerank, kept separate from record facts; route work through a small, inspectable supervisor and two
workers; prove quality with an eval gate that blocks regressions; keep one authority per data type.

| ID | Non-goal |
|---|---|
| NG-W2-1 | A general medical-document platform. Two document types that work beat five that do not. |
| NG-W2-2 | Writing derived observations back into OpenEMR (no supported write path exists; derived facts stay in the sidecar). |
| NG-W2-3 | Multi-vector (ColQwen2-style) indexing, a third document type, a lab-trend widget, query rewriting and domain filters. Deferred. |
| NG-W2-4 | Diagnosis, treatment recommendations or orders. NG1 holds; the pipeline's tools sit inside the same read-only allowlist (section 12.4). |
| NG-W2-5 | Real PHI, including document images and screenshots. |

### 18.2 Document ingestion and extraction

- **FR-DOC-1 (Must) — Ingestion endpoint.** `POST /documents/ingest`, called by the OpenEMR module's
  background service, accepts a document's content, its OpenEMR `DocumentReference` id, the patient id and
  `doc_type` ∈ {`lab_pdf`, `intake_form`} (closed enum). It returns strict-schema JSON linking every derived
  fact to the source document, and does not store the source. An unsupported `doc_type` is rejected.
- **FR-DOC-2 (Must) — The schema is the source of truth.** Raw vision-model output is validated against a
  strict schema; anything that fails is rejected with a structured error and **no** facts are persisted.
- **FR-DOC-3 (Must) — Data authority, no write-back.** Source documents are uploaded through OpenEMR's own
  Documents workflow, and OpenEMR is authoritative for them. The sidecar writes nothing to OpenEMR and owns
  only the derived facts, which cite the OpenEMR document. Re-ingesting identical content is an idempotent
  no-op keyed on a content hash.
- **FR-DOC-4 (Must) — Required fields.** `lab_pdf`: per test, `test_name`, `value`, `unit`,
  `reference_range`, `collection_date`, `abnormal_flag`, `source_citation`. `intake_form`: `demographics`,
  `chief_concern`, `current_medications`, `allergies`, `family_history`, `source_citation`.
- **FR-DOC-5 (Should) — Confidence and derived labelling.** Every extracted fact is labelled *derived* with a
  field-level confidence; low-confidence or missing values are emitted as `null` plus a flag, never guessed.

### 18.3 Hybrid retrieval

- **FR-RAG-1 (Must) — Small guideline corpus.** A cardiology guideline corpus committed to the repository and
  re-buildable from source.
- **FR-RAG-2 (Must) — Hybrid retrieval and rerank.** Sparse (keyword) plus dense (vector) search, reranked
  (Cohere Rerank or equivalent), with only the top evidence passed to the answer model. Snippets carry
  `{doc, section, chunk_id}`, and retrieval hit rate is observable. Without a reranker key, retrieval
  degrades rather than fails (section 13.1).
- **FR-RAG-3 (Must) — Evidence is separated from record facts.** Guideline snippets carry
  `source_type: guideline` and are never conflated with record facts (`fhir` / `derived`). An answer citing a
  lab value and a guideline shows two distinct source types.

### 18.4 Supervisor and workers

- **FR-GRAPH-1 (Must) — Inspectable supervisor.** A supervisor decides when extraction is needed, when
  evidence retrieval is needed and when the answer is ready, over typed state. Every routing decision is
  logged as a handoff event with the correlation ID, so a run's route and reasons can be rebuilt from logs.
  The supervisor only routes; it never writes content.
- **FR-GRAPH-2 (Must) — Two workers.** An intake-extractor (wraps FR-DOC-*) and an evidence-retriever (wraps
  FR-RAG-*), each with a typed handoff contract covered by contract tests.
- **FR-GRAPH-3 (Must) — Critic gate.** A critic node, the core copilot's rules-based verifier reused,
  rejects claims without a resolvable citation and flags unsafe content before release.

### 18.5 Citations and click-to-source

- **FR-CITE-1 (Must) — Machine-readable citation on every claim.** At least `{source_type, source_id,
  page_or_section, field_or_chunk_id, quote_or_value}`. No clinical claim ships without a resolvable citation
  object (extends FR-VERIF-1).
- **FR-CITE-2 (Must) — PDF bounding-box overlay.** Document-sourced facts carry a normalized bounding box,
  and the UI shows the document page with the cited region highlighted. Low-confidence scanned regions fall
  back to a page-level citation.

### 18.6 Eval gate

- **FR-EVAL-W2-1 (Must) — Golden set.** At least 50 synthetic cases covering extraction, evidence retrieval,
  citations, refusals and missing data, each naming the failure mode it guards, reproducible from the
  repository alone (JSON plus fixture documents). *As built:* 98 cases in [evals/golden](evals/golden/):
  50 extraction, 13 authorization, 25 answer-path and 10 evidence-retrieval.
- **FR-EVAL-W2-2 (Must) — Boolean rubrics.** Pass/fail per category, not a 1–10 score: `schema_valid`,
  `citation_present`, `factually_consistent`, `safe_refusal`, `no_phi_in_logs`, the authorization trio
  (`authorization_outcome`, `no_unauthorized_disclosure`, `attempt_logged`), the answer-path trio
  (`grounded_answer`, `constraint_flagged`, `transparent_degradation`) and the evidence pair (`retrieval_hit`,
  `evidence_grounded`). *As built:* every rubric is deterministic; there is no LLM judge.
- **FR-EVAL-W2-3 (Must) — A gate that blocks a real regression.** The suite fails the build if any category
  regresses by more than 5% or drops below its threshold, compared with
  [evals/baseline.json](evals/baseline.json). A deliberately injected regression must fail.
- **FR-EVAL-W2-4 (Must) — Adversarial and missing-data cases.** Unsafe or uncited suggestions,
  missing/low-confidence extraction, and no-evidence retrieval, each asserting the right refusal or gap
  statement plus logging.

### 18.7 Observability and cost

- **FR-OBS-W2-1 (Must) — Per-encounter telemetry.** Each encounter records tool sequence, latency per step,
  token use, cost estimate, retrieval hits, extraction confidence and eval outcome, with **no raw PHI**.
- **FR-OBS-W2-2 (Must) — Monitoring view.** The monitoring view adds ingestion count and latency, extraction field pass
  rate, retrieval hit rate, reranker latency, supervisor routing decisions, per-worker latency and eval pass
  rate per rubric category, so health is visible without reading logs.
- **FR-OBS-W2-3 (Must) — Cost and latency report.** Spend, projected cost, p50/p95 and bottlenecks, every
  number from measured telemetry (section 15.1, [METRICS.md](METRICS.md)).

### 18.8 Non-functional requirements

- **NFR-CONTRACT-W2-1 (Must) — Typed contract on every new interface.** Ingestion I/O, retrieval I/O and
  supervisor↔worker handoffs each have a strict, exported schema; the `lab_pdf` and `intake_form` extraction
  schemas are canonical; malformed payloads are rejected with a structured error. Extends NFR-CONTRACT-1.
- **NFR-MIGRATE-W2-1 (Must) — Schema evolution.** Schema changes carry a migration note, new stores are
  additive, a breaking tool-schema change forces a major version bump, and each data type has one source of
  truth with no silent overwrites.
- **NFR-TRACE-W2-1 (Must) — Tracing across the graph.** The correlation ID reaches ingestion, every worker
  handoff and every vision, embedding and rerank call; each worker is a child span of the supervisor span.
  Extends NFR-TRACE-1.
- **NFR-LOG-W2-1 (Must) — One structured log schema.** Pipeline events (`document_ingest_start/complete`,
  `extraction_outcome`, `retrieval_hit/miss`, `worker_handoff`, `eval_run_outcome`) extend the core log
  schema; no parallel convention, no plain text, no PHI.
- **NFR-SLO-W2-1 (Must) — SLOs, timeouts and alerts.** Ingestion p95 ≤ 11 s and evidence retrieval p95 ≤ 6 s,
  set from baselines (not yet verified against a measured p95). Every outbound vision, embedding, rerank and
  FHIR call has a timeout and bounded retries with backoff. Alerts fire on extraction failure rate, retrieval
  latency and eval regression (>5% drop in any category), each with a documented response. Extends FR-OBS-4.
- **NFR-HEALTH-W2-1 (Must) — Readiness covers pipeline dependencies.** `/ready` checks OpenEMR (document
  storage), the vector index and the reranker, and names the unavailable dependency rather than reporting a
  bare up/down, while `/health` still succeeds. Extends NFR-HEALTH-1.
- **NFR-CI-W2-1 (Must) — Build gates.** Schema-validation tests, supervisor↔worker contract tests and
  extraction regression tests run with build, lint, unit tests, dependency audit and security scan; a change
  failing any of them is not accepted.
- **NFR-API-W2-1 (Must) — OpenAPI and API collection.** An OpenAPI 3.0 spec for the pipeline's HTTP endpoints
  (ingest, evidence, the full agent flow) kept in sync by contract tests, and the Bruno collection extended to
  run every pipeline workflow end to end.
- **NFR-TEST-W2-1 (Must) — Testing strategy and offline integration tests.** A documented split of what is
  unit-tested, integration-tested, evaluated and not tested (and why). Integration tests run the full
  ingestion-to-answer path on fixture documents with scripted model responses and no network:
  `dotnet test tests/AgentForge.HermeticTests`. See [CONVENTIONS.md](CONVENTIONS.md).
- **NFR-DATA-W2-1 (Must) — Data model and lineage.** Every data type (extracted lab observation, intake fact,
  guideline chunk, citation record, source document) has a defined owner, lineage, access control and
  validation rule ([ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md)).
- **NFR-SEC-W2-1 (Must) — PHI audit of the observability surface.** Traces, diagnostic logs, eval data and
  cost reports carry no patient identifiers, raw document text or extracted clinical values, checked in the
  build by a PHI-detection check (the `no_phi_in_logs` rubric). The access-audit trail is outside this scope
  (NFR-SEC-1's exception), and the check excepts only the `patient=` value on its lines. The derived-fact
  store is a new PHI-at-rest surface: clinician-scoped, audited, never sent to telemetry. **At-rest encryption
  is the environment's responsibility** (decision W2-D21; [DEPLOYMENT.md](DEPLOYMENT.md)).
- **NFR-BACKUP-W2-1 (Must) — Backup and recovery.** The eval set and fixtures are reproducible from the
  repository and the guideline corpus re-builds from committed sources. The deliberate position is **no
  scheduled volume backups**: every store is derivable from synthetic, re-seedable data, so RPO is everything
  since the last re-seed and RTO is a re-seed ([DEPLOYMENT.md](DEPLOYMENT.md)). With real PHI you must supply
  backups.
- **NFR-PERF-W2-1 (Should) — Pipeline baselines.** CPU, memory, latency and throughput for ingestion,
  extraction, retrieval and the full multi-agent run, compared with the core copilot's baselines
  ([METRICS.md](METRICS.md)).

### 18.9 Pipeline risks

| ID | Risk | Mitigation |
|---|---|---|
| RW1 | The vision model invents a field or overstates confidence | Schema gate; confidence captured; `factually_consistent` and `citation_present` rubrics |
| RW2 | The ingestion trigger lives in the OpenEMR module | Sidecar writes nothing to OpenEMR; ingest reachable only on the private network; content-hash idempotency |
| RW3 | The derived-fact store is new PHI at rest | Clinician-scoped, audited, never in telemetry; at-rest encryption from the environment |
| RW4 | Rerank and embeddings add latency, cost and a dependency | Small corpus; provider seams; sparse-only degradation; cost per query tracked |
| RW5 | The supervisor becomes a black box | Typed state, logged handoffs, worker child spans |
| RW6 | An eval gate that does not actually block | Deterministic rubrics; an injected regression must fail the build |
| RW7 | Prompt injection through document content | Document text is data; authorization stays below the model |
| RW8 | Inaccurate bounding boxes on scans | Exact boxes on digital PDFs; page-level fallback on low confidence |
| RW9 | Scope creep | Two document types, two workers, one gate |
| RW10 | No guideline corpus provided | A small committed corpus, re-buildable from the repository |

---

## 19. Security platform requirements

The security platform ([security-platform](security-platform/)) is a separate, Python, multi-agent system
that continually attacks the copilot, judges the results, documents confirmed exploits and replays them as
regression tests. Its job is to show, to a standard a hospital CISO would accept, whether the copilot keeps
its promises under adversarial pressure. Design: [SECURITY-PLATFORM.md](SECURITY-PLATFORM.md).

**Status in this release.** The image ships a deterministic HTTP replayer: `serve` answers `GET /health`, and
`run` replays a case file against a target origin only if that origin is on the committed allowlist
(`allowlist.json`; the production front door is refused in code), within a spend ceiling. The four agents
(Orchestrator, Red Team, Judge, Documentation) are **defined but not run**: each has a runtime definition in
`agents/`, a versioned system prompt in `prompts/` checked by version and sha256, and message and
attack-case schemas in `schema/`. Requirements below that depend on running agents are not yet met.

### 19.1 Target, threat model and seed suite

- **FR-TARGET-W3-1 (Must) — A testable target.** The copilot runs in a testable state locally and deployed,
  with the changes needed for that documented.
- **FR-TARGET-W3-2 (Must) — A live target.** The platform tests the live deployed system, not a mock.
- **FR-THREAT-W3-1 (Must) — Threat model.** A full attack-surface map that opens with a short summary of key
  findings, the highest-risk categories and how coverage is prioritized
  ([SECURITY-PLATFORM.md](SECURITY-PLATFORM.md)).
- **FR-THREAT-W3-2 (Must) — Category coverage.** Prompt injection (direct, indirect, multi-turn); data
  exfiltration (PHI leakage, cross-patient exposure, authorization bypass); state corruption (history
  manipulation, context poisoning); tool misuse (unintended invocation, parameter tampering, recursion);
  denial of service (token exhaustion, loops, cost amplification); identity and role exploitation.
- **FR-THREAT-W3-3 (Must) — Per-category assessment.** Attack surface, impact, difficulty of exploitation, and
  whether existing defences address it, for each category.
- **FR-SUITE-W3-1 (Must) — Attack suite.** A working suite under `evals/` with reproducible results in at
  least three attack categories.
- **FR-SUITE-W3-2 (Must) — Required fields per case.** Attack category and subcategory; the exact prompt or
  input sequence; expected safe behaviour; observed behaviour (pass / fail / partial); severity and
  exploitability; whether it joins the regression suite. The schema is
  [attack-case.schema.json](security-platform/schema/attack-case.schema.json).
- **FR-SUITE-W3-3 (Must) — Seed cases, not static payloads.** Cases are structured, reproducible and
  extensible, because the Red Team learns from and extends them.
- **FR-SUITE-W3-4 (Must) — One agent role live.** At least one of Red Team, Judge or Orchestrator runs live
  against the deployed target.

### 19.2 Architecture

- **FR-ARCH-W3-1 (Must) — Architecture document.** Opens with a summary, names each agent and its role, and
  includes an agent-interaction diagram ([SECURITY-PLATFORM.md](SECURITY-PLATFORM.md)).
- **FR-ARCH-W3-2 (Must) — Per-agent definition.** Responsibilities, inputs and outputs, trust level and
  coordination for each agent.
- **FR-ARCH-W3-3 (Must) — Questions answered.** Who owns attack generation, evaluation, orchestration and
  documentation; message format; how the Orchestrator picks the next target; how Judge rulings feed the
  regression harness; where human approval gates sit and why; where AI is used versus deterministic tooling;
  how cost and rate limits are handled at scale; what manages agent state.
- **FR-METRICS-W3-1 (Must) — Key metrics.** A small set of metrics chosen in advance, with the rationale for
  each, in [METRICS.md](METRICS.md).

### 19.3 Multi-agent behaviour

- **FR-MAS-W3-1 (Must) — Distinct agents, not a pipeline.** Each role is a distinct agent with its own
  responsibilities, context and decision authority.
- **FR-MAS-W3-2 (Must) — Novel inputs.** The system generates new adversarial inputs, not only replays a list.
- **FR-MAS-W3-3 (Must) — Mutation of partial successes.** A partially successful attack is mutated into
  variants (for example ten) to probe for a bypass, without a human choosing what to try next.
- **FR-MAS-W3-4 (Must) — Multi-turn sequences.** Attacks include multi-turn sequences, not only single
  prompts.
- **FR-MAS-W3-5 (Must) — Consistent success criteria.** Whether an attack succeeded is judged by the same
  criteria across runs and target versions.
- **FR-MAS-W3-6 (Must) — Coverage-driven prioritization.** What to explore next is chosen from coverage gaps
  and unresolved findings.
- **FR-MAS-W3-7 (Must) — Cost halting.** A run halts or redirects when cost accumulates without producing new
  findings.
- **FR-MAS-W3-8 (Must) — Regression on change.** A change to the target triggers a regression run.
- **FR-MAS-W3-9 (Must) — Generation separated from evaluation.** The Judge is independent of the attacker,
  because an agent that grades its own attacks is compromised by design. The architecture states how the
  Judge's criteria are defined, kept from drifting and validated.
- **FR-MAS-W3-10 (Must) — Model choice per role, defended.** The model behind each role is a deliberate
  choice weighing frontier-model refusals of offensive work, smaller or open models, and token cost.

### 19.4 Documentation agent

- **FR-DOCAGENT-W3-1 (Must) — Reports without a human writer.** Exploits the Judge confirms become structured
  vulnerability reports automatically.
- **FR-DOCAGENT-W3-2 (Must) — Report content.** Unique ID and severity; description and **clinical impact**;
  minimal reproducible attack sequence; observed vs expected behaviour; recommended remediation; current
  status with fix-validation results.
- **FR-DOCAGENT-W3-3 (Must) — Usable cold.** A senior security engineer who was not present can reproduce,
  validate and fix the issue from the report alone.

### 19.5 Regression harness

- **FR-REGR-W3-1 (Must) — Versioned, queryable exploit store.** Confirmed exploits become deterministic,
  repeatable test cases in a versioned, queryable store.
- **FR-REGR-W3-2 (Must) — Full run on every new target version.** The whole regression suite runs
  automatically, triggered by the Orchestrator, against each new version of the target.
- **FR-REGR-W3-3 (Must) — Reappearance detection.** A previously fixed vulnerability coming back is detected.
- **FR-REGR-W3-4 (Must) — Cross-category regression.** Fixing one attack and breaking another category is
  flagged.

A regression test that passes because the model's behaviour drifted, rather than because the vulnerability
was fixed, is worse than no test; the Judge's criteria must distinguish the two.

### 19.6 Observability layer

The layer answers, at any time:

- **FR-OBSV-W3-1 (Must)** — Which attack categories have been tested, and how many cases per category?
- **FR-OBSV-W3-2 (Must)** — What is the pass/fail rate across categories and target versions?
- **FR-OBSV-W3-3 (Must)** — Is the target becoming more or less resilient over time?
- **FR-OBSV-W3-4 (Must)** — Which vulnerabilities are open, in progress or resolved?
- **FR-OBSV-W3-5 (Must)** — What did this run cost, and how is cost scaling?
- **FR-OBSV-W3-6 (Must)** — What is each agent doing, and in what order?
- **FR-OBSV-W3-7 (Must) — The Orchestrator reads it.** The layer is the data the Orchestrator prioritizes
  from, not only a human view.

### 19.7 Engineering requirements

- **NFR-OWASP-W3-1 (Must) — OWASP mapping per case.** The suite covers the relevant OWASP Top 10 categories
  (broken access control, injection, insecure design, vulnerable components, auth failures, logging failures,
  SSRF) and the OWASP LLM Top 10 (prompt injection, insecure output handling, sensitive information
  disclosure, excessive agency, supply chain), and every case is mapped to its OWASP category.

The following are **Should** requirements:

| ID | Requirement |
|---|---|
| **NFR-TESTDESIGN-W3-1** | Every adversarial case exercises a boundary (maximum prompt length, minimum token budget, an edge-case multi-turn sequence), an invariant (the Judge never approves a confirmed exploit) or a regression risk (a fixed vulnerability returning). A static payload list does not satisfy it. |
| NFR-BVC-W3-1 | Before building an agent role, a build-versus-configure record compares existing tools (Burp Suite, OWASP ZAP, Semgrep, Garak, commercial red-team platforms) and justifies building. |
| NFR-TRIAGE-W3-1 | A simulated scan report with at least ten findings (critical, high, medium, false positive), each with a validate / remediate / defer / document decision. |
| NFR-CONTRACT-W3-1 | Typed contracts for every inter-agent message (Orchestrator→Red Team, Red Team→Judge, Judge→Documentation), contract tests, migration notes, and an evidence pack: interaction diagram, schemas, trust boundaries, failure modes. |
| NFR-INTEG-W3-1 | An agent role can be integrated with an independently built peer agent through the published contract alone, with interface diffs, decision records, contract-test results and an end-to-end trace. |
| **NFR-SCHEMA-W3-1** | All inter-agent communication uses a versioned schema (JSON Schema or equivalent) published in a contracts directory; contract tests verify producer and consumer; breaking changes bump the version. Today: [agent-message.schema.json](security-platform/schema/agent-message.schema.json). |
| NFR-ATO-W3-1 | An ATO-style evidence pack: architecture and data-flow diagrams, auth model (which agent calls which target with what credentials), dependency list with versions, the platform's own scan results, eval results, a sample postmortem. |
| NFR-AIDISC-W3-1 | An AI-use disclosure: every AI-powered role, the deterministic check or human approval after each AI decision, remaining risks, and how a drifting Judge is detected and corrected. |
| NFR-INTEG-W3-2 | An integration pack: corrected contract diffs, decision records, contract-test results from both sides, an agent dependency map and a full end-to-end trace. |
| NFR-VERSION-W3-1 | Every inter-agent schema is versioned (minimum v1); a breaking change needs a version bump, migration note and updated contract tests. |
| **NFR-ERR-W3-1** | Every agent defines typed errors for: target unreachable, budget exceeded, judge timeout, no findings in window, and regression detected, published beside the success schemas. |
| NFR-RATE-W3-1 | Rate limits and auth are documented for every external API the platform calls, with the handling of rate-limit responses (backoff, queue or abort). |
| NFR-DQ-W3-1 | Every confirmed exploit has a unique ID and the required fields, with no duplicates of the same attack sequence; the Documentation agent validates this before writing a report. |
| NFR-MIGRATE-W3-1 | The exploit store's schema migrates without data loss when the report format gains a field; any queue's depth monitoring and back-pressure are documented. |
| NFR-DATA-W3-1 | The data model for exploits, regression cases, run logs and coverage metrics is documented with access control: which agents write, which only read, and what human approval precedes publishing a finding. |
| NFR-SQL-W3-1 | If the exploit store is SQL, indexes on severity, category and target version are documented, and a full regression run meets a documented time budget. |
| NFR-PERF-W3-1 | Platform CPU, memory, latency and throughput baselines for a representative run (100 attack cases plus the full regression suite). |
| NFR-LOAD-W3-1 | A load test of 100 consecutive attack cases against the live target, with orchestration latency, LLM latency and exploit-storage throughput, the bottleneck and the change that would fix it. |

---

## 20. Security platform users

The platform has three users. Two read or act on what it produces; the third never touches it and is the
reason it exists. The four agents are components, not users.

### 20.1 Users

- **U-W3-1 — The security reader.** The **security engineer** who fixes what the platform finds (the bar: an
  engineer who was not present can reproduce and fix from the report alone, FR-DOCAGENT-W3-3), and the
  **hospital CISO** who decides whether the copilot is getting safer from trends and open findings rather than
  transcripts. Not a pen tester hand-picking what to try next; the Red Team decides that itself.
- **U-W3-2 — The platform owner (the human approval gate).** The one person with authority to publish, spend
  and change what the platform may attack. They approve critical reports before publication, set model budgets
  and keys, and decide whether production is ever a target. Fixes are never pushed by the platform; a fix is an
  ordinary, reviewed copilot change. The gate must stay short enough to be read rather than rubber-stamped.
- **U-W3-3 — The clinician whose copilot is under test.** The cardiologist of section 1. They never open the
  platform. What it tests is whether the copilot still keeps its **Must refuse** promises (section 5) under
  attack, which is why every report states clinical impact.

### 20.2 Workflows

- **Continuous loop, no human in it.** The Orchestrator reads coverage gaps, open findings and regressions and
  directs the Red Team; the Red Team generates, mutates and escalates attacks against the target (staging by
  default); the Judge rules independently; a confirmed exploit goes to the Documentation agent, which writes
  the report and a regression case.
- **Security engineer.** Opens a report, replays it against staging, writes the fix as a normal copilot
  change, and the harness replays the exploit on the new build; the report's status updates from that run.
- **CISO.** Reads the observability layer (FR-OBSV-W3-1..6) and the platform metrics in [METRICS.md](METRICS.md).
- **Platform owner.** Approves the critical-report queue; the deploy gate blocks a release on any red
  regression; adding a target, a model key or a budget goes through them.

### 20.3 Use cases

| ID | Use case | Why automation | Stays human | Requirements |
|---|---|---|---|---|
| **UC-W3-1** | Find new attacks continuously, not from a fixed list: transcripts against the live target, including multi-turn sequences and mutations of partial successes. | Static payload lists go stale; generating ten variants of a partial success overnight is volume a human tester does not sustain. | Which systems may be attacked: an allowlist of your own deployments, changed only by a reviewed change. | FR-MAS-W3-2, -3, -4, -6; FR-SUITE-W3-3, -4; FR-THREAT-W3-2 |
| **UC-W3-2** | Rule on every attack consistently and independently of the attacker: succeeded / failed / partial, whether the defence held, whether it is a regression. | The same criteria must apply across thousands of transcripts and versions; an attacker that grades itself is compromised. | The Judge's ground truth and calibration. | FR-MAS-W3-5, FR-MAS-W3-9, FR-SUITE-W3-2 |
| **UC-W3-3** | Turn a confirmed exploit into a report an absent engineer can act on, validated and de-duplicated. | Everything the report needs is already data in the platform; hand transcription is where reproduction steps go missing. | Publishing a critical (UC-W3-4); deciding the fix. | FR-DOCAGENT-W3-1..3, NFR-DQ-W3-1 |
| **UC-W3-4** | Hold a critical-severity report for the platform owner's approval. | Deliberately **not** automated: a confident false positive wastes engineering time, and a real critical needs an owner. Lower severities publish without the gate. | Approve or reject. | FR-ARCH-W3-3, NFR-DATA-W3-1 |
| **UC-W3-5** | Replay every known exploit on every change to the target; flag a reappearance or a cross-category regression. | Replay is deterministic and cheap and must happen on every deploy. | Releasing despite a red run, which the gate does not allow on its own. | FR-REGR-W3-1..4, FR-MAS-W3-8 |
| **UC-W3-6** | Show whether the copilot is getting more or less resilient. | The answers aggregate every run ever made; a hand-made report is stale the next day, and the Orchestrator needs the same data in machine form. | What the numbers mean for the hospital. | FR-OBSV-W3-1..7, FR-METRICS-W3-1 |
| **UC-W3-7** | Stop spending when spend stops producing findings; record cost per agent. | The decision is made mid-run, often overnight; a self-enforced budget is the only version that works unattended. | The budget itself and any new model key. | FR-MAS-W3-7, FR-OBSV-W3-5 |
| **UC-W3-8** | Prove the copilot's refusals hold under attack: attacks aimed at the **Must refuse** clauses of UC-1..UC-6 and UC-9..UC-11 (cross-patient exposure, authorization bypass, invented values, unsourced document output). | The clinician cannot re-check the copilot inside 90 seconds, so its refusals are checked for them, continuously. The existing `authz-injection-*` golden cases are prior art. | The promises themselves; the platform tests them, it does not redefine them. | FR-THREAT-W3-2, FR-SUITE-W3-2, NFR-OWASP-W3-1 |

### 20.4 Open questions

- **Who may trigger a run or read an unpublished report**, beyond the platform owner, is not yet defined;
  any answer must be a platform access control.
- **When the Judge is uncertain**, whether the ruling escalates to the platform owner (growing the UC-W3-4
  queue) is not yet defined.
- **Production as a target.** By default the platform attacks staging only and the production front door is
  refused in code; attacking production is the platform owner's decision.
