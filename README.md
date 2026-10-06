# AgentForge Clinical Copilot

AgentForge is an AI clinical copilot for the outpatient cardiologist. It runs as a .NET 10 **sidecar** beside
OpenEMR and reads the chart only through OpenEMR's standard FHIR R4 API, using the clinician's own OAuth
identity from a SMART EHR launch, so it can never see more than that clinician could. In the minute or two
between rooms it gives a pre-visit brief of what changed and what matters today, answers grounded follow-up
questions, and lists the day's remaining patients.

## What it does

- **Pre-visit brief and follow-up chat.** A multi-turn agent plans read-only FHIR tool calls (problems,
  medications, labs, vitals, encounters, documents) through an MCP tool layer and streams its answer to the
  browser over SignalR.
- **Two-layer verification.** Every model-composed answer is checked before it is shown: each clinical claim
  must cite a real FHIR resource or document (uncited clinical values are dropped), and cardiology rules
  (INR range, QT-prolonging combinations, renal dosing of anticoagulants, potassium with ACE inhibitors, and
  others) flag unsafe combinations.
- **Authorization below the model.** The OAuth token stays in the sidecar (backend-for-frontend), every read
  uses the clinician's token, and the sidecar adds a clinician–patient relationship check that OpenEMR does
  not provide. Prompt injection cannot widen access.
- **Daily Agenda.** An on-demand list of the day's remaining appointments with a short summary per patient.
- **Document and evidence pipeline.** Lab PDFs and intake forms are extracted against a strict schema with
  page-level citations (click-to-source), and questions can be answered from a hybrid (vector + full-text,
  reranked) guideline corpus in Postgres with pgvector.
- **Security platform** (`security-platform/`). A deterministic HTTP replayer that runs attack cases against
  an allow-listed copilot deployment, plus defined (not yet running) Orchestrator, Red Team, Judge and
  Documentation agents.

Stack: C# / .NET 10 (LTS), ASP.NET Core, Refit, SignalR, Polly, EF Core with Npgsql and pgvector, Anthropic
as the LLM provider behind `ILlmProvider`, Cohere for embeddings and reranking, nginx as the front door,
Prometheus and Grafana for monitoring, and Python for the security platform.

## Run it locally

You need Docker, and the .NET 10 SDK for the setup tools. Everything is reached through one origin,
<http://localhost:8080>: OpenEMR at `/`, the copilot under `/agentforge`.

```bash
docker compose up -d     # OpenEMR (with the AgentForge module), its database and the nginx front door
```

Log in at <http://localhost:8080> as `admin` with the throwaway local password set in
[docker-compose.yml](docker-compose.yml). Set `OPENEMR_ADMIN_PASSWORD` (and `MYSQL_ROOT_PASSWORD`) in a
`.env` (`cp .env.example .env`) before exposing the stack beyond your machine.

The OpenEMR image is a patched OpenEMR build, pulled by an explicit tag: the SMART launch depends on its core
patches, so stock OpenEMR with the module dropped on top will not complete a launch.

To add the copilot:

1. Put an Anthropic API key in `.env` as `ANTHROPIC_API_KEY`.
2. Register the SMART clients, and copy the printed patient-client id and secret into `.env` as
   `OPENEMR_CLIENT_ID` / `OPENEMR_CLIENT_SECRET`:

   ```bash
   dotnet run --project tools/RegisterSmartClients -- http://localhost:8080
   ```

3. Run the database half of the bootstrap (Site Address Override, client enablement, module launch URIs). It
   needs MySQL, which only the bootstrap overlay publishes, on loopback:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.bootstrap.yml up -d
   MYSQL_ROOT_PASSWORD=<your MySQL root password> dotnet run --project tools/BootstrapOpenEmr -- http://localhost:8080
   ```

4. Start the sidecar and its pgvector store, then check the front door:

   ```bash
   docker compose --profile copilot up -d
   bash scripts/post-deploy-verify.sh http://localhost:8080
   ```

A fresh stack has only the `admin` user and no patients. Seeding a demo cardiologist, a charted synthetic
cohort and a window of appointments is described in [DEPLOYMENT.md](DEPLOYMENT.md) §4 and §4a.
`tools/SeedDemoPatients` creates demographics-only patients, which is useful for volume but not for a demo.

Optional monitoring (Prometheus and Grafana):

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml --profile copilot up -d
```

**Synthetic data only.** This stack runs over plain HTTP with local-development settings. It is for
evaluation on synthetic data, never real patient data.

## Build and test

```bash
dotnet build AgentForge.slnx
dotnet format --verify-no-changes
dotnet test tests/AgentForge.UnitTests                 # unit tests, fully mocked, no external dependencies
dotnet test tests/AgentForge.EvalTests                 # deterministic eval rubric checks (xUnit)
dotnet run --project tests/AgentForge.Evals -- evals   # golden-set eval gate (evals/golden/), fails on regression
dotnet test tests/AgentForge.HermeticTests             # ingestion-to-answer on fixtures with a scripted model, no network
dotnet test tests/AgentForge.IntegrationTests          # against a real OpenEMR, LLM and Postgres, configured by environment variables
dotnet test tests/AgentForge.IntegrationTests --filter "Deployment=None"   # only the classes that need no deployed environment
sh security-platform/run-selftest.sh                   # security platform self-test
```

Integration-test configuration is in [CONVENTIONS.md](CONVENTIONS.md) §8.2. The eval suite and its gate are
described in [evals/README.md](evals/README.md) and [ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md) §8.

## Where things are

| Path | What it holds |
|---|---|
| `src/AgentForge.Api` | the sidecar host: SMART launch, session (BFF), chat hub, agenda, evidence and ingestion endpoints, health |
| `src/AgentForge.Agent` | the conversational agent (orchestrator), tool catalog, cardiology prompt profile |
| `src/AgentForge.Mcp` | the read-only MCP tool server and the patient-relationship authorization gate |
| `src/AgentForge.Integration.OpenEmr` | typed (Refit) OpenEMR OAuth and FHIR clients |
| `src/AgentForge.Verification` | source attribution and the cardiology rule engine |
| `src/AgentForge.Llm` | the LLM provider seam and the Anthropic implementation |
| `src/AgentForge.Documents`, `src/AgentForge.Agents`, `src/AgentForge.Retrieval`, `src/AgentForge.Data` | document extraction, the evidence agent graph, hybrid retrieval, the Postgres data tier and its migrations |
| `src/AgentForge.Observability` | metrics, tracing and audit logging |
| `tests/` | unit, integration, hermetic and eval tests, and a Bruno request collection |
| `evals/` | golden cases, the baseline and committed eval results |
| `tools/` | setup and load-test tools: SMART client registration, OpenEMR bootstrap, seeding, the chat load test |
| `reverse-proxy/` | the nginx front door |
| `observability/` | Prometheus, the Grafana dashboard and the alert rules |
| `.railway/railway.ts` | the hosted deployment as infrastructure as code |
| `security-platform/` | the adversarial security platform |

## Documents

| Document | Read it for |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | the core copilot: components, trust boundaries, verification, readiness, decisions D1–D18 |
| [ARCHITECTURE-DOCUMENTS.md](ARCHITECTURE-DOCUMENTS.md) | document ingestion, retrieval, the evidence agents, the data tier, decisions W2-D1–W2-D22 |
| [SECURITY-PLATFORM.md](SECURITY-PLATFORM.md) | the security platform and the threat categories |
| [REQUIREMENTS.md](REQUIREMENTS.md) | users, use cases (`UC-`), functional (`FR-`) and non-functional (`NFR-`) requirements |
| [METRICS.md](METRICS.md) | key metrics (M1–M5), SLOs, performance baselines, the dashboard and alerts |
| [INTERFACES.md](INTERFACES.md) | the SMART/OAuth, FHIR and audit interfaces with OpenEMR, and the sidecar's own HTTP, SignalR and tool surface |
| [DEPLOYMENT.md](DEPLOYMENT.md) | configuration, bootstrap, images and versions, deploying, smoke checks and rolling back |
| [CONVENTIONS.md](CONVENTIONS.md) | the engineering rules for changing the code |
| [PROMPTS.md](PROMPTS.md) | every prompt the copilot sends to the LLM, and what is enforced in code instead |

Code comments cite these documents by section and ID, for example `ARCHITECTURE.md §5.7`, `D11`,
`FR-AUTH-2` or `CONVENTIONS.md §8.2`.

## Terms

This software is proprietary. Its use is governed by [LICENSE](LICENSE) and by the written agreement
between Adam Marquette and the licensee.
