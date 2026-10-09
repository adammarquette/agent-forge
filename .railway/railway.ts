// -----------------------------------------------------------------------------
// Railway Infrastructure as Code — the hosted deployment of the AgentForge
// Clinical Copilot stack.
//
// THIS FILE IS THE DEPLOYMENT. The previous Railway environment was retired
// because "most of its working config only ever existed as live dashboard edits"
// (commit 0a4c729), so when it went down there was nothing to rebuild it from.
// Everything that can live in source lives here, and CI fails on drift between
// this file and the live environment.
//
// reference: DEPLOYMENT.md
//
// NOT config-as-code: railway.json / railway.toml are deprecated, new services
// cannot opt into them, and they stop being read on 2026-12-01.
//
// WHAT THIS FILE CANNOT EXPRESS: the first-run bootstrap (DEPLOYMENT.md §4) is
// live state in OpenEMR's DATABASE - Site Address Override, both SMART OAuth
// clients, the module launch URIs, demo seeding. Applying this file yields a
// stack that boots but cannot complete a SMART launch until that bootstrap runs:
// tools/RegisterSmartClients (API half) then tools/BootstrapOpenEmr (database
// half), both idempotent and both taking the front door as their argument.
// -----------------------------------------------------------------------------

import { defineRailway, image, preserve, project, service, volume } from "railway/iac";

// Volumes are provisioned into a specific region; keep every volume in one
// region so private networking stays intra-region. Change in one place.
//
// "us-east4-eqdc4a" BECAUSE THAT IS WHERE RAILWAY PLACES THIS PROJECT, measured, not chosen.
// a separate change had "sfo" to match the RETIRED project's volumes; a separate change first moved it to "us-west2", a
// documented region id (docs.railway.com/deployments/regions). The first successful staging apply
// (a CI run, 88 changes applied) then put ALL NINE services in `us-east4-eqdc4a` -
// no service here declares a region, so each takes the workspace's default - and created every volume
// there too, because a volume follows the region of the service it is attached to (same docs page).
// The declared "us-west2" was not honoured on create, and every later plan proposed
// `~ Update <volume> config.region ("us-east4-eqdc4a" -> "us-west2")` on all six volumes: a migration
// the destructive guard rightly refuses. Read-only describe-environment on staging (2026-10-08): nine
// services `regions: ["us-east4-eqdc4a"]`, six volumes `region: "us-east4-eqdc4a"`. The file now says
// what is true. Moving the project to us-west2 is possible (the docs list every Metal region for every
// plan) but is a maintainer's latency/region decision: it would need a region declared on every
// service AND a volume migration (downtime), and this constant alone cannot do it.
// Once volumes hold data, changing this is destructive: sites-volume-selftest.mjs pins the value.
export const REGION = "us-east4-eqdc4a";

// THE LARGEST VOLUME THE WORKSPACE'S RAILWAY PLAN ALLOWS. A CI run
// applied this file to calm-laughter's staging and Railway refused the whole change set - status
// `failed`, 90 of 90 changes failed - with one diagnostic: "Max size of 5000 MB on current plan. Please
// select a valid size or upgrade". mysql-data and postgres-data were declared at 5120 MB. Every volume
// below is at or under this, and sites-volume-selftest.mjs asserts that for both environments.
// RAISE IT ONLY WITH THE PLAN: after an upgrade, this and the two database volumes can grow (a resize
// up is non-destructive); a volume can never shrink in place, so do not raise a size the plan might
// later lose.
export const MAX_VOLUME_MB = 5000;

// EXPLICIT PINS, BY DECISION - these name a build, they do not follow a moving tag.
// This reverts the `<component>-latest` experiment, because it cost the two properties
// this file exists to provide: it stopped saying which build is live, and it left
// nothing to roll back TO. Rollback is again what DEPLOYMENT.md §6 describes - change
// one line back to an earlier pin below (DEPLOYMENT.md §9). Must match docker-compose.yml
// (DEPLOYMENT.md §1).
const OPENEMR_IMAGE = "docker.io/amarquette/gauntletai:openemr-sha-1b5c2d659fb9";
// What Railway still stores in openemr's build config, per environment - a leftover of source
// builds, inert for an image service. Declared so a plan stops proposing to null it (see the
// openemr service below). EMPTY SINCE a separate change: only the retired project's staging held one; a fresh
// openemr has none, and declaring a DOCKERFILE build on an image service is contradictory on a create.
// An environment not in this map declares no build. Exported for sites-volume-selftest.mjs.
export const OPENEMR_STORED_BUILD_BY_ENV: Record<string, { builder: "DOCKERFILE"; dockerfilePath: string }> = {};
// ---------------------------------------------------------------------------
// ONLY PRODUCTION IS PINNED; STAGING TRACKS `develop` (maintainer ruling 2026-09-23:
// "Only prod should be pinned. Staging needs the latest deployments/merges.").
//
// HOW. Every push to `develop` publishes `<component>-sha-<12>` OF ITS OWN COMMIT for every
// component staging runs (publish-image, publish-proxy-image, publish-observability-images), and
// railway-apply-staging then evaluates this file with STAGING_BUILD_SHA set to that commit. Every
// staging entry below is TRACKS_DEVELOP, which resolves to exactly those tags. So:
//   - staging's graph still names IMMUTABLE tags, and the plan carries a real `source.image` row
//     for each one - which is what makes Railway deploy it, what lets railway-deploy-identity.sh
//     see a new deployment, and what leaves Railway's deployment record naming a build;
//   - production keeps LITERAL pins, and promoting copies the sha staging ran into them. Nothing
//     is rebuilt on the way to `main`, so production runs the bytes staging ran.
//
// WHY NOT A MOVING TAG (`agent-forge-develop`) THAT STAGING FOLLOWS. Considered and rejected: a
// constant string plans NO image row, so Railway would not redeploy when the tag moved and
// deploy-identity would (correctly) report that nothing shipped; the only lever left is
// `railway redeploy`, which replays the captured config; and staging's declaration would
// stop saying which build it runs, leaving production nothing immutable to copy. A separate change already
// paid for a moving tag once, as a silent seven-week downgrade.
//
// STAGING_BUILD_SHA IS READ ONLY WHEN A STAGING ENTRY IS RESOLVED, so a production plan is
// identical with it set, unset or wrong. Unset or malformed while staging IS being rendered, the
// resolver THROWS: a staging plan that cannot say which build it deploys must fail, not guess -
// the rule SIDECAR_IMAGE_FOR applies to an unknown environment. A 40-character sha is cut to 12.
//
// TRACKS_DEVELOP IS REFUSED OUTSIDE STAGING (resolvePin). Production tracking develop would be
// the promotion gate removed by a one-word edit, so it is a thrown error rather than a review
// comment. sites-volume-selftest.mjs asserts both directions.
export const TRACKS_DEVELOP = "tracks-develop";
export const STAGING_ENVIRONMENT = "staging";
export const STAGING_BUILD_SHA_VAR = "STAGING_BUILD_SHA";
// WHERE STAGING'S BUILDS LIVE: GHCR, where GitHub Actions publishes every develop build. This is
// ONLY what TRACKS_DEVELOP resolves to. Production's literals below still name docker.io - the
// earlier Docker Hub builds - and they move to GHCR only by a deliberate promotion that copies the
// exact GHCR string staging ran; the production pin check compares full image strings, so an image
// string staging never ran is refused. OPENEMR_IMAGE is built in the fork and stays on docker.io.
// The package belongs to the MarqSpec organisation: the repository's GITHUB_TOKEN can write to an
// organisation package, not to a user-owned one. Lower case: GHCR owner names are.
// It is named after the product, agent-forge, and is public, so a deploy pulls it without
// registry credentials.
const REGISTRY = "ghcr.io/marqspec/agent-forge";

// `globalThis.process`, not the `process` global: the tsconfig loads no Node types. The CLI
// evaluates this file under Node and passes the job's environment through (measured).
const readEnv = (name: string): string | undefined =>
  (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env?.[name];

export const stagingBuildSha = (): string => {
  const raw = readEnv(STAGING_BUILD_SHA_VAR);
  if (raw === undefined || !/^[0-9a-f]{12}(?:[0-9a-f]{28})?$/.test(raw)) {
    throw new Error(
      `Cannot resolve staging's images: ${STAGING_BUILD_SHA_VAR} is ` +
        `${raw === undefined ? "unset" : `"${raw}"`}. It must be the 12- or 40-character ` +
        `lower-case sha of a develop commit whose push pipeline published its images. ` +
        `railway-apply-staging sets it to the develop commit it deploys; a hand plan must set it explicitly ` +
        `(DEPLOYMENT.md section 9). Refusing rather than guessing which build staging runs.`,
    );
  }
  return raw.slice(0, 12);
};

// One map entry, resolved. A literal is returned unchanged; TRACKS_DEVELOP becomes this develop
// build's tag, and only in staging. `component` is the tag prefix the publish job writes.
export const resolvePin = (component: string, env: string | undefined, pin: string): string => {
  if (pin !== TRACKS_DEVELOP) return pin;
  if (env !== STAGING_ENVIRONMENT) {
    throw new Error(
      `${component}: environment ${env === undefined ? "unset" : `"${env}"`} is declared ` +
        `${TRACKS_DEVELOP}, and only ${STAGING_ENVIRONMENT} may track develop. Every other ` +
        `environment pins an explicit ${component}-sha-<12> that staging has already run.`,
    );
  }
  return `${REGISTRY}:${component}-sha-${stagingBuildSha()}`;
};

// The sidecar. PROMOTED 2026-09-27: production's pin copies agent-forge-sha-49a8a5b5c411,
// the develop build of 49a8a5b5c411. Staging's successful railway-apply-staging of that commit is
// this promotion's merge precondition.
// Previous pin agent-forge-sha-3d8e5b266082 (tag digest sha256:a778f351) - the pin this replaced,
// promoted 2026-09-24 and hand-applied the same day. Its own predecessor,
// agent-forge-sha-60c9495d39ff (sha256:cc631206, built 2026-07-29), is DEPLOYMENT.md §6's older record.
// Must match docker-compose.yml. Production rolls back by naming one of its own recent pins again
// (DEPLOYMENT.md §9).
// Staging ran a hand-built agent-forge-sha-0ce310f30842; that literal, and the
// a separate change "second commit on `staging`" that used to move it, are gone. A separate change,
// a separate change (3d8e5b266082 reached `main` and was hand-applied before it reached
// `develop` - DEPLOYMENT.md §9 *Carrying the pin back to `develop`* is the step that stops that
// gap recurring)
// EXPORTED, with PROXY_IMAGE_BY_ENV, so sites-volume-selftest.mjs can assert every map's shape.
export const SIDECAR_IMAGE_BY_ENV: Record<string, string> = {
  production: "docker.io/amarquette/gauntletai:agent-forge-sha-49a8a5b5c411",
  staging: TRACKS_DEVELOP,
};
// Unknown/unset environment THROWS - see SIDECAR_IMAGE_FOR below. It used to fall back
// to production's pin and call that the conservative direction, which it was while
// production was the only environment CI could reach. Once staging began applying
// unattended the same fallback became a DOWNGRADE of the promotion gate, because staging
// deliberately runs ahead, so the resolver refuses instead.
// `Object.hasOwn`, not bare bracket access - a `Record<string, string>` read for an
// environment named `constructor`/`toString`/`__proto__` yields a truthy inherited
// member, so a naive presence check would hand back that member instead of throwing.
// ---------------------------------------------------------------------------
// OBSERVABILITY. Prometheus + Grafana, metrics only, in BOTH environments.
//
// EMPTY PINS MEAN "NOT DECLARED", DELIBERATELY. An empty pin omits the service from the graph
// entirely rather than deploying something arbitrary, and OBSERVABILITY_FOR requires BOTH to
// be non-empty, so a half-created tier is impossible. PRODUCTION'S ENTRY IS FILLED since a separate change
// 2026-09-24 promotion (carried here, after the pin reached `main` and
// was hand-applied before `develop` had it): see the production entry below for the pin.
//
// STAGING TRACKS develop HERE TOO: both entries are TRACKS_DEVELOP, so each develop push
// deploys the prometheus/grafana images publish-observability-images built from that commit. This
// retires the hand-built prometheus-/grafana-sha-4768e5d79880 pins staging ran from 2026-09-21,
// and with them the "grafana image is N panels behind this tree" gap: from the first develop
// apply, staging's dashboard IS this tree's dashboard. Emptying an entry still means "not
// declared" - that is how staging would drop a component, never a literal pin.
//
// LOKI IS NOT PART OF THIS MAP: it has its own, staging-only one (LOKI_IMAGE_BY_ENV),
// because shipping logs to a reachable store makes "no PHI in logs" load-bearing, and production's
// logs fail it. NOR IS TEMPO: traces have their own staging-only map too
// (TEMPO_IMAGE_BY_ENV). Metrics carry no patient data.
// EXPORTED for sites-volume-selftest.mjs, same reason as DECLARED_SITES_VOLUME below: the
// pins-filled case is the one that proves the sidecar's readiness probe appears WITH the
// service, and it cannot be rendered without temporarily filling them.
// reference: DEPLOYMENT.md, a separate change
export const OBSERVABILITY_IMAGES: Record<string, { prometheus: string; grafana: string }> = {
  // TRACKS develop. Resolved by OBSERVABILITY_FOR to prometheus-/grafana-sha-<develop
  // commit>, which publish-observability-images pushed from that same commit.
  staging: { prometheus: TRACKS_DEVELOP, grafana: TRACKS_DEVELOP },
  // PROMOTED 2026-09-27: the two tags of the develop build of 49a8a5b5c411; staging's
  // successful railway-apply-staging of that commit is the merge precondition. First filled 2026-09-24 (carried here) with the
  // *-sha-3d8e5b266082 pair, which is the rollback target: a pin change, non-destructive.
  // Every develop build since a separate change carries its entrypoint, so with no Loki grafana provisions no
  // Loki datasource. Emptying the entry instead plans deleting both services and prometheus-data
  // from production's graph - a DESTRUCTIVE plan the guard refuses, so that needs its own ruling. sites-volume-selftest.mjs refuses a
  // filled entry that is not CI-shaped, or that names a grafana image older than a separate change, AND refuses
  // production ever going back to empty by accident. Production names a build staging has run, or
  // one of its own recent pins; the two images may differ.
  // THE PIN PLAN FOR THE *NEXT* PROMOTION,: pick a develop commit D whose
  // railway-apply-staging succeeded, and put
  // prometheus-sha-<D12> and grafana-sha-<D12> here - the tags that push published and staging
  // ran. staging -> main carries them to a hand apply. Nothing is rebuilt for production.
  // Check that staging's apply of D went green before promoting it (DEPLOYMENT.md section 9).
  // Before this apply: GRAFANA_ADMIN_USER / GRAFANA_ADMIN_PASSWORD as PRODUCTION shared
  // variables (DEPLOYMENT.md section 9), or the route below serves admin/admin.
  production: {
    prometheus: "docker.io/amarquette/gauntletai:prometheus-sha-49a8a5b5c411",
    grafana: "docker.io/amarquette/gauntletai:grafana-sha-49a8a5b5c411",
  },
};

// The Prometheus TSDB, per environment. Prometheus's own default is 15d with NO size
// cap, which lets the TSDB fill its volume; the size cap is what bounds it.
//
// retentionSize STAYS UNDER HALF OF sizeMB, deliberately: Railway caps a manual backup at 50% of
// the volume, and railway-snapshot-guard.sh takes one of every volume before a non-refreshable
// apply (DEPLOYMENT.md section 10), so a TSDB past half full would make every such apply refuse.
// The cap counts blocks and WAL together. sites-volume-selftest.mjs asserts the ratio.
//
// PRODUCTION'S VALUES ARE A PROPOSAL, small and conservative, and the maintainer's to change:
// staging's TSDB held 1.9 MB of 2048 MB after a day (DEPLOYMENT.md section 10), so 1024 MB is
// ample. Staging keeps its live 2048 MB, because a volume cannot shrink in place.
export const PROMETHEUS_TSDB_BY_ENV: Record<
  string,
  { sizeMB: number; retentionTime: string; retentionSize: string }
> = {
  production: { sizeMB: 1024, retentionTime: "15d", retentionSize: "400MB" },
  staging: { sizeMB: 2048, retentionTime: "15d", retentionSize: "400MB" },
};
// Throws rather than inventing a size: a guessed volume size is a destructive plan row later.
const PROMETHEUS_TSDB_FOR = (env: string | undefined) => {
  if (env !== undefined && Object.hasOwn(PROMETHEUS_TSDB_BY_ENV, env)) {
    return PROMETHEUS_TSDB_BY_ENV[env];
  }
  throw new Error(
    `Cannot size the Prometheus TSDB: environment ${env === undefined ? "unset" : `"${env}"`} ` +
      `declares observability but has no PROMETHEUS_TSDB_BY_ENV entry.`,
  );
};
// Same `Object.hasOwn` reasoning as SIDECAR_IMAGE_FOR: a bare bracket read for an
// environment named `constructor`/`toString` yields a truthy inherited member.
// Unknown environment => no observability. An environment with either pin empty gets none.
const OBSERVABILITY_FOR = (env: string | undefined) => {
  if (env === undefined || !Object.hasOwn(OBSERVABILITY_IMAGES, env)) return undefined;
  const pins = OBSERVABILITY_IMAGES[env];
  // BOTH pins, not the entry: `{ prometheus: "", grafana: "" }` is a truthy object, so
  // testing the entry alone would declare two services with an empty image - the exact
  // half-created tier the empty default exists to prevent. And both, not either: a
  // Grafana with no Prometheus is a dashboard of errors, which reads as a broken deploy
  // rather than an unfinished one.
  if (pins.prometheus === "" || pins.grafana === "") return undefined;
  return {
    prometheus: resolvePin("prometheus", env, pins.prometheus),
    grafana: resolvePin("grafana", env, pins.grafana),
  };
};

// ---------------------------------------------------------------------------
// LOKI: sidecar logs, STAGING ONLY. A map of its own rather than a third
// OBSERVABILITY_IMAGES pin, so Loki can never be promoted as a side effect of promoting
// the metrics tier.
//
// THERE IS NO `production` KEY, AND THAT IS THE CONTROL. The sidecar's production stdout
// carries FHIR Patient ids on every tool call, so shipping it to a queryable store
// would put patient identifiers one Grafana login away. sites-volume-selftest.mjs refuses a
// production key. Enabling production after a separate change lands is one line here plus lifting that
// refusal - nothing else in this file changes.
//
// HOW LOGS GET THERE: direct OTLP push from the sidecar (Observability__LokiOtlpEndpoint,
// already wired and fail-open in Program.cs), not a Railway log drain or a shipper service.
// It is the path both local wirings already use, adds no service beyond Loki itself, and
// is absent - no exporter, no error - wherever the variable is unset.
//
// EMPTY PIN = NOT DECLARED, exactly as OBSERVABILITY_IMAGES: no service, no volume, no
// endpoint on the sidecar, and an empty LOKI_URL on Grafana. ONLY a grafana image built from a
// commit carrying the entrypoint then hides Loki. Every earlier grafana-sha (the retired
// hand-built 4768e5d79880 staging ran included) still shows the Loki datasource and an erroring
// panel, so production's first Grafana pin must be a later build. sites-volume-selftest.mjs
// refuses the known earlier tags wherever Loki is absent. Loki is declared only
// beside a declared metrics tier - a Loki nobody can query is cost with no reader.
//
// STAGING IS EMPTY TOO, BY THE MAINTAINER'S RULING OF 2026-09-24: Tempo goes live on
// staging, Loki waits (Patient ids in sidecar stdout). The mechanism stays whole:
// `staging: TRACKS_DEVELOP` switches it on, from each develop push's loki-sha-<12>, which
// publish-observability-images keeps building. sites-volume-selftest.mjs pins this state.
export const LOKI_IMAGE_BY_ENV: Record<string, string> = {
  staging: "",
};
// Same `Object.hasOwn` reasoning as OBSERVABILITY_FOR.
const LOKI_FOR = (env: string | undefined) => {
  if (OBSERVABILITY_FOR(env) === undefined) return undefined;
  if (env === undefined || !Object.hasOwn(LOKI_IMAGE_BY_ENV, env)) return undefined;
  const pin = LOKI_IMAGE_BY_ENV[env];
  return pin !== "" ? resolvePin("loki", env, pin) : undefined;
};

// ---------------------------------------------------------------------------
// TEMPO: sidecar traces, STAGING ONLY - the disposition, applied rather than decided
// twice. A map of its own for the same reason as Loki's: promoting the metrics tier must never
// promote a trace store as a side effect.
//
// THERE IS NO `production` KEY, AND THAT IS THE CONTROL. Unlike logs, spans ARE scrubbed before they
// leave the process (SpanPhiScrubber: URLs keep their origin and an {id}-templated path, and query
// strings, client addresses and free-text status never leave), so the PHI argument is weaker here
// than for Loki. Production still gets none, by the maintainer's ruling that scoped
// every log/trace store to staging - that stands independently of whether production's metrics
// tier (OBSERVABILITY_IMAGES) is filled, which it now is. sites-volume-selftest.mjs
// refuses a production key; enabling it is one line here plus lifting that refusal, and is the
// maintainer's call.
//
// HOW SPANS GET THERE: direct OTLP/HTTP push from the sidecar (Observability__TraceOtlpEndpoint),
// the same path both local wirings use. Unset, Program.cs registers no trace exporter at all.
//
// EMPTY PIN = NOT DECLARED, exactly as LOKI_IMAGE_BY_ENV: no service, no volume, no endpoint on the
// sidecar, and an empty TEMPO_URL on Grafana, which a grafana image built from a commit carrying
// a separate change reads as "provision no Tempo datasource". An EARLIER grafana image carries no Tempo
// datasource at all, so absence is transparent there too - there is no stale-image hazard to guard.
// Tempo is declared only beside a declared metrics tier, because Grafana is its only reader.
//
// STAGING TRACKS develop: the first develop apply after it creates Tempo there from that
// push's tempo-sha-<12>. Loki does NOT, by the 2026-09-24 ruling (LOKI_IMAGE_BY_ENV above). A separate change declared this pin empty, in the old model.
export const TEMPO_IMAGE_BY_ENV: Record<string, string> = {
  staging: TRACKS_DEVELOP,
};
// Same `Object.hasOwn` reasoning as OBSERVABILITY_FOR.
const TEMPO_FOR = (env: string | undefined) => {
  if (OBSERVABILITY_FOR(env) === undefined) return undefined;
  if (env === undefined || !Object.hasOwn(TEMPO_IMAGE_BY_ENV, env)) return undefined;
  const pin = TEMPO_IMAGE_BY_ENV[env];
  return pin !== "" ? resolvePin("tempo", env, pin) : undefined;
};

// ---------------------------------------------------------------------------
// SECURITY PLATFORM. The deterministic replayer image, STAGING ONLY.
//
// THERE IS NO `production` KEY. The runner also refuses the production front door in
// code (security-platform/security_platform/allowlist.py), so a pin here could not point
// a run at production by itself — and the service is still not declared there.
//
// NO PUBLIC DOMAIN. The image's default command is `serve`: GET /health, and POST /run
// is 404. Concrete cases belong to a separate change and live runs against the front door to a separate change; this
// service shares the project private network with MySQL and the sidecar (DEPLOYMENT.md).
//
// NO VOLUME, NO MODEL KEY. The maintainer approved this staging service and its idle cost
// (2026-09-28). Production is not approved: a production key needs its own ruling.
// DEPLOYMENT.md, SECURITY-PLATFORM.md
export const SECURITY_PLATFORM_IMAGE_BY_ENV: Record<string, string> = {
  staging: TRACKS_DEVELOP,
};
const SECURITY_PLATFORM_FOR = (env: string | undefined) => {
  if (env === undefined || !Object.hasOwn(SECURITY_PLATFORM_IMAGE_BY_ENV, env)) return undefined;
  const pin = SECURITY_PLATFORM_IMAGE_BY_ENV[env];
  return pin !== "" ? resolvePin("security-platform", env, pin) : undefined;
};

// FAILS CLOSED ON AN UNKNOWN ENVIRONMENT. This used to fall back to production's pin
// and call that "the conservative direction", which was true while production was the
// only environment CI could apply to. It stopped being true the moment
// railway-apply-staging began applying unattended (on `staging` merges then, on every
// `develop` push): staging deliberately runs a NEWER build, so the fallback
// would plan an in-place DOWNGRADE of the promotion gate to production's older image - and
// railway-destructive-guard.sh passes that, because overwriting an image tag destroys
// no volume. The guard is structurally unable to catch it, so the resolver must.
// An apply that cannot say which environment it is for must fail, not guess.
const SIDECAR_IMAGE_FOR = (env: string | undefined) => {
  if (env !== undefined && Object.hasOwn(SIDECAR_IMAGE_BY_ENV, env)) {
    return resolvePin("agent-forge", env, SIDECAR_IMAGE_BY_ENV[env]);
  }
  const known = Object.keys(SIDECAR_IMAGE_BY_ENV).join(", ");
  throw new Error(
    `Cannot resolve a sidecar image: environment is ${env === undefined ? "unset" : `"${env}"`}, ` +
      `and the known environments are ${known}. A Railway project token selects the ` +
      `environment, so an unset context usually means the job extends neither ` +
      `.railway-staging nor .railway-production, or the CLI did not pass the environment ` +
      `into the authoring context. Refusing rather than defaulting to production's pin, ` +
      `which would silently downgrade staging.`,
  );
};

// ---------------------------------------------------------------------------
// THE MODEL, PER ENVIRONMENT. Llm__Provider picks the ILlmProvider the sidecar boots
// with (Anthropic or Gemini), and the model and its prices travel with it: nothing validates a
// price against a model, so a model change that leaves its prices behind mis-reports
// agentforge_llm_cost_usd_total in silence. Change a row's four values together, here
// and in docker-compose.yml and .env.example. The key is NOT here: Llm__ApiKey stays preserve()
// below, and it must be a key for the row's provider, set in Railway BEFORE an apply that
// switches the row (staging applies unattended on every develop push).
//
// BOTH ROWS ARE ANTHROPIC, with the values every environment ran (claude-sonnet-5's
// list rate). Staging moves to Gemini in a follow-up once its key exists - DEPLOYMENT.md section 3
// has the steps. A Gemini row is for synthetic data only: the free tier may use prompts and
// responses to improve Google's products, so it is never production's and never sees PHI; a
// free-tier row prices both directions at "0", which reports a true zero cost.
//
// EXPORTED for sites-volume-selftest.mjs, which asserts every environment declares a provider.
// An unknown environment THROWS (LLM_FOR), for SIDECAR_IMAGE_FOR's reason: a guessed model is
// a silent change to every clinical answer.
// ---------------------------------------------------------------------------
export const LLM_PROVIDERS = ["Anthropic", "Gemini"] as const;
export type LlmProvider = (typeof LLM_PROVIDERS)[number];

export const LLM_BY_ENV: Record<
  string,
  { provider: LlmProvider; model: string; inputPricePerMillionUsd: string; outputPricePerMillionUsd: string }
> = {
  production: {
    provider: "Anthropic",
    model: "claude-sonnet-5",
    inputPricePerMillionUsd: "2.00",
    outputPricePerMillionUsd: "10.00",
  },
  staging: {
    provider: "Anthropic",
    model: "claude-sonnet-5",
    inputPricePerMillionUsd: "2.00",
    outputPricePerMillionUsd: "10.00",
  },
};

const LLM_FOR = (env: string | undefined) => {
  if (env !== undefined && Object.hasOwn(LLM_BY_ENV, env)) {
    return LLM_BY_ENV[env];
  }
  throw new Error(
    `Cannot resolve the model: environment ${env === undefined ? "unset" : `"${env}"`} has no ` +
      `LLM_BY_ENV entry, and the known environments are ${Object.keys(LLM_BY_ENV).join(", ")}. ` +
      `Refusing rather than guessing which provider and model serve clinical answers.`,
  );
};

// ---------------------------------------------------------------------------
// OPENEMR SITES VOLUME. `openemr-sites`, in every environment,.
//
// The retired project ran OpenEMR on two hand-created 50000 MB volumes
// (`openemr-volume-ceSx` in production, `openemr-volume-o8g8` in staging), pinned here by a
// LEGACY_SITES_VOLUME map with literal regions so an edit to REGION could not propose
// recreating them. `calm-laughter` has neither, so the map would only have
// created two empty 50 GB volumes under legacy names. The migration happens by
// construction: both environments get the declared name, as every other volume here is
// declared by one name across environments (a volume is project-level, with one instance per
// environment). Changing the name or region once data exists is destructive - the guard
// refuses the plan.
export const DECLARED_SITES_VOLUME = { name: "openemr-sites", region: REGION, sizeMB: 2048 };

// THE PROXY PINS AN IMAGE, like every other service. It used to build
// from a BRANCH - and from GitHub's `agent-forge-copilot`, which nothing mirrors this project
// to, so a reverse-proxy change merged on the earlier CI host rebuilt neither front door and nothing
// reported it. `publish-proxy-image` closed that on 2026-09-21; this map consumes it.
//
// THE TWO ENVIRONMENTS NO LONGER SHARE A PIN. Staging tracks develop like every other
// service, so its front door is the proxy-sha-<12> each develop push publishes; production keeps
// a literal. Both ran proxy-sha-b73635187bcf from a separate change, and production kept it until
// a separate change promoted proxy-sha-49a8a5b5c411 (rollback -> b73635187bcf, one of production's own last pins
// ). Promoting the front door is
// the same diff the sidecar's is: a develop build staging has run, copied into production's slot.
//
// a separate change IS A BUILD CONCERN, NOT A DEPLOY ONE: only `main` (60c9495) still COPYs a
// repo-root-relative path and lacks 10-resolver.envsh. No apply builds from a branch any more,
// and every develop build has had neither problem since 0a4c729.
export const PROXY_IMAGE_BY_ENV: Record<string, string> = {
  production: "docker.io/amarquette/gauntletai:proxy-sha-49a8a5b5c411",
  staging: TRACKS_DEVELOP,
};

// Fails closed on an unknown environment, for the reason SIDECAR_IMAGE_FOR does: a default
// here would silently point one environment's front door at another's image.
const PROXY_IMAGE_FOR = (env: string | undefined) => {
  if (env !== undefined && Object.hasOwn(PROXY_IMAGE_BY_ENV, env)) {
    return resolvePin("proxy", env, PROXY_IMAGE_BY_ENV[env]);
  }
  throw new Error(
    `Cannot resolve a reverse-proxy image: environment is ${env === undefined ? "unset" : `"${env}"`}, ` +
      `and the known environments are ${Object.keys(PROXY_IMAGE_BY_ENV).join(", ")}. ` +
      `Refusing rather than defaulting, which would point one environment's front door at ` +
      `another environment's build.`,
  );
};

// Railway resolves ${{service.VAR}} references at deploy time. These are plain
// strings, not template literals - `${{` is literal text here.
const PROXY_DOMAIN = "${{reverse-proxy.RAILWAY_PUBLIC_DOMAIN}}";
const OPENEMR_HOST = "${{openemr.RAILWAY_PRIVATE_DOMAIN}}";
const SIDECAR_HOST = "${{agent-forge-api.RAILWAY_PRIVATE_DOMAIN}}";
const POSTGRES_HOST = "${{postgres.RAILWAY_PRIVATE_DOMAIN}}";
const PROMETHEUS_HOST = "${{prometheus.RAILWAY_PRIVATE_DOMAIN}}";
// Only referenced from inside an `observability ?` guard - like PROMETHEUS_HOST, this
// reference cannot resolve on an environment with no `grafana` service in the graph.
const GRAFANA_HOST = "${{grafana.RAILWAY_PRIVATE_DOMAIN}}";
// Only referenced from inside a `loki ?` guard, for the same reason.
const LOKI_HOST = "${{loki.RAILWAY_PRIVATE_DOMAIN}}";
// Only referenced from inside a `tempo ?` guard, for the same reason.
const TEMPO_HOST = "${{tempo.RAILWAY_PRIVATE_DOMAIN}}";
const MYSQL_HOST = "${{mysql.RAILWAY_PRIVATE_DOMAIN}}";

// The front door. LOAD-BEARING (DEPLOYMENT.md §2): this exact value must also be
// OpenEMR's `site_addr_oath`, or every SMART launch dies on "Aud parameter did
// not match authorized server" before a login form is reached. site_addr_oath is
// database state - the bootstrap sets it, this file cannot.
const FRONT_DOOR = "https://" + PROXY_DOMAIN;

// OpenEMR document category id -> sidecar docType, for the ingest forwarder. Keyed by
// the category's numeric id, which is DATABASE STATE, so the id is only as good as its source:
//   "2" = `Lab Report`. Fixed by stock OpenEMR's install schema - the fork's sql/database.sql
//         inserts `categories` row (2, 'Lab Report', ...) on first-boot setup, identically in
//         every environment. Nothing in the fork's module, seeds or bootstrap creates, renames
//         or renumbers categories (module installs such as faxsms append at MAX(id)+1).
// NO `intake_form` ENTRY, BY DESIGN: neither stock OpenEMR nor anything the fork seeds creates
// an intake category, and mapping one the install happens to have (`Patient Information`,
// `Medical Record`) would be a guess; the self-contained /evidence/ask path still takes intake
// forms. Same map in both environments, because the id comes from the install schema rather than
// from either environment - DEPLOYMENT.md §4 says how to confirm it against a live install.
const INGEST_CATEGORY_MAP = JSON.stringify({ "2": "lab_pdf" });

export default defineRailway((ctx) => {
  // -------------------------------------------------------------------------
  // Volumes. Managed volumes mount EMPTY - see SWARM_MODE on openemr below.
  // -------------------------------------------------------------------------
  // At the plan's cap (MAX_VOLUME_MB): 5120 was refused on calm-laughter.
  const mysqlData = volume("mysql-data", { region: REGION, sizeMB: MAX_VOLUME_MB });
  // The same declared volume in every environment - see DECLARED_SITES_VOLUME.
  const sitesVolume = volume(DECLARED_SITES_VOLUME.name, {
    region: DECLARED_SITES_VOLUME.region,
    sizeMB: DECLARED_SITES_VOLUME.sizeMB,
  });
  const postgresData = volume("postgres-data", { region: REGION, sizeMB: MAX_VOLUME_MB });
  // Small, but NOT optional: the pending-SMART-launch cookie is DataProtection-
  // encrypted, and an in-memory key ring cannot decrypt it after a restart -
  // which surfaces as "No pending SMART launch" on the callback.
  const dataProtectionKeys = volume("dataprotection-keys", { region: REGION, sizeMB: 1024 });

  // -------------------------------------------------------------------------
  // Data tier. Pinned images rather than Railway's managed database helpers,
  // deliberately:
  //   - postgres MUST be pgvector (vector column + HNSW index; the EF migrations
  //     create the extension). The managed helper is stock Postgres.
  //   - mysql stays an image for parity with docker-compose.yml, so the OpenEMR
  //     fork sees identical MYSQL_* wiring in both environments.
  //
  // preserve() = "keep the value already set in Railway". Secrets are NEVER
  // written into this file; set them once per environment and every subsequent
  // apply leaves them alone. On a FIRST apply they are unset - see DEPLOYMENT.md.
  // -------------------------------------------------------------------------
  // AN IMAGE SERVICE, AND THAT IS WHAT IS LIVE. Do not "reconcile" this to
  // `mysqlDatabase("mysql", { region })`. A separate change did, and its apply was refused at the
  // guard with five destructive rows (pipeline 24247, job 78064) - correctly: a
  // DatabaseConfig carries no variables and no mount, so the declaration said the four
  // live MYSQL_* variables and the mysql-data mount should not exist.
  //
  // The "both environments run a managed database" premise was a CLI artefact. Read from
  // the API rather than from a plan, environment.config shows source.image "mysql:9.4",
  // four MYSQL_* variables and mysql-data at /var/lib/mysql in both environments, with a
  // null templateServiceId - and Railway's schema carries no database marker at all.
  // `database.mysql` appears only in CLI 5.57.2's plan output, inferred from the image
  // name; 5.57.12 and 5.59.0 emit no mysql row against this declaration. Production
  // additionally holds a MYSQL_URL variable; left undeclared, a production apply proposed deleting
  // it, so it is preserve()'d below.
  //
  // DO NOT BUMP THE IMAGE TAG ON THIS ONE SERVICE WITHOUT CHECKING THE ENVIRONMENT AFTERWARDS.
  // Under 5.57.12/5.59.0 `source.image` is not compared for mysql - controls on 2026-09-21, each
  // applying the same edit to mysql and to postgres: an image change emitted a postgres row and NO
  // mysql row, while an added variable and a changed mount path emitted rows for BOTH. So the
  // resource is in the comparison and one field of it is not: an image bump here plans clean,
  // applies, and leaves the old tag running with nothing reporting it. Verify that one field
  // against `environment(id:){ config }`. env and volumeMounts plan normally.
  // DEPLOYMENT.md §9, §10
  const mysql = service("mysql", {
    source: image("mysql:9.4"),
    env: {
      // Shared variables, NOT preserve(): mysql and openemr must hold the SAME
      // password, and two independent preserve() values can silently diverge -
      // compose derived both from one ${MYSQL_ROOT_PASSWORD}, and dropping to
      // per-service secrets would be a real regression in safety.
      MYSQL_ROOT_PASSWORD: ctx.shared.MYSQL_ROOT_PASSWORD,
      MYSQL_DATABASE: "openemr",
      MYSQL_USER: "openemr",
      MYSQL_PASSWORD: ctx.shared.MYSQL_PASSWORD,
      // Production only, set by hand; nothing reads it. preserve(), not a reference: the live value
      // is a literal no reference reproduces, so a reference would rewrite it.
      MYSQL_URL: preserve(),
    },
    volumeMounts: { "/var/lib/mysql": mysqlData },
  });

  const postgres = service("postgres", {
    source: image("pgvector/pgvector:pg17"),
    env: {
      POSTGRES_DB: "agentforge",
      POSTGRES_USER: "agentforge",
      // Shared, matching the sidecar connection string below. A service-scoped
      // preserve() here would be a DIFFERENT variable, and the sidecar could not connect.
      POSTGRES_PASSWORD: ctx.shared.POSTGRES_PASSWORD,
      // A subdirectory, never the mount point: a Railway volume is ext4, whose
      // `lost+found` makes the mount non-empty, and `initdb` refuses that.
      // Compose needs no equivalent - named volumes have no `lost+found`.
      // reference: DEPLOYMENT.md §9 "Railway-specific gotchas"
      PGDATA: "/var/lib/postgresql/data/pgdata",
    },
    volumeMounts: { "/var/lib/postgresql/data": postgresData },
  });

  // -------------------------------------------------------------------------
  // OpenEMR - this project's FORK, not stock. Carries the core launch patches
  // (SessionUtil launch-bridge cookie, AuthorizationController, auth.inc.php)
  // on top of oe-module-agentforge. Stock openemr/openemr plus the module does
  // NOT reproduce a working launch. Published by the fork's own pipeline.
  // -------------------------------------------------------------------------
  const openemr = service("openemr", {
    source: image(OPENEMR_IMAGE),
    // A build is declared only where OPENEMR_STORED_BUILD_BY_ENV lists the environment, which since
    // The retired project's staging held a stale Dockerfile builder that an apply
    // could not null, so declaring it was how the plan converged. A fresh openemr
    // has none. sites-volume-selftest.mjs pins that no image service declares a build.
    // DEPLOYMENT.md §9
    ...(Object.hasOwn(OPENEMR_STORED_BUILD_BY_ENV, ctx.environment ?? "")
      ? { build: OPENEMR_STORED_BUILD_BY_ENV[ctx.environment as string] }
      : {}),
    env: {
      MYSQL_HOST: MYSQL_HOST,
      MYSQL_PORT: "3306",
      // Same shared values the mysql service reads - see the note there.
      MYSQL_ROOT_PASS: ctx.shared.MYSQL_ROOT_PASSWORD,
      MYSQL_USER: "openemr",
      MYSQL_PASS: ctx.shared.MYSQL_PASSWORD,
      MYSQL_DATABASE: "openemr",
      OE_USER: "admin",
      OE_PASS: preserve(),
      // Volumes mount EMPTY - they do not inherit image contents. The OpenEMR
      // image only restores its sites/ skeleton from /swarm-pieces (and runs
      // first-boot setup) when SWARM_MODE=yes and the replica elects itself
      // leader; without this it crash-loops on a missing sites/default/sqlconf.php.
      // Single replica => always leader => safe. reference: DEPLOYMENT.md §7
      SWARM_MODE: "yes",
      // A fresh OpenEMR ships the REST and FHIR APIs OFF, so RegisterSmartClients -
      // the first bootstrap step - dies on 404 "OpenEMR Error: API is disabled". The
      // image applies every OPENEMR_SETTING_<global> to the `globals` table on EVERY
      // boot (docker/release/openemr.sh -> setGlobalSettings), which is the only route
      // that works here: Railway's MySQL is private, so a workstation cannot run the
      // equivalent UPDATE. reference: DEPLOYMENT.md §4, a separate change
      OPENEMR_SETTING_rest_api: "1",
      OPENEMR_SETTING_rest_fhir_api: "1",
      OPENEMR_SETTING_rest_system_scopes_api: "1",
      // Set live in production and declared nowhere, so every plan proposed deleting
      // them - two destructive rows for what is ordinary config. Declared here
      // instead of removed live, because removing them is the change that needs an apply
      // and these do not.
      //
      // TZ drives PHP's date handling and therefore every appointment time the copilot
      // reads back; staging had it unset, which is the sort of difference that surfaces
      // as one environment disagreeing about a clinic day.
      TZ: "America/Chicago",
      // A counter bumped BY HAND to force a redeploy, because a Railway redeploy can
      // replay stale config. Declaring it pins it: bumping it is now a file edit
      // and an apply, which is the behaviour a file-as-source-of-truth should have. It
      // goes away, not before - it is a live workaround, not decoration.
      DEPLOY_NONCE: "5",
      // The module's document-ingestion Background Service (every 2 min) forwards new uploads
      // to the sidecar and DOES NOTHING - silently - unless BOTH of these resolve
      // (DocumentIngestService::run in the fork's oe-module-agentforge). Neither was declared,
      // so staging never ingested a document. A non-empty `agentforge_ingest_uri` /
      // `agentforge_ingest_category_map` global saved on the module's config page WINS over
      // these; a blank one falls back to them.
      //
      // The sidecar's PRIVATE address, never the front door: /documents/ingest carries no token,
      // trusts its private-network origin, and the proxy 404s it (W2-D17). 8080 is the sidecar's
      // PORT below; no /agentforge prefix, because UsePathBase leaves an unprefixed path routable.
      AGENTFORGE_INGEST_URI: "http://" + SIDECAR_HOST + ":8080/documents/ingest",
      AGENTFORGE_INGEST_CATEGORY_MAP: INGEST_CATEGORY_MAP,
    },
    volumeMounts: { "/var/www/localhost/htdocs/openemr/sites": sitesVolume },
  });

  // -------------------------------------------------------------------------
  // Observability. Undefined unless OBSERVABILITY_IMAGES carries BOTH pins for
  // THIS environment - see the constant at the top of this file. Resolved HERE, above the
  // sidecar, because the sidecar's readiness probe has to be gated on the same answer: the
  // services themselves are declared further down.
  // -------------------------------------------------------------------------
  const observability = OBSERVABILITY_FOR(ctx.environment);
  // Loki: undefined unless this environment has a Loki pin AND the metrics tier.
  const loki = LOKI_FOR(ctx.environment);
  // Tempo: undefined unless this environment has a Tempo pin AND the metrics tier.
  const tempo = TEMPO_FOR(ctx.environment);

  // -------------------------------------------------------------------------
  // The .NET 10 sidecar / BFF. The map is keyed PER ENVIRONMENT - see
  // SIDECAR_IMAGE_BY_ENV at the top of this file - and each environment resolves to an
  // immutable `-sha-<12>` build: production's literal pin, staging's develop build,
  // so the two run different, NAMED images ("build once, deploy that exact artifact" -
  // DEPLOYMENT.md §5). Staging is ahead of production deliberately: it is the gate that
  // promotes into it. An unknown or unset environment THROWS rather than resolving to
  // production's entry - see SIDECAR_IMAGE_FOR for why that fallback stopped being the
  // conservative direction once staging began applying unattended.
  //
  // `ctx.environment`, not `ctx.environmentName`: createRailwayContext computes
  // `input.environment ?? input.environmentName` and assigns BOTH fields that one
  // value, so they can never disagree - and where the inputs do, `environment` is
  // the one that won. Reading the other first implied the opposite precedence.
  // -------------------------------------------------------------------------
  const llm = LLM_FOR(ctx.environment);
  const sidecar = service("agent-forge-api", {
    source: image(SIDECAR_IMAGE_FOR(ctx.environment)),
    env: {
      PORT: "8080",

      // Both MUST be the front door, never a service's own hostname: BaseUrl
      // drives the SMART `aud` and the authorize URL; PublicBaseUrl drives the
      // OAuth redirect_uri. See DEPLOYMENT.md §2.
      OpenEmr__BaseUrl: FRONT_DOOR,
      Bff__PublicBaseUrl: FRONT_DOOR + "/agentforge",
      Bff__PathBase: "/agentforge",
      OpenEmr__Site: "default",

      // Railway terminates TLS at the edge, so the public origin is real HTTPS.
      // The AllowInsecureHttpForLocalDevelopment escape hatches the compose stack
      // needs are deliberately NOT set here.

      OpenEmr__ClientId: preserve(),
      OpenEmr__ClientSecret: preserve(),
      OpenEmrAgenda__ClientId: preserve(),
      OpenEmrAgenda__ClientSecret: preserve(),

      // The agenda/roster client has its OWN scope list and does not inherit the
      // patient list above - omitting it binds AgendaOpenEmrOptions with a null
      // Scopes and the roster launch cannot request anything. Same contiguity and
      // casing rules apply. reference: DEPLOYMENT.md section 3
      OpenEmrAgenda__Scopes__0: "openid",
      OpenEmrAgenda__Scopes__1: "fhirUser",
      OpenEmrAgenda__Scopes__2: "launch",
      OpenEmrAgenda__Scopes__3: "api:fhir",
      OpenEmrAgenda__Scopes__4: "user/Appointment.read",
      OpenEmrAgenda__Scopes__5: "user/Patient.read",

      // Contiguous 0-based list - a GAP SILENTLY TRUNCATES the bound array at the
      // first missing index. Casing matters: patient/encounter.read is rejected.
      // patient/Binary.read (index 9) is what makes click-to-source work: without
      // it on the REGISTERED client, OpenEMR's finalizeScopes drops it and the
      // document fetch 401s, surfacing as a misleading 404. patient/Appointment.read
      // (index 15) is the same trap one layer up: FR-AUTH-2's relationship gate
      // reads the clinic day's appointments on EVERY launch, and without it that
      // search 401s and the launch is refused 403.
      OpenEmr__Scopes__0: "openid",
      OpenEmr__Scopes__1: "fhirUser",
      OpenEmr__Scopes__2: "launch",
      OpenEmr__Scopes__3: "launch/patient",
      OpenEmr__Scopes__4: "api:fhir",
      OpenEmr__Scopes__5: "patient/Patient.read",
      OpenEmr__Scopes__6: "patient/Encounter.read",
      OpenEmr__Scopes__7: "patient/Observation.read",
      OpenEmr__Scopes__8: "patient/DocumentReference.read",
      OpenEmr__Scopes__9: "patient/Binary.read",
      OpenEmr__Scopes__10: "patient/Condition.read",
      OpenEmr__Scopes__11: "patient/AllergyIntolerance.read",
      OpenEmr__Scopes__12: "patient/MedicationRequest.read",
      OpenEmr__Scopes__13: "patient/Procedure.read",
      OpenEmr__Scopes__14: "patient/DiagnosticReport.read",
      OpenEmr__Scopes__15: "patient/Appointment.read",

      // [Required] + ValidateOnStart: the sidecar CANNOT boot without a real key.
      // Unset here is a crash-loop, not a degraded mode. It must be a key for
      // Llm__Provider's provider, which LLM_BY_ENV sets per environment.
      Llm__ApiKey: preserve(),
      Llm__Provider: llm.provider,
      Llm__Model: llm.model,

      // SHARED VARIABLE, NOT preserve(): the maintainer's own ruling on the hand-set
      // key found missing - "I would rather it be a shared variable referenced from
      // source." Optional at the type level, so an empty/unset shared value degrades to
      // sparse-only retrieval (unranked) rather than a crash-loop - see DEPLOYMENT.md §9
      // Secrets. Must exist as an environment-level shared variable in BOTH environments
      // BEFORE an apply that carries this, or the reference resolves empty and retrieval goes
      // quietly keyless again (staging applies unattended on every develop push).
      Cohere__ApiKey: ctx.shared.COHERE_API_KEY,
      // The row's prices, which travel with its model: see LLM_BY_ENV.
      Llm__InputPricePerMillionTokensUsd: llm.inputPricePerMillionUsd,
      Llm__OutputPricePerMillionTokensUsd: llm.outputPricePerMillionUsd,

      // POSTGRES_PASSWORD is a shared variable on the environment, referenced so
      // the password lives in exactly one place for both postgres and the sidecar.
      AgentForgeData__ConnectionString:
        "Host=" + POSTGRES_HOST + ";Port=5432;Database=agentforge;Username=agentforge;Password=${{shared.POSTGRES_PASSWORD}}",

      DataProtection__KeyRingPath: "/keys",

      // The third dependency NFR-HEALTH-1 names; /ready also checks the vector index, which the
      // AgentForgeData__ConnectionString above configures in every environment.
      // SET ONLY WHERE PROMETHEUS IS ACTUALLY
      // DECLARED, and the spread is what makes that true rather than intended: the key is
      // absent entirely on an environment with no observability pin, where
      // ObservabilityHealthCheck reports Degraded - an honest "never contacted", HTTP 200
      // (NFR-REL-2, ARCHITECTURE.md D17). Setting it unconditionally would be the opposite
      // error to the one this fixes: a hardcoded URL for a service that does not exist turns
      // every /ready into a 503, and `${{prometheus.*}}` cannot resolve where no `prometheus`
      // service is in the graph. That 503 is now prompt rather than 100 seconds late
      // (Readiness__ProbeTimeout), which makes it cheaper to hit and no less wrong - it is how
      // staging, the environment that HAS the tier, is the only one whose /ready fails. The
      // private hostname, never the front door - Prometheus has no auth and no domain (see the
      // service below).
      ...(observability
        ? { Observability__PrometheusHealthUrl: "http://" + PROMETHEUS_HOST + ":9090/-/healthy" }
        : {}),
      // Log export to Loki - ONLY where Loki is declared. Unset, Program.cs registers no
      // exporter and logs nothing about it. NOT a readiness dependency: /ready never probes Loki.
      ...(loki ? { Observability__LokiOtlpEndpoint: "http://" + LOKI_HOST + ":3100/otlp/v1/logs" } : {}),
      // Trace export to Tempo - ONLY where Tempo is declared, for the same reasons. Unset,
      // Program.cs registers no trace exporter. Observability__TraceConsoleExporter is set NOWHERE
      // here, so no deployed sidecar writes spans to stdout. Not a readiness dependency.
      ...(tempo ? { Observability__TraceOtlpEndpoint: "http://" + TEMPO_HOST + ":4318/v1/traces" } : {}),
    },
    volumeMounts: { "/keys": dataProtectionKeys },
  });

  // -------------------------------------------------------------------------
  // THE FRONT DOOR. The ONLY service with a public domain - the one-origin
  // invariant (DEPLOYMENT.md §2) is what makes the SMART launch work at all: a
  // launch whose /launch and /callback land on different hosts loses its session
  // cookie. Everything else is reachable only over private networking.
  //
  // PULLED, NOT BUILT FROM SOURCE - since a separate change step 2. It was built from
  // reverse-proxy/Dockerfile here until then, on the reasoning that the image is only a
  // few lines over stock nginx; what that actually bought was a front door with no version
  // pin. `publish-proxy-image` builds it now and this service pulls the result.
  // NOTE a `proxy-latest` tag DOES exist in the Docker
  // Hub repo and docker-compose.yml still names it - it is a hand-pushed workstation
  // build, and `publish-proxy-image` deliberately does not move it. Nothing deployed
  // pulls it. "Not published by CI" rather than "not published".
  //
  // THIS SERVICE PINS AN IMAGE, like every other one - see PROXY_IMAGE_FOR at the top of
  // this file. Until a separate change it was the exception: it built from a BRANCH, and from
  // GitHub's `agent-forge-copilot`, which nothing mirrors this project to. So its branch
  // WAS its version pin, it moved without a diff in this file, and a reverse-proxy change
  // merged on the earlier CI host rebuilt neither front door while nothing reported it. Both front
  // doors were frozen at GitHub's 2026-09-17 tree for four days on exactly that.
  //
  // WHAT THAT MEANS NOW: staging's front door moves with every develop push, from the
  // proxy-sha-<12> that push published, and production's moves only by a re-pin
  // here, reviewable in a diff, exactly as the sidecar's does.
  //
  // KEEP THIS, IT OUTLIVED THE BRANCH BUILD: `main`'s reverse-proxy/Dockerfile
  // still COPYs `reverse-proxy/nginx.conf.template` - a REPO-ROOT-relative path written
  // when the build context was the repo root - and does not contain 10-resolver.envsh at
  // all. That is why `publish-proxy-image` builds with `reverse-proxy` as its context and
  // why reconciling the two trees is still worth doing. It is no longer a way to take a
  // front door down on an apply, because no apply builds from a branch any more; it is
  // now only a reason a `main`-sourced build would fail.
  // See DEPLOYMENT.md section 9 and a separate change.
  // -------------------------------------------------------------------------
  const proxy = service("reverse-proxy", {
    source: image(PROXY_IMAGE_FOR(ctx.environment)),
    env: {
      // Railway injects PORT; the nginx template listens on it directly.
      OPENEMR_UPSTREAM: OPENEMR_HOST + ":80",
      SIDECAR_UPSTREAM: SIDECAR_HOST + ":8080",
      // Grafana at /grafana on the front door, so the dashboard shares this origin
      // instead of needing a published domain of its own.
      //
      // A SPREAD, NOT A PLAIN KEY, for exactly the reason the sidecar's
      // Observability__PrometheusHealthUrl is one: `${{grafana.*}}` cannot resolve on an
      // environment where no `grafana` service is in the graph - any environment whose
      // OBSERVABILITY_IMAGES pins are not both filled. Absent here, the proxy image's own empty
      // ENV GRAFANA_UPSTREAM applies and nginx answers /grafana with 404 rather than 502 -
      // reverse-proxy/nginx.conf.template carries the rendering detail.
      //
      // Port 3000 is GF_SERVER_HTTP_PORT on the grafana service below, not Railway's injected
      // PORT; Grafana reads that one. The private domain, never the front door - this hop is
      // container-to-container.
      ...(observability ? { GRAFANA_UPSTREAM: GRAFANA_HOST + ":3000" } : {}),
      // Docker's embedded DNS (127.0.0.11) does not exist here. Left EMPTY on
      // purpose: reverse-proxy/10-resolver.envsh derives the real resolver from
      // the container's /etc/resolv.conf at start, which is correct on Railway
      // AND under Docker. Set a value only to override that detection.
      DNS_RESOLVER: "",
    },
    // NO `domains:` ENTRY, AND THAT IS A KNOWN GAP - not an oversight.
    //
    // Railway does not assign a domain automatically, and IaC cannot declare a
    // GENERATED *.up.railway.app domain (the docs exclude them from this file in
    // both apply and pull directions). So after a first apply someone must click
    // Settings -> Networking -> Generate Domain on this service, then redeploy
    // agent-forge-api so it resolves RAILWAY_PUBLIC_DOMAIN. Until then FRONT_DOOR
    // is degenerate and no SMART launch can work.
    //
    // NOT WITH `railway redeploy` - it replays the PREVIOUS deployment's captured
    // configuration rather than re-resolving anything, so it would report SUCCESS and
    // leave RAILWAY_PUBLIC_DOMAIN exactly as degenerate as it was. A variable
    // change or a re-apply creates a genuinely new deployment instead; confirm the
    // deployment id actually moved afterwards (DEPLOYMENT.md section 7 and section 9).
    //
    // That click is a live dashboard edit the drift job CANNOT detect, because
    // generated domains are outside the planned graph. Accepted deliberately as
    // the no-DNS option and written down in DEPLOYMENT.md §9. Declaring a custom
    // hostname here instead - `domains: [{ domain: "<host>", port: 8080 }]` with
    // FRONT_DOOR built from that literal - puts it back under source control and
    // drift detection, and requires re-running the §4 bootstrap so OpenEMR's
    // site_addr_oath matches.
  });

  // -------------------------------------------------------------------------
  // Observability, continued. `observability` is resolved above the sidecar,
  // because the sidecar's Observability__PrometheusHealthUrl is gated on the same value.
  // -------------------------------------------------------------------------

  // Prometheus TSDB. Without it every restart starts the history at zero, which for a
  // load indicator is the difference between "traffic is climbing" and "no data".
  // Sized and retained per environment - see PROMETHEUS_TSDB_BY_ENV.
  const tsdb = observability ? PROMETHEUS_TSDB_FOR(ctx.environment) : undefined;
  const prometheusData = tsdb
    ? volume("prometheus-data", { region: REGION, sizeMB: tsdb.sizeMB })
    : undefined;

  const prometheus = observability
    ? service("prometheus", {
        source: image(observability.prometheus),
        env: {
          // Rendered into the scrape config by the image's entrypoint. The private
          // hostname, not the front door: this is container-to-container and must
          // never leave the private network.
          SIDECAR_TARGET: SIDECAR_HOST + ":8080",
          // Passed as --storage.tsdb.retention.* by the image's entrypoint. An image
          // older than that ignores them and keeps Prometheus's default: 15d, no size cap.
          PROMETHEUS_RETENTION_TIME: tsdb!.retentionTime,
          PROMETHEUS_RETENTION_SIZE: tsdb!.retentionSize,
          // The image runs as `USER nobody` and Railway mounts the volume at /prometheus owned
          // by root, so without this Prometheus panics at start: `open /prometheus/queries.active:
          // permission denied` -> `Unable to create mmap-ed active query log`. Staging crash-looped
          // on every deployment from 2026-09-20 (a0d0f807, 8eead98c). Railway's documented fix is
          // this variable (docs.railway.com/volumes/reference#caveats).
          RAILWAY_RUN_UID: "0",
        },
        // NO `domains:` ENTRY, AND THAT IS THE POINT. Prometheus has no auth of its
        // own - publishing it exposes every metric and the admin API to the internet.
        // Its only protection is being unreachable, exactly as in local compose, where
        // the loopback binding does the same job. Adding a domain here is a trust-
        // boundary change (DEPLOYMENT.md) and needs auth in the same edit.
        volumeMounts: { "/prometheus": prometheusData! },
      })
    : undefined;

  const grafana = observability
    ? service("grafana", {
        source: image(observability.grafana),
        env: {
          // Grafana does not read Railway's injected PORT; it reads this. Both are set
          // so the edge and the process agree - a mismatch is a service that deploys
          // green and 502s at the domain.
          PORT: "3000",
          GF_SERVER_HTTP_PORT: "3000",

          // SERVED UNDER A SUB-PATH, and BOTH of these are required to make that work
          // . The proxy routes /grafana here (see GRAFANA_UPSTREAM above); without
          // serve_from_sub_path Grafana still believes it is mounted at /, so every redirect
          // and every asset URL it emits drops the prefix and the UI half-loads in a way that
          // reads as a proxy fault. root_url is what it builds absolute URLs from.
          //
          // AN ABSOLUTE root_url, NOT A RELATIVE PREFIX, and it is the front door because
          // that is the only origin a browser reaches this service through. It therefore
          // shares the proxy domain's fate: until someone clicks Generate Domain on
          // reverse-proxy, ${{reverse-proxy.RAILWAY_PUBLIC_DOMAIN}} is empty and this is
          // degenerate exactly as OpenEmr__BaseUrl is (DEPLOYMENT.md section 9) - the same
          // one click fixes both, and grafana needs a redeploy after it for the same reason
          // agent-forge-api does. Being absolute also means Grafana does not depend on
          // X-Forwarded-Proto, which this proxy sets from nginx's own $scheme (http behind
          // Railway's TLS edge) rather than from the edge's header.
          //
          // NOT WITH `railway redeploy` - it replays the PREVIOUS deployment's captured
          // configuration rather than re-resolving GF_SERVER_ROOT_URL, so it would report
          // SUCCESS and leave RAILWAY_PUBLIC_DOMAIN exactly as degenerate as it was.
          // A variable change or a re-apply creates a genuinely new deployment instead;
          // confirm the deployment id actually moved afterwards (DEPLOYMENT.md section 7
          // and section 9).
          GF_SERVER_ROOT_URL: FRONT_DOOR + "/grafana/",
          GF_SERVER_SERVE_FROM_SUB_PATH: "true",

          // Private hostnames. Grafana is the only thing here a human reaches.
          PROMETHEUS_URL: "http://" + PROMETHEUS_HOST + ":9090",
          // NON-EMPTY ONLY where Loki is declared. Empty, the image provisions no Loki
          // datasource and swaps in the dashboard without its Loki panel, so an environment with
          // no Loki shows no Loki - not an erroring panel.
          //
          // EMPTY, NOT ABSENT, AND THAT IS DELIBERATE. Staging already carries LOKI_URL, and
          // removing a variable is a DESTRUCTIVE plan row: railway-destructive-guard.sh exits 10
          // on it, which would refuse the unattended staging apply. Measured under CLI 5.59.0:
          // omitting the key plans `- Delete variable grafana.LOKI_URL`. An empty value plans as a
          // safe update, and the image treats empty exactly as unset.
          LOKI_URL: loki ? "http://" + LOKI_HOST + ":3100" : "",
          // Same rule for Tempo: non-empty only where Tempo is declared, and empty rather
          // than absent so that removing Tempo later plans an update, not a destructive delete.
          TEMPO_URL: tempo ? "http://" + TEMPO_HOST + ":3200" : "",

          // SHARED VARIABLES, NOT preserve(), and the distinction is the whole
          // security of this service. preserve() means "decline to manage this
          // value" - it does not supply one (DEPLOYMENT.md section 9). With no
          // value set, Grafana falls back to its OWN grafana.ini default, which is
          // admin/admin, on a service that is internet-facing the moment a domain
          // is generated. Measured on the image built from this branch with the
          // vars unset: `curl -u admin:admin /api/datasources` returns 200 and the
          // full datasource list. A shared reference does NOT guarantee the value
          // exists - ctx.shared mints a reference for any name without a lookup, and
          // an apply proceeds with it unset (DEPLOYMENT.md section 9). What it buys is
          // discoverability: the credential appears in the graph and in the one table
          // an operator reads. THE ORDERING NOTE BELOW IS THE ACTUAL CONTROL, because
          // this variable fails in the opposite direction to the others here - an
          // unset MYSQL_ROOT_PASSWORD fails CLOSED (the database will not initialise),
          // an unset GF_SECURITY_ADMIN_PASSWORD fails OPEN (admin/admin still
          // authenticates, measured with the value empty as well as absent).
          GF_SECURITY_ADMIN_USER: ctx.shared.GRAFANA_ADMIN_USER,
          GF_SECURITY_ADMIN_PASSWORD: ctx.shared.GRAFANA_ADMIN_PASSWORD,
        },
        // STILL NO `domains:` ENTRY, AND NOW IT NEEDS NONE. A separate change routes /grafana from
        // the reverse-proxy service, which already holds the environment's one public
        // domain, so Grafana is reached through the front door and the Generate Domain
        // click this comment used to demand is no longer a step (DEPLOYMENT.md section 9).
        // Keeping the domain off this service keeps the one-published-origin property the
        // topology is argued from (DEPLOYMENT.md) - there is still exactly one
        // internet-facing surface, and it is the proxy.
        //
        // THE CONSEQUENCE IS THAT THE EXPOSURE NOW ARRIVES WITH THE ROUTE, at the merge
        // that applies it, rather than at a click someone chooses to make. Every push to
        // develop applies to staging unattended, and the
        // proxy's domain already exists, so from that apply Grafana's login page is on the
        // public internet - whatever GF_SECURITY_ADMIN_* resolve to at that moment is what
        // stands in front of it. The ordering note this comment used to carry (set the
        // shared credential BEFORE generating the domain) has no "before" left: there is
        // no second step to sequence against.
        //
        // THAT IS A DELIBERATE, RECORDED TRADE-OFF, NOT AN OVERSIGHT - the maintainer's
        // ruling, on the ground that this environment holds synthetic demo data
        // only. A separate change remains open and is the one-command fix: set GRAFANA_ADMIN_USER and
        // GRAFANA_ADMIN_PASSWORD as environment-level shared variables and redeploy this
        // service. Read the resolved values back off the service afterwards rather than
        // trusting the reference - an unset GF_SECURITY_ADMIN_PASSWORD fails OPEN, which is
        // the measurement the comment above records and is how this state arose.
        //
        // NOT WITH `railway redeploy` - it replays the PREVIOUS deployment's captured
        // configuration and reports `SUCCESS` while leaving the just-set credential unresolved
        // . A variable change or a re-apply is what creates the genuinely new
        // deployment this step needs. Confirm the deployment id moved (DEPLOYMENT.md section 7
        // and section 9).
        //
        // PRODUCTION GETS NO SUCH WINDOW. Its apply is a hand step, so the credential is
        // set as PRODUCTION shared variables BEFORE that apply, and
        // `post-deploy-verify.sh --grafana` afterwards proves admin/admin is refused.
        //
        // There is also no volume here on purpose - grafana.db is ephemeral, so
        // a password changed in the UI does not survive a redeploy; the environment
        // is the only place the credential durably lives.
      })
    : undefined;

  // -------------------------------------------------------------------------
  // Loki. See LOKI_IMAGE_BY_ENV. Private only - it has no auth of its own
  // (auth_enabled: false), so NO `domains:` entry, exactly as Prometheus. Retention is the
  // image's config: 168h, enforced by the compactor. The image runs as root because a Railway
  // volume mounts root-owned and Loki's uid 10001 cannot write it.
  // -------------------------------------------------------------------------
  const lokiData = loki ? volume("loki-data", { region: REGION, sizeMB: 1024 }) : undefined;
  const lokiService = loki
    ? service("loki", {
        source: image(loki),
        env: {},
        volumeMounts: { "/loki": lokiData! },
      })
    : undefined;

  // -------------------------------------------------------------------------
  // Tempo. See TEMPO_IMAGE_BY_ENV. Private only - no auth of its own, so NO `domains:`
  // entry, exactly as Loki and Prometheus; Grafana reaches it over the private domain and a human
  // reaches Grafana through /grafana on the front door. Retention is the image's config: 168h, as
  // Loki's. The image runs as root for the same root-owned-volume reason as Loki.
  // -------------------------------------------------------------------------
  const tempoData = tempo ? volume("tempo-data", { region: REGION, sizeMB: 1024 }) : undefined;
  const tempoService = tempo
    ? service("tempo", {
        source: image(tempo),
        env: {},
        volumeMounts: { "/var/tempo": tempoData! },
      })
    : undefined;

  // Deterministic replayer image. Staging only, no domain. `serve` is idle.
  const securityPlatformImage = SECURITY_PLATFORM_FOR(ctx.environment);
  const securityPlatform = securityPlatformImage
    ? service("security-platform", {
        source: image(securityPlatformImage),
        env: {},
      })
    : undefined;

  return project("agent-forge-copilot", {
    resources: [
      mysql,
      postgres,
      openemr,
      sidecar,
      proxy,
      mysqlData,
      sitesVolume,
      postgresData,
      dataProtectionKeys,
      // Spread rather than listed: each is undefined on an environment with no
      // observability pin, and `resources` must not carry holes.
      ...[prometheus, grafana, prometheusData].filter((r) => r !== undefined),
      ...[lokiService, lokiData].filter((r) => r !== undefined),
      ...[tempoService, tempoData].filter((r) => r !== undefined),
      ...[securityPlatform].filter((r) => r !== undefined),
    ],
  });
});
