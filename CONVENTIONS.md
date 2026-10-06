# Engineering Conventions

This document states the engineering rules the AgentForge codebase follows: the runtime and dependencies,
how C# code, HTTP clients, configuration, logging and tests are written, and the security, real-time, API
documentation, versioning and prompt rules. Section numbers are stable, because comments in the code cite
them (for example "CONVENTIONS.md §7"). Architecture lives in [ARCHITECTURE.md](ARCHITECTURE.md) and
[ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md); external interfaces in [INTERFACES.md](INTERFACES.md).

Two rules apply everywhere, in code, tests, fixtures, logs and telemetry alike:

- **Synthetic data only, never real PHI.** Every patient in the repository is a seeded demo patient.
- **No secrets in source.** Credentials and endpoints come from the environment (§6).

---

## 1. Runtime & language

- **.NET 10 (LTS)**, chosen for its long support window in a long-lived healthcare service.
- **C# latest** (`<LangVersion>latest</LangVersion>`), **nullable reference types on**, **implicit usings on**,
  **file-scoped namespaces** required.
- **Warnings are errors** (`TreatWarningsAsErrors`), with `AnalysisLevel` `latest-recommended` and code style
  enforced in the build. All of this is set once in [Directory.Build.props](Directory.Build.props).

## 2. Dependency manifest

Package versions are declared once for the whole solution with **Central Package Management**, in
[Directory.Packages.props](Directory.Packages.props). That file is the source of truth for versions; the
table below explains why each package is there.

| Package | Purpose | Notes |
|---|---|---|
| Refit (+ HttpClientFactory) | Typed HTTP clients for the OpenEMR FHIR/REST surface | §4 |
| Microsoft.AspNetCore.SignalR.Client | Real-time push to the embedded UI | §12 |
| Microsoft.Extensions.Http.Resilience | Retry, timeout and circuit breaker on outbound calls (Polly v8) | §5 |
| OpenTelemetry (+ hosting, instrumentation, exporters) | Traces, metrics, OTLP logs to Loki, OTLP traces to Tempo | §7 |
| Microsoft.EntityFrameworkCore + Npgsql | PostgreSQL persistence for the document and evidence pipeline | |
| Pgvector (+ EF Core) | pgvector bindings for hybrid retrieval embeddings | 0.x: bump deliberately |
| PdfPig | Word geometry from digital PDFs for click-to-source boxes | Apache-2.0 |
| Microsoft.Playwright | Automated SMART login for test token minting and local tools | Never in a shipped image |
| System.Security.Cryptography.Xml, Microsoft.OpenApi | Security pins over vulnerable transitive versions | |
| xUnit | Test framework | §8 |
| FakeItEasy | Fakes for the unit tier only | MIT |
| FluentAssertions | Readable assertions | **Capped `[6.12.0,8.0.0)`** |

**FluentAssertions stays below 8.0.0** because v8 and later are commercially licensed (paid per developer),
while v7.x is Apache-2.0. Do not lift the cap.

**Every third-party package has a stated purpose and a known license.** Check the license of anything new,
including what it pulls in transitively.

**Vulnerable packages fail the build.** `Directory.Build.props` sets `NuGetAudit=true`,
`NuGetAuditMode=all` (transitive packages too, which is where every advisory so far has come from) and
`NuGetAuditLevel=low`. Combined with warnings-as-errors, an advisory (`NU1901`–`NU1904`) fails
`dotnet restore`. **Respond to an advisory by bumping or pinning the package, never by suppressing it**: no
`NU190x` in `NoWarn`, `WarningsNotAsErrors` or `.editorconfig`, no `<NuGetAuditSuppress>`, and no
redeclaration of these four properties in any other `.props`, `.targets` or `.csproj` (a nested
`Directory.Build.props` would replace the root one for its whole subtree).

## 3. Coding standards (C# / .NET)

Use modern .NET idioms; do not carry over patterns from the OpenEMR PHP core.

- **Immutable by default**: `record` / `readonly record struct` for DTOs and value objects, `required` and
  `init` members. **`sealed` by default** on classes not designed for inheritance.
- **Constructor dependency injection** only. No service locators, no `new` of service types inside business
  logic, no ambient or global state. Everything is registered in the composition root.
- **Every external dependency sits behind an interface** (`IOpenEmrFhirApi`, `ILlmProvider`, the clock,
  retrievers, stores) so unit tests can fake it.
- **Domain primitives** for identifiers that string typing would confuse; parse raw input into typed values
  at the boundary.
- **`System.Text.Json` with source-generated contexts.** Strict schemas are the contract (NFR-CONTRACT-1:
  tool and message inputs and outputs follow strict, versioned schemas).
- **Async all the way.** Every I/O method takes a `CancellationToken` and honours it. No `.Result` or
  `.Wait()`.
- **Exhaustive `switch` expressions** on enums; no `default` arm that silently swallows a new case.
- **Errors**: catch the narrowest exception you can act on; never catch and swallow. Return a **typed error
  that names the category** (auth, not found, transient, validation), never a raw exception, secret, PHI or
  internal detail.
- **No PHI in diagnostic logs, client-facing exceptions or telemetry.** §7 governs the one exception, the
  access-audit trail.
- **Concurrency-safe.** Shared services are stateless where possible; no shared mutable state without
  synchronisation; no cross-request interference.
- **Extend additively.** A new endpoint or tool is a new interface method and tool, not a rewrite.
- **Authorization is enforced below the model**, in the tool and data layer. It is never enforced by prompt
  text: a prompt can be talked out of a rule, code cannot.

## 4. HTTP client standards (Refit)

Each external API is a **Refit interface**, registered through `IHttpClientFactory`, with cross-cutting
concerns in `DelegatingHandler`s rather than at call sites:

- **Auth handler**: attaches the clinician's bearer token from the server-side token store. It refuses to
  send when there is no token, or when the token has passed the expiry recorded at launch
  (`AccessTokenExpiredException`).
- **Correlation id handler**: adds the correlation id header to every outbound call, to OpenEMR and the LLM
  provider alike (FR-OBS-1). Handlers are pooled outside the request scope, so the id (like the token) is
  carried ambiently on the logical flow, never read from a scoped service.
- **Resilience**: the §5 pipeline is attached to the named client.
- **HTTPS only**, with server certificate validation left on. Disabling validation is allowed only on an
  isolated, explicitly flagged local path, never in a deployed environment.

```csharp
public interface IOpenEmrFhirApi
{
    [Get("/apis/{site}/fhir/MedicationRequest")]
    Task<string> GetMedicationRequestsAsync(string site, [AliasAs("patient")] string patientId,
        CancellationToken ct = default);
}
```

## 5. Resilience (Polly)

Every outbound call to OpenEMR and the LLM provider runs through a named pipeline from
`Microsoft.Extensions.Http.Resilience`:

- a **per-attempt timeout** sized to the interactive latency budget;
- **retry on transient faults only** (5xx, 408, timeouts, network), with exponential backoff and jitter,
  never on auth failures or non-idempotent calls;
- **429 handling** that honours `Retry-After`, capped, then degrades;
- a **circuit breaker** that sheds load and feeds `/ready`.

On exhaustion the system **degrades deterministically**: it returns source-cited data without synthesis. It
never fabricates and never fails silently.

## 6. Configuration (Options pattern)

- Settings bind to strongly typed options with validation at startup:
  `services.AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`. Misconfiguration fails
  at boot, not on the first request.
- **Credentials and endpoints** (OpenEMR base URL and site, OAuth client id, LLM keys, connection strings)
  come from **environment variables or a secret store**, layered over `appsettings.{Environment}.json`. The
  variables are listed in [.env.example](.env.example); `__` separates configuration sections.
- `IOptionsSnapshot<T>` where a per-request refresh matters, `IOptionsMonitor<T>` for change notification.
- **Secrets are never stored in plaintext** in source, `appsettings`, images or backups; use the platform's
  secret store or an encrypted provider.
- Per-environment configuration follows the deployment split in [DEPLOYMENT.md](DEPLOYMENT.md).

## 7. Logging & observability

### The basics

- **`ILogger` everywhere.** The libraries never bind to a concrete provider; the host chooses.
- **Structured logging only**: message templates with named properties, never string interpolation, and
  never PHI.

  ```csharp
  // BAD:  logger.LogInformation($"Fetched labs for {patientId}");
  // GOOD: logger.LogInformation("Fetched {ResourceCount} labs", count);
  ```

- **Correlation id** (FR-OBS-1: every request and downstream call carries one id that joins its log lines
  and spans). It is opened as a logging scope in exactly one place per invocation and never nested:
  `CorrelationIdMiddleware` for an HTTP request (adopting a well-formed inbound `X-Correlation-Id`, otherwise
  minting one) and `ChatSessionCoordinator` for a chat turn. A chat turn's scope also carries
  `ConversationId`, derived one-way from the session id, so a multi-turn conversation can be reconstructed.
- **One log line per LLM call, however it ends.** Success logs model, input and output tokens, cost, stop
  reason and latency. Failure logs model, status, exception type and latency, including Polly timeout and
  circuit-breaker exhaustion. **Never log prompt or completion text, and never the provider's error body**:
  requests carry chart content and error bodies can echo it. Provider exceptions carry only the status,
  the provider's error type and request id, both allow-listed.
- **Metrics** (OpenTelemetry: latency, tool counts, tokens and cost, verification pass/fail, authorization
  permit/refuse) feed the Grafana panels. **Metric labels must be bounded, enum-like values**, never a patient,
  user, resource id or free text. The labels in use are `outcome`, `tool`, `reason`, `direction`, `worker`,
  `from`, `to` and `stage`. Note that `tool` carries the name the model asked for, so it is bounded only by
  the provider respecting the advertised tool catalog.
- **Logs** go through the OpenTelemetry provider: always to the console, and to Loki over OTLP/HTTP when
  `Observability:LokiOtlpEndpoint` is set. Loki is optional and fail-open: unset means console only, and an
  unreachable endpoint never blocks a request.
- **Traces** go to Tempo over OTLP/HTTP only when `Observability:TraceOtlpEndpoint` is an absolute http(s)
  URL (malformed: no exporter plus a startup warning). The console trace exporter is opt-in
  (`Observability:TraceConsoleExporter`) for local debugging. **`SpanPhiScrubber` runs before every
  exporter**: URLs keep their origin and a path with identifier segments replaced by `{id}`; query strings,
  user info, client address, status descriptions and exception events are dropped. A new span tag must be a
  bounded value (a node name, an outcome, a count).
- If a host adds Serilog or NLog, it must redact PHI and include the correlation id on every record.

### The audit trail is not diagnostic logging

"No PHI in logs" is a rule about **diagnostic** logging. The **access-audit trail** is the one deliberate
exception, because **FR-AUTH-4** requires every patient-data access to record which patient was accessed (an
audit log that cannot name the patient fails its purpose). **NFR-SEC-1** (no PHI in logs or telemetry)
carries that exception in its own text, and HIPAA's audit-control rule points the same way: audit records are
protected by access control and retention, not by leaving the patient out.

- **The audit stream names the patient, on purpose.** [`AccessAuditLog`](src/AgentForge.Mcp/AccessAuditLog.cs)
  records granted and refused access alike, and is the only place a patient id is expected. Every method
  takes an `ILogger<AccessAudit>`, so every line is written under the category `AgentForge.AccessAudit`.
- **That category is console-only.** `Program.cs` removes it from the OpenTelemetry logging provider in
  code, after all configuration sources, so no logging setting can route it to Loki or any OTLP sink.
  Audit lines reach the built-in console provider and so the platform's stdout retention. For production a
  dedicated, access-controlled audit sink under your retention schedule is recommended
  ([DEPLOYMENT.md](DEPLOYMENT.md)).
- **The diagnostic stream never names a patient.** No `[LoggerMessage]` other than `AccessAuditLog` may
  template a patient, subject, document, binary or encounter id; a unit test reflects over every logger in
  every `AgentForge.*` assembly and fails on one. Diagnostic lines log the clinician, the resource type or a
  category instead.
- **The correlation id is the join.** Every diagnostic line about an authorization decision is paired with
  an audit record under the same correlation id, so finding which patient a refusal concerned needs the
  audit trail and nothing else. No pseudonym is needed; if one ever is, use a keyed HMAC with the key held
  outside the data volume, since a plain hash is reversible by enumeration.
- **No exception messages in diagnostic lines** where they could echo a resource path: log
  `ex.GetType()`, not `ex.Message`.
- **Framework URL logging is suppressed.** `HttpClient` logging (lines and the `HTTP {Method} {Uri}` scope)
  is removed for every client in `ConfigureHttpClientDefaults`, and framework URL loggers are held at
  `Warning` in `appsettings.json` as a backstop. The inbound `RequestPath` scope is kept but scrubbed with
  the same `{id}` rule as spans (`RequestPathScrubbingScopeProvider`).
- **New code**: log the correlation id and a category; route anything that genuinely must name a patient
  through `AccessAuditLog`.

### How "no PHI" is verified

| Artifact | Check |
|---|---|
| Diagnostic logs | The `no_phi_in_logs` eval rubric (a safety gate at 1.0, see [evals/README.md](evals/README.md)) scans every line a golden case logs for the case's patient ids, names and extracted values, including short identifier digit runs under identifier-named keys. The only exemption is the templated `patient=` field of a genuine `AgentForge.AccessAudit` line. |
| Host stdout | `HostStdoutPhiScanTests` boots the production host with fakes at the HTTP transport, drives five patient-data paths and scans everything both console sinks write. |
| Hermetic runs | The hermetic tier (§8.3) scans its logs and spans, including each part of a patient's name. |
| Traces | `SpanPhiScrubber`, with a test pinning that it runs ahead of every exporter. |
| Metrics | The bounded-label rule above. |
| Eval datasets and results | `CommittedArtifactPhiScanTests`: committed results carry no golden patient id or name; datasets use only `syn-patient-` ids and no real-shaped SSN, phone or email. |

When a legitimate word trips the PHI rubric, first establish whether the logged value could have come from a
document or model reply; if so it is a leak and the log line is fixed. Never edit a golden reply or its
tokens to make a hit disappear. A fixed code literal may be exempted only by an entry in `RubricEvaluator.cs`
bound to one log event and field, with a test showing the same word elsewhere still fails. A synthetic number
that collides by chance with a computed count is fixed by changing the planted synthetic value everywhere the
case carries it.

## 8. Testing standards

There are three test tiers, each its own project, plus the eval pair (`AgentForge.EvalTests`,
`AgentForge.Evals`, which score agent behaviour; see [evals/README.md](evals/README.md)). Framework xUnit;
assertions FluentAssertions `[6.12.0,8.0.0)`; FakeItEasy in the unit tier only.

Rules for every tier: Arrange-Act-Assert; names in `Method_State_ExpectedResult` form; one behaviour per test;
synthetic data only; and **each test guards a named failure mode** (a boundary such as missing or malformed
data, an invariant such as "a claim must cite a source", or a regression). Happy-path-only suites are not
acceptable.

**Tests first.** Write the failing unit test from the contract (the use case, the schema, the requirement)
before the implementation, then the minimum code to pass, then refactor. Tests specify behaviour, not private
details. Fix a bug by first reproducing it with a failing test.

### 8.1 Unit tests (`AgentForge.UnitTests`)

- **Fully faked.** FakeItEasy fakes every external dependency. **No network, no database, no file I/O.**
  Deterministic and fast enough for every build.
- Cover every public method, including faked failures: timeouts, 4xx/5xx, malformed FHIR, empty bundles,
  cancellation, resilience exhaustion leading to degradation.
- **Narrow exception: host-composition tests.** A few tests boot `Program` on an in-memory TestServer
  because the property they guard exists only in the composed host (`TracerPipelineOrderTests`,
  `FrameworkLogLevelTests`, `AccessAuditOtlpExclusionTests`, the histogram export test,
  `HostStdoutPhiScanTests`). They still use no network or database, write no file, read only
  `appsettings.json`, and are deterministic. A new one needs the same justification.
- Line coverage is collected here (Cobertura) as information only; it says nothing about the other tiers.

### 8.2 Integration tests (`AgentForge.IntegrationTests`)

- **Real dependencies, nothing under test mocked.** These run against a deployed OpenEMR (FHIR, OAuth2,
  SMART), its database and the real LLM provider, exercising the real Refit clients, token flow, FHIR
  parsing and end-to-end tool calls. They catch contract drift that fakes cannot (US Core profile, scope to
  resource mapping, search parameter support).
- **Configured from environment variables**, never hard-coded: `OpenEmrQa__BaseUrl`, `OpenEmrQa__Site` and
  related keys for OpenEMR, `LlmQa__ApiKey` and `LlmQa__Model` for the LLM, `AgentForgeDataQa__*` for the
  vector index.
- **Unconfigured means fail, not pass.** A fixture whose required variables are missing throws at setup,
  naming the variables; there is no fallback to a mock. A small number of end-to-end tests carry an explicit
  `Skip` reason.
- Synthetic demo data only. Tests are **idempotent** and tolerant of the demo data's known gaps (they assert
  graceful handling). Include **authorization and adversarial cases**: a request for data the caller is not
  entitled to must be refused with no leakage.
- `/ready` must return 503 promptly when a configured dependency (OpenEMR, database, LLM, observability, the
  vector index with its extension, table and index) is unreachable, and 200 with a `Degraded` body for a
  dependency that was never configured. Assert on the named check in the JSON body.
- **Every test class declares what it needs.** A class taking a `*QaFixture` needs a deployed environment.
  Any other class carries `[Trait("Deployment", "None")]` (nothing leaves the process except a loopback
  peer) or the name of the one real service it reaches; `DeploymentTraitTests` enforces this. Run the
  self-contained set with `dotnet test tests/AgentForge.IntegrationTests --filter "Deployment=None"`.
- A hand-built test host validates its container on build (`ValidateOnBuild`, `ValidateScopes`), as
  `Program.cs` does, and reuses the production registration extensions rather than copying registrations.
- The full tier is slow and environment-dependent, so run it **after a deploy**, as a post-deploy smoke test.

### 8.3 Hermetic tests (`AgentForge.HermeticTests`)

- **Purpose**: the full ingestion-to-answer path with no network. Committed fixture documents go through the
  real `DocumentIngestionService`, `DocumentExtractor`, `DerivedFactMapper` and `DerivedFactStore`, then a
  question through the real `EvidenceAgentSupervisor`, `HybridEvidenceRetriever`, composer and
  `ClinicalResponseVerifier`; the test rules on citations, the critic and the logs.
- **Why a separate tier**: the unit tier forbids the file I/O fixtures need, and the integration tier forbids
  the scripted model this needs.
- **Only external boundaries are replaced, never with a mocking library**: a hand-written scripted
  `ILlmProvider`, EF Core's in-memory provider under the real `AgentForgeDbContext`, and an in-memory
  `ISparseRetriever` for the Postgres full-text half. Everything else is composed through the production
  registration extensions.
- **Hermeticity is asserted**: configuration is an empty in-memory source, and the run listens on the .NET
  networking activity sources (`System.Net.Http`, `Experimental.System.Net.Sockets`,
  `Experimental.System.Net.NameResolution`) and fails on any HTTP request, socket connect or DNS lookup.
- **Red controls**: each check names the stage it guards, and a control per stage breaks it and requires
  failure there. The end-to-end test asserts several stages in one run, since the behaviour under test is
  the chain itself. Tests run serially.
- **Fixtures are generated, never hand-edited**:
  `dotnet run --project tools/GenerateFixtureDocuments` regenerates `tests/fixtures/documents/`
  byte-identically and `-- --check` verifies; `FixtureDocumentTests` fails on drift. Each document has a
  `.manifest.json` of the facts a correct extraction yields. Every patient is a seeded demo patient with a
  `SYN-` MRN.

### 8.4 A guard is proved in both directions

A test or check that has only been seen passing has not been tested. When you add or change one: break what
it guards and confirm this assertion fails; leave it alone (or make an inert edit) and confirm it stays
green; widen what it matches and confirm it fails too. For C#, the mutation is reverting the production line
under test. Common traps: partial matches (`> 6` matches `> 600`), checks that report absence without
proving they ran, a mutation that silently did not apply, and an assertion that cannot fail on its own.

## 9. Solution layout

The solution [AgentForge.slnx](AgentForge.slnx) sits at the repository root, with `src/` and `tests/` as
siblings. Base namespace and assembly prefix: `AgentForge`.

```
src/
  AgentForge.Api/                 ASP.NET Core host: BFF, SignalR hub, endpoints, /health and /ready
  AgentForge.Agent/               orchestrator: multi-turn loop, tool chaining
  AgentForge.Agents/              evidence agent supervisor, workers and composer
  AgentForge.Mcp/                 tool server: contracts, access-audit log, read-only FHIR tools
  AgentForge.Verification/        source attribution and cardiology constraint rules
  AgentForge.Integration.OpenEmr/ Refit clients, OAuth/SMART, FHIR mappers
  AgentForge.Llm/                 ILlmProvider abstraction and the Anthropic implementation
  AgentForge.Documents/           document extraction, PDF text layer, citation boxes
  AgentForge.Retrieval/           hybrid (dense + full-text) retrieval, reranking, corpus seeding
  AgentForge.Data/                EF Core + pgvector: entities, DbContext, migrations
  AgentForge.Observability/       activity source and metrics
tests/
  AgentForge.UnitTests/           §8.1
  AgentForge.IntegrationTests/    §8.2
  AgentForge.HermeticTests/       §8.3
  AgentForge.EvalTests/           golden-set rubrics as xUnit theories
  AgentForge.Evals/               golden-set eval console
  fixtures/documents/             generated synthetic documents
tools/                            .NET utilities (fixture generator, demo seeding, token minting, load test)
```

Unit tests reference the projects they fake; integration tests reference the host; hermetic tests compose
the pipeline projects through their registration extensions.

## 10. Checks to run before a change

Run, from the repository root, every check the change touches:

```
dotnet build AgentForge.slnx
dotnet format --verify-no-changes
dotnet test tests/AgentForge.UnitTests
dotnet test tests/AgentForge.EvalTests
dotnet run --project tests/AgentForge.Evals -- evals
dotnet test tests/AgentForge.HermeticTests
dotnet test tests/AgentForge.IntegrationTests      # against a configured environment (§8.2)
```

If the HTTP surface, tool schemas or evidence graph types change, the contract tests in the unit suite
(OpenAPI, tool schema and graph schema checks) fail until the committed renderings are regenerated. A green
build and test run does not prove the host starts: after changing dependency registrations, run the host and
call its endpoints.

## 11. Security standards

- **Secrets never appear** in logs, exception messages, stack traces, telemetry or error responses. Log a
  lookup id, not the secret.
- **HTTPS for all REST** (§4) and **secure transports for all SignalR traffic** (§12).
- **Certificate validation on**, except an isolated, explicitly flagged local development path.
- **No plaintext secrets at rest** in source, `appsettings`, images or backups (§6).
- **Tokens stay server-side** (decision D11: the backend-for-frontend holds OAuth tokens and patient context
  in the server session; the browser only ever has a session cookie). Tokens are attached by a
  `DelegatingHandler` and never reach the browser, logs or errors.
- **PHI**: synthetic data only; never in diagnostic logs or telemetry. The access-audit trail is the single,
  deliberate exception (§7).
- **Authorization is enforced in code below the model**, never by prompt text (§3).

## 12. Real-time / SignalR standards

- **Secure transports only**: `wss://` for WebSockets and `https://` for Server-Sent Events. Plaintext
  `ws://` is not allowed.
- **No silent drops.** Failed outbound messages are queued and retried with bounded backoff; if still
  undeliverable they are reported on an error channel and logged with the correlation id.
- **No session state inside a hub.** ASP.NET Core does not support `HttpContext.Session` in SignalR, and
  under long-polling a hub has no session feature at all. `ChatHubSessionMiddleware`, running after
  `UseSession()`, copies the caller's identity into `HttpContext.Items` while the request is still ordinary
  HTTP, and the hub reads it from there. Do not move it into cookie claims, which would put the token and
  patient context in the browser (D11).
- **Automatic reconnect** with backoff, replaying queued messages idempotently.
- **Concurrency-safe hub client.** The hub streams tool-progress status (`ChatStatus`) during a turn and
  delivers the answer whole.

## 13. API documentation standards

- **XML doc comments on every public type, model and method** (`GenerateDocumentationFile` is on; missing
  docs are errors on the public surface). They describe behaviour and contract.
- **Match the source contract**: comments on OpenEMR-facing models follow the OpenEMR API wording where
  practical.
- **The sidecar's API reference is its generated OpenAPI document** ([INTERFACES.md](INTERFACES.md)). An
  endpoint handler's `<summary>` and `<remarks>`, and a request or response record's `<summary>`, are
  published verbatim as external API text. Anything true only of the code (why a member is `internal`,
  which test guards it) goes in a `//` comment instead.
- **The document is generated**: fix the source and regenerate, never edit the JSON. A unit test fails when
  the committed document differs from the implementation.
- **Doc source depends on accessibility**: a `private` handler's XML comment never reaches the document, so
  use `.WithSummary()` there; on an `internal` or public handler the XML comment wins and `.WithSummary()`
  is dead. Set one, not both.

## 14. Versioning (SemVer)

- The sidecar release, git tags and tool/contract schemas follow **Semantic Versioning 2.0.0**. The release
  is one version for the whole solution, declared as `<Version>` in `Directory.Build.props` (not yet set
  there, so assemblies currently carry the SDK default).
- **Conventional Commits drive the bump**: `fix:` is PATCH, `feat:` is MINOR, a `!` after the type or a
  `BREAKING CHANGE:` footer is MAJOR.
- **Before 1.0.0** a MINOR may break; 1.0.0 marks a contract surface worth committing to.
- **Releases are tagged** `vMAJOR.MINOR.PATCH` at deploy time, so a version traces to a commit and a build.
- **A breaking change to a tool schema** (NFR-CONTRACT-1) is a breaking change to the sidecar and forces a
  MAJOR on its own.
- Keep history readable: one commit per reversible decision, each message stating its purpose.

## 16. Prompt standards

A prompt is source: it is compiled into the binary and changes behaviour on every turn, but nothing in a diff
to it says what it is for. [PROMPTS.md](PROMPTS.md) is the inventory and the rationale.

### 16.1 Every rule records why it exists

Adding or changing a prompt rule means recording, in the same change, in [PROMPTS.md](PROMPTS.md): its
**purpose**; the **use case or requirement** it serves; what **enforces** it deterministically, or explicitly
nothing; which **eval category** would notice a regression, or that none would; and its **provenance** when it
fixes a defect. Several rules exist to fix specific defects and read as arbitrary without that note; a reader
who cannot tell tuning from a defect fix will cut the fix.

### 16.2 The inventory is enforced

`LlmPromptInventoryTests` asserts that every prompt quoted in [PROMPTS.md](PROMPTS.md) is byte-equal to its
constant, and that every prompt constant in the prompt-bearing assemblies is accounted for. So a prompt
change that skips the document, or a new prompt the document does not list, fails the unit suite.

What it cannot check is whether the rationale was written, and **nothing scores prompt behaviour**: the eval
gate drives scripted providers that never read the prompt. A deleted phrase is caught; a behavioural
regression is not. Put annotations **outside** the fenced prompt blocks, because text inside breaks the byte
comparison.

### 16.3 A prompt never claims what only a mechanism can deliver

Do not write a prompt sentence that promises a guarantee nothing implements. When a rule has no
deterministic backstop, say so in [PROMPTS.md](PROMPTS.md) rather than letting its presence imply one.

### 16.4 Check the wire before adding a knob

`AnthropicMessageRequest` deliberately has no `temperature`: the current model rejects an explicit value with
a 400, which would degrade every call to the deterministic fallback. Before adding any sampling parameter,
confirm the API accepts it and record the answer in [PROMPTS.md](PROMPTS.md).
