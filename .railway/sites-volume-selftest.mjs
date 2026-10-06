// Self-test for the openemr sites-volume resolver.
//
// WHY THIS IS A TEST AND NOT A PLAN. The acceptance criterion offered "a test or a
// plan against a scratch environment". A plan is not available: SIDECAR_IMAGE_FOR
// throws for any environment outside its map, so evaluating this
// file against a scratch environment dies on the sidecar pin long before it reaches a
// volume. Case 3 below is that criterion, asserted where it can be.
//
// Run: node .railway/sites-volume-selftest.mjs
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRailwayContext } from "railway/iac";
import program, {
  SITES_VOLUME_FOR,
  DECLARED_SITES_VOLUME,
  LEGACY_SITES_VOLUME,
  OBSERVABILITY_IMAGES,
  PROMETHEUS_TSDB_BY_ENV,
  LOKI_IMAGE_BY_ENV,
  TEMPO_IMAGE_BY_ENV,
  SECURITY_PLATFORM_IMAGE_BY_ENV,
  SIDECAR_IMAGE_BY_ENV,
  PROXY_IMAGE_BY_ENV,
  TRACKS_DEVELOP,
  STAGING_BUILD_SHA_VAR,
  OPENEMR_STORED_BUILD_BY_ENV,
  resolvePin,
} from "./railway.ts";

// Staging tracks develop, so no staging graph renders without a build sha. Every case
// renders against this one; the block at the end varies it. An obviously-fixture value,
// so it can never be mistaken for a real develop commit in failure output.
const FIXTURE_SHA = "feedc0ffee12";
process.env[STAGING_BUILD_SHA_VAR] = FIXTURE_SHA;

const MOUNT = "/var/www/localhost/htdocs/openemr/sites";
let failures = 0;
// Counted so the closing line can say how many cases RAN, not only that none failed.
let cases = 0;

const check = (label, fn) => {
  cases += 1;
  try {
    fn();
    console.log(`  ok    ${label}`);
  } catch (err) {
    failures += 1;
    console.log(`  FAIL  ${label}\n        ${err.message.split("\n")[0]}`);
  }
};

// The graph an environment actually gets: asserts the volume is ATTACHED at the sites
// path, not merely declared somewhere in the resource list. A change that declared the
// right volume and mounted the wrong one passes the second check and fails this one.
const attachmentFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const openemr = graph.resources.find((r) => r.address === "service.openemr");
  assert.ok(openemr, "no service.openemr in the graph");
  const attached = Object.values(openemr.volumeAttachments ?? {}).filter(
    (a) => a.mountPath === MOUNT,
  );
  assert.equal(attached.length, 1, `expected exactly one volume at ${MOUNT}, got ${attached.length}`);
  return attached[0];
};

console.log("== the two pre-IaC environments keep the volume OpenEMR serves from ==");

check("production attaches openemr-volume-ceSx at the sites path", () => {
  const a = attachmentFor("production");
  assert.equal(a.volume, "volume.openemr-volume-ceSx");
});

check("staging attaches openemr-volume-o8g8 at the sites path", () => {
  const a = attachmentFor("staging");
  assert.equal(a.volume, "volume.openemr-volume-o8g8");
});

// The region and size are asserted because both are silent in the graph and only surface
// as destructive rows in a plan: a wrong region proposes RECREATING a 50 GB volume, a
// wrong sizeMB proposes resizing it. Neither is caught before merge by anything else -
// GitLab runs no plan on a merge request, and GitHub's runs with the production token, so
// a wrong `staging` entry reaches neither.
check("the legacy attachments carry the live region and size", () => {
  for (const env of ["production", "staging"]) {
    const cfg = attachmentFor(env).volumeConfig;
    assert.equal(cfg.region, "sfo", `${env} region`);
    assert.equal(cfg.sizeMB, 50000, `${env} sizeMB`);
  }
});

// THIS ONE READS THE SOURCE, DELIBERATELY. The check above compares values, and it went
// DEGENERATE when set REGION to "sfo": "sfo" === REGION now, so it passes whether
// the entries say `region: "sfo"` or `region: REGION` - the mutation that used to redden
// it no longer does. The property that matters is not the value today but where it comes
// from: these two volumes hold the live OpenEMR sites tree and cannot be recreated, so a
// later edit to REGION must not move them. Only the source can express that.
check("the legacy entries pin their region literally, not through REGION", () => {
  const src = readFileSync(new URL("./railway.ts", import.meta.url), "utf8");
  const block = src.slice(
    src.indexOf("LEGACY_SITES_VOLUME"),
    src.indexOf("SITES_VOLUME_FOR"),
  );
  assert.ok(block.length > 0, "could not locate the LEGACY_SITES_VOLUME block");
  assert.ok(
    !/region:\s*REGION/.test(block),
    "a legacy entry takes its region from REGION; an edit to that constant would then " +
      "propose recreating 50 GB of patient documents",
  );
  assert.equal((block.match(/region:\s*"sfo"/g) || []).length, 2, "two literal regions");
});

console.log("== an environment outside the legacy list gets the DECLARED volume ==");

check("an unlisted environment resolves to openemr-sites", () => {
  assert.deepEqual(SITES_VOLUME_FOR("scratch-env"), DECLARED_SITES_VOLUME);
  assert.equal(SITES_VOLUME_FOR("scratch-env").name, "openemr-sites");
});

check("an UNSET environment resolves to openemr-sites, never to a legacy one", () => {
  assert.equal(SITES_VOLUME_FOR(undefined).name, "openemr-sites");
});

// The reason the resolver uses Object.hasOwn rather than a bare bracket read: these
// names resolve to truthy INHERITED members on any object literal, so a naive presence
// check hands back Object.prototype.constructor and calls it a legacy environment.
check("a prototype member name is not a legacy environment", () => {
  for (const name of ["constructor", "toString", "__proto__", "hasOwnProperty"]) {
    assert.equal(SITES_VOLUME_FOR(name).name, "openemr-sites", `environment "${name}"`);
  }
});

// Two environments naming the SAME volume is the cross-environment binding that detached
// production's volume on 2026-09-18: a volume is project-scoped, so the alias resolves and
// two environments mount one disk. The resolver cannot catch this - it returns whatever the
// map says - so the map itself is what gets asserted.
check("no two environments are mapped onto the same volume", () => {
  const names = Object.values(LEGACY_SITES_VOLUME).map((v) => v.name);
  assert.equal(new Set(names).size, names.length, `aliased volume name in ${names.join(", ")}`);
  assert.ok(
    !names.includes(DECLARED_SITES_VOLUME.name),
    "a legacy entry names the declared volume, so a legacy environment and a fresh one collide",
  );
});

// Mounting a volume that is not in `resources` is silent in the graph and only shows up as a
// plan that does not create it. Asserting the mount alone would pass.
check("the resolved volume is declared in the resources list, not only mounted", () => {
  for (const env of ["production", "staging"]) {
    const graph = program(createRailwayContext({ environment: env }));
    const mounted = attachmentFor(env).volume;
    const declared = graph.resources.some((r) => r.type === "volume" && r.address === mounted);
    assert.ok(declared, `${env}: ${mounted} is mounted but absent from resources`);
  }
});

check("the declared volume is the one a fresh environment would create", () => {
  assert.equal(DECLARED_SITES_VOLUME.name, "openemr-sites");
  assert.equal(DECLARED_SITES_VOLUME.sizeMB, 2048);
});

// ---------------------------------------------------------------------------
// The readiness-probe invariant.
//
// WHY IT LIVES IN THIS FILE. The job that runs this script is the only pre-merge check
// that evaluates the graph for BOTH environments - nothing plans on a merge request on
// GitLab at all, and GitHub's PR plan holds the production token. A rule asserted anywhere else is
// asserted nowhere. The file name undersells its scope as a result; the job is the point.
//
// WHY A BICONDITIONAL rather than "the URL is set". Both directions are real failures and
// they point in opposite directions:
//   key WITHOUT service - ${{prometheus.RAILWAY_PRIVATE_DOMAIN}} resolves to nothing, so
//     every /ready becomes a 503 on a single-replica deployment. That is strictly
//     worse than the Degraded a separate change fixed, and it is what an unconditional assignment at
//     the sidecar - the likeliest future edit - produces, silently: typecheck stays 0. Since
//     a separate change that 503 arrives within Readiness__ProbeTimeout instead of 100s later, which
//     makes it cheaper to reproduce and no less wrong.
//   service WITHOUT key - a declared Prometheus is never probed and /ready reports Degraded
//     next to a running backend, which is a separate change itself.
const HEALTH_URL_KEY = "Observability__PrometheusHealthUrl";

const readinessWiringFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const sidecar = graph.resources.find((r) => r.address === "service.agent-forge-api");
  assert.ok(sidecar, "no service.agent-forge-api in the graph");
  const variables = sidecar.variables ?? {};
  const raw = variables[HEALTH_URL_KEY];
  return {
    // hasOwn, not a truthiness test: "absent" and "set to an empty string" are different
    // graphs and different intents, so the assertions below have to be able to tell them
    // apart. ObservabilityHealthCheck cannot - it guards on IsNullOrEmpty, so both report the
    // same unconfigured Degraded - which is the reason to assert on the graph and not on /ready.
    hasKey: Object.hasOwn(variables, HEALTH_URL_KEY),
    value: raw === undefined ? undefined : (typeof raw === "string" ? raw : raw.value),
    hasPrometheus: graph.resources.some((r) => r.address === "service.prometheus"),
  };
};

// Renders with staging's pins filled, then restores them. Both pins, because
// OBSERVABILITY_FOR declares the tier only when neither is empty. The values are obvious
// non-tags so they can never be mistaken for a real pin in failure output.
const withStagingPinsFilled = (fn) => {
  const saved = { ...OBSERVABILITY_IMAGES.staging };
  OBSERVABILITY_IMAGES.staging.prometheus = "selftest-only-not-a-real-pin";
  OBSERVABILITY_IMAGES.staging.grafana = "selftest-only-not-a-real-pin";
  try {
    fn();
  } finally {
    Object.assign(OBSERVABILITY_IMAGES.staging, saved);
  }
};

// The mirror, and this suite needs it as. While staging's real pins were "" the
// empty state was the DEFAULT one every case rendered; filling them left nothing exercising
// it. Production is not a substitute - it has no OBSERVABILITY_IMAGES entry, so it returns at
// OBSERVABILITY_FOR's `Object.hasOwn` check and never reaches the emptiness test. Measured:
// with this helper absent, deleting the `!== ""` guard in OBSERVABILITY_FOR left the whole
// suite green. `which` names the pins to empty, because "both are required" and "either alone
// is enough to omit" are two rules and railway.ts states both.
const withStagingPinsEmptied = (which, fn) => {
  const saved = { ...OBSERVABILITY_IMAGES.staging };
  for (const key of which) OBSERVABILITY_IMAGES.staging[key] = "";
  try {
    fn();
  } finally {
    Object.assign(OBSERVABILITY_IMAGES.staging, saved);
  }
};

// Production's pins filled, as the promotion's pin commit will: two CI-shaped tags of one
// develop build. NOT staging's entry copied across - that is TRACKS_DEVELOP, which
// resolvePin refuses outside staging, and the block below asserts exactly that refusal.
const PROMOTED = {
  prometheus: "docker.io/amarquette/gauntletai:prometheus-sha-aaaaaaaaaaaa",
  grafana: "docker.io/amarquette/gauntletai:grafana-sha-aaaaaaaaaaaa",
};
const withProductionPinsFilled = (fn) => {
  const saved = { ...OBSERVABILITY_IMAGES.production };
  Object.assign(OBSERVABILITY_IMAGES.production, PROMOTED);
  try {
    fn();
  } finally {
    Object.assign(OBSERVABILITY_IMAGES.production, saved);
  }
};

console.log("== /ready probes Prometheus exactly where Prometheus is declared ==");

// A STATE TRIPWIRE for staging, which runs the tier. PRODUCTION IS NO LONGER PINNED HERE AS
// "neither": its entry exists and is filled by the promotion's pin commit, which must
// stay a pin-only diff. What guards that commit instead is the block below - production
// may only name staging's images, and only in the CI-published shape.
check("staging declares the tier and carries the URL; production does both or neither", () => {
  const stg = readinessWiringFor("staging");
  assert.equal(stg.hasPrometheus, true, "staging no longer declares a prometheus service");
  assert.equal(stg.hasKey, true, `staging lost ${HEALTH_URL_KEY} beside a declared Prometheus`);

  const prd = readinessWiringFor("production");
  const filled = OBSERVABILITY_IMAGES.production.prometheus !== "" &&
    OBSERVABILITY_IMAGES.production.grafana !== "";
  assert.equal(prd.hasPrometheus, filled, "production's tier does not follow its own pins");
  assert.equal(prd.hasKey, filled, `production's ${HEALTH_URL_KEY} does not follow its own pins`);
});

check("filling staging's pins brings the service and the URL together", () => {
  withStagingPinsFilled(() => {
    const w = readinessWiringFor("staging");
    assert.equal(w.hasPrometheus, true, "the tier is declared");
    assert.equal(w.hasKey, true, `${HEALTH_URL_KEY} is absent beside a declared Prometheus`);
    assert.match(
      String(w.value),
      /^http:\/\/\$\{\{prometheus\.RAILWAY_PRIVATE_DOMAIN\}\}:9090\/-\/healthy$/,
      "the probe must name the prometheus service over private networking",
    );
  });
});

// Stated as independence rather than "production has none", so it holds before AND after
// production is promoted.
check("staging's pins never change production's wiring", () => {
  const before = readinessWiringFor("production");
  withStagingPinsFilled(() => {
    assert.deepEqual(readinessWiringFor("production"), before, "filling staging moved production");
  });
  withStagingPinsEmptied(["prometheus", "grafana"], () => {
    assert.deepEqual(readinessWiringFor("production"), before, "emptying staging moved production");
  });
});

// The guard this covers is OBSERVABILITY_FOR's `prometheus !== "" && grafana !== ""`. Both
// halves are asserted: both-empty omits, and EITHER-empty omits, which is what makes a
// half-created tier impossible rather than merely unlikely. Removing that guard reddens here.
check("an empty pin omits the service and the URL together - either pin, or both", () => {
  for (const which of [["prometheus", "grafana"], ["prometheus"], ["grafana"]]) {
    withStagingPinsEmptied(which, () => {
      const w = readinessWiringFor("staging");
      const empty = which.join(" + ");
      assert.equal(w.hasPrometheus, false, `staging declared a prometheus service with ${empty} empty`);
      assert.equal(w.hasKey, false, `staging carried ${HEALTH_URL_KEY} with ${empty} empty`);
    });
  }
});

// The invariant itself, swept over every environment and both pin states - filled AND empty,
// which since a separate change takes an explicit helper, because staging's real pins are no longer
// empty. This is the case that reddens when the gating at the sidecar is removed; the cases
// above would still pass on an environment that happens to be configured consistently.
check("the URL is present if and only if the prometheus service is, in every case", () => {
  const sweep = (label) => {
    for (const env of ["production", "staging", "scratch-env-that-does-not-exist"]) {
      let w;
      try {
        w = readinessWiringFor(env);
      } catch {
        continue; // SIDECAR_IMAGE_FOR throws for an unknown environment - a separate change, by design.
      }
      assert.equal(
        w.hasKey,
        w.hasPrometheus,
        `${label} ${env}: ${HEALTH_URL_KEY} ${w.hasKey ? "present" : "absent"} but ` +
          `service.prometheus ${w.hasPrometheus ? "present" : "absent"}`,
      );
    }
  };
  sweep("as pinned,");
  withStagingPinsFilled(() => sweep("with staging pinned,"));
  withStagingPinsEmptied(["prometheus", "grafana"], () => sweep("with staging emptied,"));
  withProductionPinsFilled(() => sweep("with production pinned,"));
});

// ---------------------------------------------------------------------------------------
// the proxy's /grafana route, and the same biconditional for the same reason.
//
// WHY THIS BLOCK EXISTS AT ALL. The nginx side of a separate change answers 404 on an empty
// GRAFANA_UPSTREAM, so a route to a service that is not there is survivable rather than a
// 502 - but the failure this guards is the OTHER direction, and it is not survivable and not
// visible: an UNCONDITIONAL `GRAFANA_UPSTREAM: GRAFANA_HOST + ":3000"` at the proxy (the
// likeliest future edit, exactly as it was for the readiness URL above) hands production a
// `${{grafana.RAILWAY_PRIVATE_DOMAIN}}` reference naming a service its graph does not
// contain. Typecheck stays 0, nginx-config-lint stays green - both read files, and neither
// renders this graph. Separate changes
const GRAFANA_UPSTREAM_KEY = "GRAFANA_UPSTREAM";

const variablesOf = (graph, address) => {
  const svc = graph.resources.find((r) => r.address === address);
  return svc ? (svc.variables ?? {}) : undefined;
};

const grafanaWiringFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const proxy = variablesOf(graph, "service.reverse-proxy");
  assert.ok(proxy, "no service.reverse-proxy in the graph");
  const raw = proxy[GRAFANA_UPSTREAM_KEY];
  const grafana = variablesOf(graph, "service.grafana");
  const readVar = (vars, key) => {
    const v = vars?.[key];
    return v === undefined ? undefined : typeof v === "string" ? v : v.value;
  };
  return {
    // hasOwn for the same reason as the readiness block: "absent" and "set to empty" are
    // different graphs. Absent is what lets the proxy image's own empty ENV default apply.
    hasUpstream: Object.hasOwn(proxy, GRAFANA_UPSTREAM_KEY),
    upstream: raw === undefined ? undefined : typeof raw === "string" ? raw : raw.value,
    hasGrafana: grafana !== undefined,
    rootUrl: readVar(grafana, "GF_SERVER_ROOT_URL"),
    serveFromSubPath: readVar(grafana, "GF_SERVER_SERVE_FROM_SUB_PATH"),
  };
};

console.log("\n== the proxy routes /grafana exactly where grafana is declared ==");

// The state tripwire, and the address shape with it. A private domain, never the front door:
// this hop is container-to-container, and port 3000 is GF_SERVER_HTTP_PORT rather than
// Railway's injected PORT, which Grafana does not read.
check("staging wires GRAFANA_UPSTREAM to grafana's private domain; production follows its own pins", () => {
  const stg = grafanaWiringFor("staging");
  assert.equal(stg.hasGrafana, true, "staging no longer declares a grafana service");
  assert.equal(stg.hasUpstream, true, `staging lost ${GRAFANA_UPSTREAM_KEY} beside a declared grafana`);
  assert.match(
    String(stg.upstream),
    /^\$\{\{grafana\.RAILWAY_PRIVATE_DOMAIN\}\}:3000$/,
    "the upstream must name the grafana service over private networking, on 3000",
  );

  // Both or neither, following production's own pins - see the readiness tripwire.
  const prd = grafanaWiringFor("production");
  assert.equal(
    prd.hasUpstream,
    prd.hasGrafana,
    `production: ${GRAFANA_UPSTREAM_KEY} and service.grafana disagree - /grafana would 502 or 404 wrongly`,
  );
});

// BOTH, not either. This is the failure calls out as looking like a proxy fault:
// root_url alone leaves Grafana serving from / and emitting unprefixed redirects and asset
// URLs; serve_from_sub_path alone leaves it building absolute URLs from a default root. Only
// a rendered graph can see this - there is no type that requires the pair.
check("a declared grafana is always told it lives under the sub-path - both keys, never one", () => {
  const assertPair = (label, w) => {
    if (!w.hasGrafana) return;
    assert.match(
      String(w.rootUrl),
      /^https:\/\/\$\{\{reverse-proxy\.RAILWAY_PUBLIC_DOMAIN\}\}\/grafana\/$/,
      `${label}: GF_SERVER_ROOT_URL must be the front door plus /grafana/, got ${w.rootUrl}`,
    );
    assert.equal(
      w.serveFromSubPath,
      "true",
      `${label}: GF_SERVER_SERVE_FROM_SUB_PATH must be "true" beside a sub-path root_url`,
    );
  };
  assertPair("as pinned, staging", grafanaWiringFor("staging"));
  withStagingPinsFilled(() => assertPair("with staging pinned, staging", grafanaWiringFor("staging")));
});

// The invariant, swept exactly as the readiness one is: every environment, both pin states.
// This is the case that reddens when the gating at the proxy is removed.
check("the upstream is present if and only if the grafana service is, in every case", () => {
  const sweep = (label) => {
    for (const env of ["production", "staging", "scratch-env-that-does-not-exist"]) {
      let w;
      try {
        w = grafanaWiringFor(env);
      } catch {
        continue; // SIDECAR_IMAGE_FOR throws for an unknown environment - a separate change, by design.
      }
      assert.equal(
        w.hasUpstream,
        w.hasGrafana,
        `${label} ${env}: ${GRAFANA_UPSTREAM_KEY} ${w.hasUpstream ? "present" : "absent"} but ` +
          `service.grafana ${w.hasGrafana ? "present" : "absent"}`,
      );
    }
  };
  sweep("as pinned,");
  withStagingPinsFilled(() => sweep("with staging pinned,"));
  withStagingPinsEmptied(["prometheus", "grafana"], () => sweep("with staging emptied,"));
  withProductionPinsFilled(() => sweep("with production pinned,"));
  withStagingPinsEmptied(["grafana"], () => sweep("with staging's grafana pin emptied,"));
});

// ---------------------------------------------------------------------------------------
// the observability tier in PRODUCTION.
//
// Production's entry is filled by a pin-only commit during a promotion, under time pressure.
// WHICH IMAGES production may name is no longer judged here. The rule - production EQUALS
// staging's entry, decayed into "one develop build, in the right shape" - forbade any
// rollback and passed the hand-built *-sha-4768e5d79880 images. It is REPLACED,
// not joined, by the production pin-window check, which accepts a develop build staging ran or one
// of production's own last PRODUCTION_PIN_WINDOW pins, per service, prometheus and grafana
// independently. What
// stays here is the half of the old rule that is about STAGING: its entries track develop.
// Production's shape is the rule below, and the graph it gets is rendered further down.
const PIN_SHAPE = (component) =>
  new RegExp(`^docker\\.io/amarquette/gauntletai:${component}-sha-[0-9a-f]{12}$`);

// Returns every violation rather than throwing on the first, so a negative control can assert
// that the rule fires for the reason it exists.
const stagingObservabilityViolations = (images) => {
  const out = [];
  const pins = images.staging ?? {};
  for (const component of ["prometheus", "grafana"]) {
    const pin = pins[component];
    if (pin !== TRACKS_DEVELOP && pin !== "") {
      out.push(`staging.${component} = "${pin}" is a literal pin; staging tracks develop`);
    }
  }
  return out;
};

// Prometheus reads size units as powers of two; MB is MiB. Railway's sizeMB is compared in the
// same unit, which errs towards a smaller volume and so towards refusing.
const retentionMiB = (size) => {
  const m = /^(\d+)(MB|GB)$/.exec(size);
  assert.ok(m, `retentionSize "${size}" is not <N>MB or <N>GB`);
  return Number(m[1]) * (m[2] === "GB" ? 1024 : 1);
};

const tsdbViolations = (tsdbByEnv, images) => {
  const out = [];
  for (const env of Object.keys(images)) {
    if (!Object.hasOwn(tsdbByEnv, env)) out.push(`${env} has observability pins but no TSDB entry`);
  }
  for (const [env, t] of Object.entries(tsdbByEnv)) {
    if (!/^\d+[dwh]$/.test(t.retentionTime)) out.push(`${env}.retentionTime "${t.retentionTime}"`);
    if (retentionMiB(t.retentionSize) * 2 >= t.sizeMB) {
      out.push(`${env}: retentionSize ${t.retentionSize} is not under half of ${t.sizeMB} MB`);
    }
  }
  return out;
};

console.log("\n==: staging's observability entries track develop ==");

check("staging's observability entries track develop (or are empty)", () => {
  assert.deepEqual(stagingObservabilityViolations(OBSERVABILITY_IMAGES), []);
});

// NEGATIVE CONTROL: a literal staging pin - the model a separate change retired, and the shape the hand-built
// *-sha-4768e5d79880 images arrived in - is refused; tracking and empty are not.
check("the staging rule refuses a literal staging pin", () => {
  assert.ok(stagingObservabilityViolations({ staging: { ...PROMOTED } }).length > 0,
    "a literal staging pin was accepted");
  assert.ok(stagingObservabilityViolations({ staging: { prometheus: TRACKS_DEVELOP, grafana: PROMOTED.grafana } }).length > 0,
    "one literal staging pin beside a tracking one was accepted");
  assert.deepEqual(stagingObservabilityViolations({ staging: { prometheus: TRACKS_DEVELOP, grafana: TRACKS_DEVELOP } }), [],
    "a tracking staging entry was refused");
  assert.deepEqual(stagingObservabilityViolations({ staging: { prometheus: "", grafana: "" } }), [],
    "an undeclared staging tier was refused");
});

// THE RULE, AS A FUNCTION - so the real case below and its red control call the SAME
// check, rather than the control re-implementing one assertion inline where it cannot detect the
// real rule being gutted (review finding F3). Since a separate change it is the only production-shape
// rule here; the one-build rule it used to sit beside deliberately tolerated production being empty (a tree where the tier is not yet
// promoted is valid); this rule is narrower and fires only once production HAS been promoted -
// which,, is always, so a `develop` edit that regresses it back to undeclared - a bad
// merge, a bad rebase, or copying an older revision of this file - must redden here.
const productionObservabilityViolations = (images) => {
  const out = [];
  const production = images.production;
  for (const component of ["prometheus", "grafana"]) {
    const pin = production[component];
    if (pin === "") {
      out.push(`production.${component} is empty - a promoted tier regressed to undeclared`);
    } else if (pin === TRACKS_DEVELOP) {
      out.push(`production.${component} tracks develop - only staging may`);
    } else if (!PIN_SHAPE(component).test(pin)) {
      out.push(`production.${component} = "${pin}" is not an explicit ${component}-sha-<12> tag`);
    }
  }
  return out;
};

// THE ACTUAL FILE'S production ENTRY, NOT A FIXTURE. This is exactly how a separate change happened:
// `develop` kept carrying `{ prometheus: "", grafana: "" }` for an environment a separate change had
// already promoted, and a production plan from `develop` proposed deleting the live Grafana and
// Prometheus. Asserting against the real, imported `OBSERVABILITY_IMAGES` - not a copy - is what
// makes this case pin the file's own current state.
check("production's observability entries are non-empty, explicit -sha-<12> literals, not TRACKS_DEVELOP", () => {
  assert.deepEqual(productionObservabilityViolations(OBSERVABILITY_IMAGES), []);
});

// THE RED CONTROL for the case above: prove the SAME rule actually fires on the regression it
// exists to catch - a separate change itself, production emptied - and on the other two ways this rule can be
// violated: a TRACKS_DEVELOP fixture, and a malformed or moving literal (a `-latest` tag, never
// published by any CI job). Fixture objects, not a mutate-and-restore of the exported map: if the
// rule above were gutted on any one of its three branches, this control catches it, because both
// call `productionObservabilityViolations` rather than each asserting it separately.
check("the rule reddens when production is emptied, tracks develop, or names a malformed tag", () => {
  assert.ok(productionObservabilityViolations({ production: { prometheus: "", grafana: "" } }).length > 0,
    "the own regression (production emptied) was accepted as valid");
  assert.ok(
    productionObservabilityViolations({ production: { prometheus: TRACKS_DEVELOP, grafana: TRACKS_DEVELOP } })
      .length > 0,
    "production tracking develop was accepted as valid",
  );
  assert.ok(
    productionObservabilityViolations({
      production: {
        prometheus: "docker.io/amarquette/gauntletai:prometheus-latest",
        grafana: "docker.io/amarquette/gauntletai:grafana-latest",
      },
    }).length > 0,
    "a moving -latest tag in production was accepted as an explicit -sha-<12> pin",
  );
  // The registry's dots are literal: an unescaped one in PIN_SHAPE accepted this.
  assert.ok(
    productionObservabilityViolations({
      production: {
        prometheus: "dockerXio/amarquette/gauntletai:prometheus-sha-49a8a5b5c411",
        grafana: "dockerXio/amarquette/gauntletai:grafana-sha-49a8a5b5c411",
      },
    }).length > 0,
    "a tag on another registry (dockerXio) was accepted as docker.io",
  );
});

check("every environment with pins has a TSDB entry whose size cap is under half its volume", () => {
  assert.deepEqual(tsdbViolations(PROMETHEUS_TSDB_BY_ENV, OBSERVABILITY_IMAGES), []);
  const over = tsdbViolations({ production: { sizeMB: 1024, retentionTime: "15d", retentionSize: "512MB" } },
    { production: {} });
  assert.ok(over.length > 0, "a cap at half the volume was accepted");
  const missing = tsdbViolations({}, { production: {} });
  assert.ok(missing.length > 0, "an environment with no TSDB entry was accepted");
});

// The graph production gets from the pin commit, rendered now rather than at the apply.
check("production, once pinned: unpublished Prometheus on a sized, retained TSDB; Grafana behind /grafana", () => {
  withProductionPinsFilled(() => {
    const graph = program(createRailwayContext({ environment: "production" }));
    const res = (a) => graph.resources.find((r) => r.address === a);
    const prom = res("service.prometheus");
    const graf = res("service.grafana");
    assert.ok(prom && graf, "the tier is not declared with both pins filled");
    for (const svc of [prom, graf]) {
      // The SDK renders any domain or TCP proxy under `networking`; none may exist here.
      assert.equal(svc.networking, undefined, `${svc.address} is published: ${JSON.stringify(svc.networking)}`);
    }
    const tsdb = PROMETHEUS_TSDB_BY_ENV.production;
    const att = prom.volumeAttachments?.["prometheus-data"];
    assert.equal(att?.mountPath, "/prometheus", "prometheus-data is not mounted at /prometheus");
    assert.deepEqual(att?.volumeConfig, { region: "sfo", sizeMB: tsdb.sizeMB }, "TSDB region or size");
    assert.ok(res("volume.prometheus-data"), "prometheus-data is mounted but not declared");
    assert.equal(prom.variables.PROMETHEUS_RETENTION_TIME?.value, tsdb.retentionTime);
    assert.equal(prom.variables.PROMETHEUS_RETENTION_SIZE?.value, tsdb.retentionSize);
    for (const [key, name] of [["GF_SECURITY_ADMIN_USER", "GRAFANA_ADMIN_USER"],
      ["GF_SECURITY_ADMIN_PASSWORD", "GRAFANA_ADMIN_PASSWORD"]]) {
      assert.deepEqual(graf.variables[key], { type: "sharedReference", name },
        `${key} must be the environment's shared ${name}, never a literal or preserve()`);
    }
    const w = readinessWiringFor("production");
    assert.equal(w.hasKey, true, `production lost ${HEALTH_URL_KEY} beside its Prometheus`);
    assert.equal(grafanaWiringFor("production").hasUpstream, true, "production has no /grafana route");
  });
});

// ---------------------------------------------------------------------------------------
// Loki, STAGING ONLY, and absent everywhere else without a trace.
//
// Production's sidecar logs carry FHIR Patient ids, so the property that matters most is
// that NO state of this file ships them to Loki in production - not even with every pin filled.
// The second property is transparency: where Loki is absent, the sidecar gets no endpoint and
// Grafana no LOKI_URL, so there is no exporter, no dead datasource and no erroring panel.
const LOKI_PIN = "docker.io/amarquette/gauntletai:loki-sha-aaaaaaaaaaaa";
const FILLED = { prometheus: "docker.io/amarquette/gauntletai:prometheus-sha-aaaaaaaaaaaa",
  grafana: "docker.io/amarquette/gauntletai:grafana-sha-aaaaaaaaaaaa" };

// Sets the pin maps to a named state for one render, then restores them exactly - including
// deleting an entry that did not exist before.
const withPins = ({ tier = {}, loki = {}, tempo = {} }, fn) => {
  const savedTier = Object.fromEntries(Object.entries(OBSERVABILITY_IMAGES).map(([k, v]) => [k, { ...v }]));
  const savedLoki = { ...LOKI_IMAGE_BY_ENV };
  const savedTempo = { ...TEMPO_IMAGE_BY_ENV };
  try {
    for (const [env, pins] of Object.entries(tier)) OBSERVABILITY_IMAGES[env] = { ...pins };
    for (const [env, pin] of Object.entries(loki)) LOKI_IMAGE_BY_ENV[env] = pin;
    for (const [env, pin] of Object.entries(tempo)) TEMPO_IMAGE_BY_ENV[env] = pin;
    return fn();
  } finally {
    for (const k of Object.keys(OBSERVABILITY_IMAGES)) if (!Object.hasOwn(savedTier, k)) delete OBSERVABILITY_IMAGES[k];
    Object.assign(OBSERVABILITY_IMAGES, savedTier);
    for (const k of Object.keys(LOKI_IMAGE_BY_ENV)) if (!Object.hasOwn(savedLoki, k)) delete LOKI_IMAGE_BY_ENV[k];
    Object.assign(LOKI_IMAGE_BY_ENV, savedLoki);
    for (const k of Object.keys(TEMPO_IMAGE_BY_ENV)) if (!Object.hasOwn(savedTempo, k)) delete TEMPO_IMAGE_BY_ENV[k];
    Object.assign(TEMPO_IMAGE_BY_ENV, savedTempo);
  }
};

const lokiWiringFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const find = (a) => graph.resources.find((r) => r.address === a);
  const sidecarVars = find("service.agent-forge-api")?.variables ?? {};
  const grafanaVars = find("service.grafana")?.variables;
  const val = (v) => (v === undefined ? undefined : typeof v === "string" ? v : v.value);
  return {
    loki: find("service.loki"),
    lokiVolume: find("volume.loki-data"),
    sidecarLokiKeys: Object.keys(sidecarVars).filter((k) => /loki/i.test(k)),
    endpoint: val(sidecarVars.Observability__LokiOtlpEndpoint),
    hasGrafana: grafanaVars !== undefined,
    lokiUrl: val(grafanaVars?.LOKI_URL),
    // Non-empty, not merely present: Grafana always carries the key, and empty means "no Loki".
    hasLokiUrl: (val(grafanaVars?.LOKI_URL) ?? "") !== "",
    lokiUrlDeclared: grafanaVars === undefined || Object.hasOwn(grafanaVars, "LOKI_URL"),
  };
};

// Every pin state worth rendering: Loki pin empty or filled, tier empty or filled.
// "as pinned" leaves Loki off on staging (the 2026-09-24 ruling); the pinned states are what keep
// the present-on-staging direction exercised, and "emptied" restates the declared state explicitly.
const LOKI_STATES = [
  ["as pinned", {}],
  ["staging Loki emptied", { loki: { staging: "" } }],
  ["staging Loki pinned", { loki: { staging: LOKI_PIN } }],
  ["staging Loki pinned, staging tier emptied",
    { loki: { staging: LOKI_PIN }, tier: { staging: { prometheus: "", grafana: "" } } }],
  ["everything pinned, production tier included",
    { loki: { staging: LOKI_PIN }, tier: { staging: FILLED, production: FILLED } }],
];

console.log("\n==: Loki is staging-only, and absent without a trace everywhere else ==");

check("LOKI_IMAGE_BY_ENV has no production entry - gates it", () => {
  assert.equal(Object.hasOwn(LOKI_IMAGE_BY_ENV, "production"), false,
    "production has a Loki pin; its logs carry FHIR Patient ids until lands");
  for (const [env, pin] of Object.entries(LOKI_IMAGE_BY_ENV)) {
    assert.ok(pin === "" || pin === TRACKS_DEVELOP,
      `${env} Loki pin "${pin}" is a literal; staging's is empty or tracks develop and nothing else has Loki`);
  }
});

check("production renders no Loki path in any pin state, even with every other pin filled", () => {
  for (const [label, state] of LOKI_STATES) {
    withPins(state, () => {
      const w = lokiWiringFor("production");
      assert.equal(w.loki, undefined, `${label}: production declares a loki service`);
      assert.equal(w.lokiVolume, undefined, `${label}: production declares loki-data`);
      assert.deepEqual(w.sidecarLokiKeys, [], `${label}: production's sidecar has ${w.sidecarLokiKeys}`);
      assert.equal(w.hasLokiUrl, false, `${label}: production's grafana has a non-empty LOKI_URL`);
    });
  }
});

check("service, sidecar endpoint and Grafana LOKI_URL appear together or not at all, in every state", () => {
  for (const [label, state] of LOKI_STATES) {
    withPins(state, () => {
      for (const env of ["production", "staging"]) {
        const w = lokiWiringFor(env);
        const has = w.loki !== undefined;
        assert.equal(w.lokiVolume !== undefined, has, `${label} ${env}: loki-data disagrees with service.loki`);
        assert.equal(w.endpoint !== undefined, has, `${label} ${env}: sidecar endpoint disagrees with service.loki`);
        assert.equal(w.hasLokiUrl, has, `${label} ${env}: grafana LOKI_URL disagrees with service.loki`);
        if (has) assert.equal(w.hasGrafana, true, `${label} ${env}: Loki declared with no Grafana to read it`);
        // Removing the key is a destructive plan row that refuses the unattended staging apply.
        assert.equal(w.lokiUrlDeclared, true, `${label} ${env}: grafana dropped LOKI_URL instead of emptying it`);
      }
    });
  }
});

check("staging, once Loki is pinned: private Loki on a mounted volume, pushed to over the private domain", () => {
  withPins({ loki: { staging: LOKI_PIN } }, () => {
    const w = lokiWiringFor("staging");
    assert.ok(w.loki, "staging declares no loki with its pin filled");
    assert.equal(w.loki.networking, undefined, `loki is published: ${JSON.stringify(w.loki.networking)}`);
    assert.equal(w.loki.source?.image, LOKI_PIN);
    const att = w.loki.volumeAttachments?.["loki-data"];
    assert.equal(att?.mountPath, "/loki", "loki-data is not mounted at /loki");
    assert.deepEqual(att?.volumeConfig, { region: "sfo", sizeMB: 1024 }, "loki-data region or size");
    assert.deepEqual(w.sidecarLokiKeys, ["Observability__LokiOtlpEndpoint"], "the sidecar gained another Loki key");
    assert.equal(w.endpoint, "http://${{loki.RAILWAY_PRIVATE_DOMAIN}}:3100/otlp/v1/logs");
    assert.equal(w.lokiUrl, "http://${{loki.RAILWAY_PRIVATE_DOMAIN}}:3100");
  });
});

// TRANSPARENT ABSENCE LIVES IN THE GRAFANA IMAGE, NOT IN THIS FILE (review, finding 1).
// An empty LOKI_URL hides Loki only in a grafana image built from a commit carrying a separate change
// entrypoint. Every grafana-sha published still provisions the Loki datasource, with an
// empty URL, and the Loki panel, which errors. That is exactly what forbids in production.
// These are EXACTLY the grafana-sha tags built WITHOUT the fix - not all of Docker Hub's
// grafana-sha tags, which also carries plenty of later builds (review of !689 note 104696).
// The last one built without the fix is `fe5a81db92ef`. `0e80692860b7` and `fe5a81db92ef` were
// missing here: staging-only commit 61439aea added them to staging's tree,
// and this file's develop copy never picked them up, so a pin or rollback to either would have
// passed this self-test.
// SINCE a separate change EVERY NEW grafana-sha PUBLISHED FROM THIS REPOSITORY'S CI is a develop build, and
// develop has carried since it merged, so no tag this repository's CI publishes from here on
// can join this list - three staging builds from 2026-09-24 (`3d8e5b266082`, `ea862820089f`,
// `325cd499d55a`) already carry the fix and are correctly absent. It stays closed rather than
// growing.
// A TRACKS_DEVELOP entry is exempt for the same reason: it resolves to a build of this tree.
const PRE_GL596_GRAFANA_TAGS = [
  "37a445938025", "65ff92637286", "69096cf3e934", "d9d81f0b0355", "4f795b145145",
  "b73635187bcf", "c98e038d33e8", "90acd65a09cc", "41f2f0a58d28", "4768e5d79880",
  "0e80692860b7", "fe5a81db92ef",
].map((sha) => `docker.io/amarquette/gauntletai:grafana-sha-${sha}`);
// The hand-built image staging ran from 2026-09-21 until a separate change retired its pin.
const RETIRED_STAGING_GRAFANA = "docker.io/amarquette/gauntletai:grafana-sha-4768e5d79880";

const staleGrafanaViolations = (images, lokiByEnv) => {
  const out = [];
  for (const [env, pins] of Object.entries(images)) {
    const hasLoki = Object.hasOwn(lokiByEnv, env) && lokiByEnv[env] !== "";
    if (pins.grafana === "" || pins.grafana === TRACKS_DEVELOP || hasLoki) continue;
    if (PRE_GL596_GRAFANA_TAGS.includes(pins.grafana)) {
      out.push(`${env} pins ${pins.grafana}, which predates and shows an erroring Loki panel with no Loki`);
    }
  }
  return out;
};

check("an environment without Loki never pins a Grafana image that predates the entrypoint", () => {
  assert.deepEqual(staleGrafanaViolations(OBSERVABILITY_IMAGES, LOKI_IMAGE_BY_ENV), []);
  const old = PRE_GL596_GRAFANA_TAGS[0];
  const gl678a = "docker.io/amarquette/gauntletai:grafana-sha-0e80692860b7";
  const gl678b = "docker.io/amarquette/gauntletai:grafana-sha-fe5a81db92ef";
  const fresh = "docker.io/amarquette/gauntletai:grafana-sha-0123456789ab";
  const p = "docker.io/amarquette/gauntletai:prometheus-sha-0123456789ab";
  const bad = {
    "production pins the retired hand-built staging image": { production: { prometheus: p, grafana: RETIRED_STAGING_GRAFANA } },
    "production pins another earlier build": { production: { prometheus: p, grafana: old } },
    // these two were staging-only (commit 61439aea) and missing from this file's
    // list until now - a pin to either passed this self-test before the fix.
    "production pins the staging-only tag 0e80692860b7": { production: { prometheus: p, grafana: gl678a } },
    "production pins the staging-only tag fe5a81db92ef": { production: { prometheus: p, grafana: gl678b } },
  };
  for (const [why, images] of Object.entries(bad)) {
    assert.ok(staleGrafanaViolations(images, { staging: TRACKS_DEVELOP }).length > 0, `${why} was accepted`);
  }
  assert.deepEqual(staleGrafanaViolations({ staging: { prometheus: TRACKS_DEVELOP, grafana: TRACKS_DEVELOP }, production: { prometheus: p, grafana: fresh } }, { staging: TRACKS_DEVELOP }), [],
    "a later promotion was refused");
});

// Staging follows its own entry: TRACKS_DEVELOP declares Loki, "" does not.
check("as declared, staging has Loki exactly when its own Loki entry is not empty", () => {
  const filled = LOKI_IMAGE_BY_ENV.staging !== "";
  assert.equal(lokiWiringFor("staging").loki !== undefined, filled, "staging Loki does not follow its entry");
});

// ---------------------------------------------------------------------------------------
// Tempo, STAGING ONLY - the disposition applied to traces - and absent everywhere else.
// Same two properties as Loki: no state of this file gives production a trace store, and where
// Tempo is absent the sidecar gets no endpoint and Grafana an empty TEMPO_URL, so there is no
// exporter and no dead datasource. Plus one Loki does not need: no deployed sidecar is ever told to
// print spans to stdout.
const TEMPO_PIN = "docker.io/amarquette/gauntletai:tempo-sha-aaaaaaaaaaaa";

const tempoWiringFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const find = (a) => graph.resources.find((r) => r.address === a);
  const sidecarVars = find("service.agent-forge-api")?.variables ?? {};
  const grafanaVars = find("service.grafana")?.variables;
  const val = (v) => (v === undefined ? undefined : typeof v === "string" ? v : v.value);
  return {
    tempo: find("service.tempo"),
    tempoVolume: find("volume.tempo-data"),
    sidecarTraceKeys: Object.keys(sidecarVars).filter((k) => /trace|tempo/i.test(k)),
    endpoint: val(sidecarVars.Observability__TraceOtlpEndpoint),
    hasGrafana: grafanaVars !== undefined,
    tempoUrl: val(grafanaVars?.TEMPO_URL),
    hasTempoUrl: (val(grafanaVars?.TEMPO_URL) ?? "") !== "",
    tempoUrlDeclared: grafanaVars === undefined || Object.hasOwn(grafanaVars, "TEMPO_URL"),
  };
};

const TEMPO_STATES = [
  ["as pinned", {}],
  ["staging Tempo emptied", { tempo: { staging: "" } }],
  ["staging Tempo pinned", { tempo: { staging: TEMPO_PIN } }],
  ["staging Tempo pinned, staging tier emptied",
    { tempo: { staging: TEMPO_PIN }, tier: { staging: { prometheus: "", grafana: "" } } }],
  ["everything pinned, production tier and Loki included",
    { tempo: { staging: TEMPO_PIN }, loki: { staging: LOKI_PIN }, tier: { staging: FILLED, production: FILLED } }],
];

console.log("\n==: Tempo is staging-only, and absent without a trace everywhere else ==");

check("TEMPO_IMAGE_BY_ENV has no production entry, and staging's tracks develop or is empty", () => {
  assert.equal(Object.hasOwn(TEMPO_IMAGE_BY_ENV, "production"), false,
    "production has a Tempo pin; the disposition scopes trace stores to staging");
  for (const [env, pin] of Object.entries(TEMPO_IMAGE_BY_ENV)) {
    assert.ok(pin === "" || pin === TRACKS_DEVELOP,
      `${env} Tempo pin "${pin}" is a literal; staging tracks develop and nothing else has Tempo`);
  }
});

check("production renders no Tempo path in any pin state, even with every other pin filled", () => {
  for (const [label, state] of TEMPO_STATES) {
    withPins(state, () => {
      const w = tempoWiringFor("production");
      assert.equal(w.tempo, undefined, `${label}: production declares a tempo service`);
      assert.equal(w.tempoVolume, undefined, `${label}: production declares tempo-data`);
      assert.deepEqual(w.sidecarTraceKeys, [], `${label}: production's sidecar has ${w.sidecarTraceKeys}`);
      assert.equal(w.hasTempoUrl, false, `${label}: production's grafana has a non-empty TEMPO_URL`);
    });
  }
});

check("service, sidecar endpoint and Grafana TEMPO_URL appear together or not at all, in every state", () => {
  for (const [label, state] of TEMPO_STATES) {
    withPins(state, () => {
      for (const env of ["production", "staging"]) {
        const w = tempoWiringFor(env);
        const has = w.tempo !== undefined;
        assert.equal(w.tempoVolume !== undefined, has, `${label} ${env}: tempo-data disagrees with service.tempo`);
        assert.equal(w.endpoint !== undefined, has, `${label} ${env}: sidecar endpoint disagrees with service.tempo`);
        assert.equal(w.hasTempoUrl, has, `${label} ${env}: grafana TEMPO_URL disagrees with service.tempo`);
        if (has) assert.equal(w.hasGrafana, true, `${label} ${env}: Tempo declared with no Grafana to read it`);
        assert.equal(w.tempoUrlDeclared, true, `${label} ${env}: grafana dropped TEMPO_URL instead of emptying it`);
        // Console spans reach Railway's stdout retention; no deployed sidecar asks for them.
        assert.ok(!w.sidecarTraceKeys.includes("Observability__TraceConsoleExporter"),
          `${label} ${env}: the deployed sidecar is told to print spans to stdout`);
      }
    });
  }
});

check("staging, once Tempo is pinned: private Tempo on a mounted volume, pushed to over the private domain", () => {
  withPins({ tempo: { staging: TEMPO_PIN } }, () => {
    const w = tempoWiringFor("staging");
    assert.ok(w.tempo, "staging declares no tempo with its pin filled");
    assert.equal(w.tempo.networking, undefined, `tempo is published: ${JSON.stringify(w.tempo.networking)}`);
    assert.equal(w.tempo.source?.image, TEMPO_PIN);
    const att = w.tempo.volumeAttachments?.["tempo-data"];
    assert.equal(att?.mountPath, "/var/tempo", "tempo-data is not mounted at /var/tempo");
    assert.deepEqual(att?.volumeConfig, { region: "sfo", sizeMB: 1024 }, "tempo-data region or size");
    assert.deepEqual(w.sidecarTraceKeys, ["Observability__TraceOtlpEndpoint"], "the sidecar gained another trace key");
    assert.equal(w.endpoint, "http://${{tempo.RAILWAY_PRIVATE_DOMAIN}}:4318/v1/traces");
    assert.equal(w.tempoUrl, "http://${{tempo.RAILWAY_PRIVATE_DOMAIN}}:3200");
  });
});

check("as declared, staging has Tempo exactly when its own Tempo entry is not empty", () => {
  const filled = TEMPO_IMAGE_BY_ENV.staging !== "";
  assert.equal(tempoWiringFor("staging").tempo !== undefined, filled, "staging Tempo does not follow its entry");
});

// The image runs as `nobody` and Railway mounts /prometheus owned by root; without
// RAILWAY_RUN_UID=0 it panics at start. Staging crash-looped on it from 2026-09-20. A separate change
check("a declared Prometheus runs as uid 0 (RAILWAY_RUN_UID), in every environment that declares it", () => {
  const promFor = (environment) =>
    program(createRailwayContext({ environment })).resources.find((r) => r.address === "service.prometheus");
  const assertRunsAsRoot = (environment) => {
    const prom = promFor(environment);
    assert.ok(prom, `${environment} declares no prometheus service`);
    assert.equal(prom.variables?.RAILWAY_RUN_UID?.value, "0",
      `${environment}'s prometheus lacks RAILWAY_RUN_UID=0 and cannot write its root-owned volume`);
  };
  assertRunsAsRoot("staging");
  withStagingPinsFilled(() => assertRunsAsRoot("staging"));
  withProductionPinsFilled(() => assertRunsAsRoot("production"));
});

console.log("\n== the reverse-proxy pins a published image, not a branch ==");

// THE COMMENT IS NOT THE GATE. PROXY_IMAGE_BY_ENV carries a header explaining how each
// environment resolves to one immutable `proxy-sha-<12>` build, and a header is not enforcement -
// asking politely in a comment is what this repository calls a gate asserted in a comment
// (platform.md). This is the gate.
//
// WHAT IT GUARDS CHANGED WITH a separate change: until step 2 this service followed a BRANCH, and the
// cases asserted which one. A pin cannot be broken that way. What it CAN be broken by is a
// MOVING tag, which restores the same property - a front door that changes with no diff here
// - so that is what these cases assert instead.
//
// Read off the RENDERED graph, not the map, so moving the literal or routing it through a
// different resolver still reddens. Separate changes
const proxySourceFor = (environment) => {
  const graph = program(createRailwayContext({ environment }));
  const proxy = graph.resources.find((r) => r.address === "service.reverse-proxy");
  assert.ok(proxy, `no service.reverse-proxy in the ${environment} graph`);
  assert.ok(proxy.source, `service.reverse-proxy has no source in the ${environment} graph`);
  return proxy.source;
};

check("both environments pin the proxy to a published image, not a branch", () => {
  for (const environment of ["production", "staging"]) {
    const src = proxySourceFor(environment);
    assert.equal(
      src.type,
      "image",
      `${environment}'s reverse-proxy source is "${src.type}", not "image". ended the ` +
        "branch-follow: the proxy was the one service whose version pin moved without a diff in " +
        "railway.ts, and it followed a GITHUB branch nothing mirrors this project to, so a " +
        "reverse-proxy change merged here rebuilt neither front door and nothing reported it. " +
        "Going back to a branch source reintroduces a silent-by-construction failure.",
    );
    assert.ok(
      /^docker\.io\/amarquette\/gauntletai:proxy-sha-[0-9a-f]{12}$/.test(src.image ?? ""),
      `${environment}'s reverse-proxy image is "${src.image}". It must be an explicit ` +
        "`proxy-sha-<12>` from publish-proxy-image: a MOVING tag (proxy-latest, proxy-develop) " +
        "would restore exactly the property this issue removed - a front door that changes " +
        "without a record of which build it is. proxy-latest is additionally hand-pushed.",
    );
  }
});

// unlike Llm__ApiKey (preserve(), crash-loops loudly if missing), an unset or literal
// Cohere__ApiKey degrades QUIETLY to sparse-only retrieval ("unranked") - which is exactly how
// the key went missing in both environments for weeks with nothing red, until a separate change found it.
// Source can pin the shape of the reference even though it cannot pin the shared value itself,
// so that is what this case asserts, in both environments the sidecar renders in.
check("Cohere__ApiKey is the shared COHERE_API_KEY reference in both environments, never a literal", () => {
  for (const environment of ["staging", "production"]) {
    const sidecar = program(createRailwayContext({ environment })).resources.find(
      (r) => r.address === "service.agent-forge-api",
    );
    assert.ok(sidecar, `no service.agent-forge-api in the ${environment} graph`);
    assert.deepEqual(
      sidecar.variables?.Cohere__ApiKey,
      { type: "sharedReference", name: "COHERE_API_KEY" },
      `${environment}: Cohere__ApiKey must be the environment's shared COHERE_API_KEY, never a literal or preserve()`,
    );
  }
});

// the module's ingest forwarder returns SILENTLY unless both of these resolve, so their
// absence was invisible until a demo upload was never cited. Pin both in both environments: the
// URI is the sidecar's private address on its PORT (never the front door - W2-D17), and the map
// is exactly {"2":"lab_pdf"}, Lab Report in the stock install schema.
check("openemr declares the ingest URI (sidecar private address) and the Lab Report category map in both environments", () => {
  for (const environment of ["staging", "production"]) {
    const openemr = program(createRailwayContext({ environment })).resources.find(
      (r) => r.address === "service.openemr",
    );
    assert.ok(openemr, `no service.openemr in the ${environment} graph`);
    assert.deepEqual(
      openemr.variables?.AGENTFORGE_INGEST_URI,
      { type: "literal", value: "http://${{agent-forge-api.RAILWAY_PRIVATE_DOMAIN}}:8080/documents/ingest" },
      `${environment}: AGENTFORGE_INGEST_URI must be the sidecar's private address, or the forwarder skips every document`,
    );
    const map = openemr.variables?.AGENTFORGE_INGEST_CATEGORY_MAP;
    assert.equal(map?.type, "literal", `${environment}: AGENTFORGE_INGEST_CATEGORY_MAP is not a literal`);
    // EXACT, not merely well-formed: "2" is `Lab Report` in the stock install schema, and any other
    // id is a different folder - which would pass a shape check and ingest nothing the demo uploads.
    assert.deepEqual(JSON.parse(map.value), { "2": "lab_pdf" },
      `${environment}: the category map must be exactly {"2":"lab_pdf"} (Lab Report); see railway.ts`);
  }
});

// The two front doors DIVERGE, by design: staging's is the develop build, and
// production's is its own literal. What stays true is that production's never follows staging's.
check("staging's proxy is this develop build; production's is its literal, whatever staging builds", () => {
  assert.equal(proxySourceFor("staging").image, `docker.io/amarquette/gauntletai:proxy-sha-${FIXTURE_SHA}`);
  assert.equal(proxySourceFor("production").image, PROXY_IMAGE_BY_ENV.production,
    "production's front door is not the literal its map entry names");
});

// ---------------------------------------------------------------------------------------
// ONLY PRODUCTION IS PINNED; STAGING TRACKS develop.
//
// Four properties, each asserted on the RENDERED graph where it can be:
//   1. staging names, for EVERY service it runs from this repo's images, the tag the develop push
//      for STAGING_BUILD_SHA published - one build, seven components;
//   2. staging refuses to render without a well-formed STAGING_BUILD_SHA rather than guessing;
//   3. production never reads it: its graph is byte-identical whatever the variable says;
//   4. TRACKS_DEVELOP is a staging-only value - in the maps, and in the resolver if a map is wrong.
const withBuildSha = (value, fn) => {
  const saved = process.env[STAGING_BUILD_SHA_VAR];
  if (value === undefined) delete process.env[STAGING_BUILD_SHA_VAR];
  else process.env[STAGING_BUILD_SHA_VAR] = value;
  try {
    return fn();
  } finally {
    if (saved === undefined) delete process.env[STAGING_BUILD_SHA_VAR];
    else process.env[STAGING_BUILD_SHA_VAR] = saved;
  }
};
const render = (environment) => program(createRailwayContext({ environment }));
const imagesOf = (graph) =>
  Object.fromEntries(
    graph.resources.filter((r) => r.type === "service").map((r) => [r.address, r.source?.image]),
  );
// The services built by this repository's publish jobs, and the tag prefix each one publishes.
const TRACKED = {
  "service.agent-forge-api": "agent-forge",
  "service.reverse-proxy": "proxy",
  "service.prometheus": "prometheus",
  "service.grafana": "grafana",
  "service.loki": "loki",
  "service.tempo": "tempo",
  "service.security-platform": "security-platform",
};

console.log("\n==: only production is pinned; staging tracks develop ==");

check("staging names this develop build's tag for every component it runs, and nothing else moves", () => {
  // Loki is declared off, so render with it switched on to prove its tag too.
  const images = withPins({ loki: { staging: TRACKS_DEVELOP } }, () => imagesOf(render("staging")));
  for (const [address, component] of Object.entries(TRACKED)) {
    assert.equal(images[address], `docker.io/amarquette/gauntletai:${component}-sha-${FIXTURE_SHA}`,
      `${address} does not track develop`);
  }
  // The two services this repo does not build keep their literals: the fork's OpenEMR and stock images.
  assert.match(images["service.openemr"], /^docker\.io\/amarquette\/gauntletai:openemr-sha-[0-9a-f]{12}$/);
  assert.equal(images["service.mysql"], "mysql:9.4");
  assert.equal(images["service.postgres"], "pgvector/pgvector:pg17");
});

check("a full 40-character sha renders exactly the graph its 12-character prefix does", () => {
  const full = FIXTURE_SHA + "0123456789abcdef0123456789ab";
  const long = withBuildSha(full, () => JSON.stringify(render("staging")));
  const short = withBuildSha(FIXTURE_SHA, () => JSON.stringify(render("staging")));
  assert.equal(long, short);
});

check("staging refuses to render with STAGING_BUILD_SHA unset or malformed, and says which variable", () => {
  const bad = [undefined, "", "latest", "develop", "feedc0ffee1", "feedc0ffee123", "FEEDC0FFEE12",
    "feedc0ffee12\n", " feedc0ffee12", "feedc0ffee12" + "0".repeat(27), "gggggggggggg"];
  for (const value of bad) {
    assert.throws(() => withBuildSha(value, () => render("staging")),
      (err) => err.message.includes(STAGING_BUILD_SHA_VAR),
      `staging rendered with ${STAGING_BUILD_SHA_VAR}=${JSON.stringify(value)}`);
  }
});

check("production's graph is identical whatever STAGING_BUILD_SHA says - it never reads it", () => {
  const baseline = withBuildSha(undefined, () => JSON.stringify(render("production")));
  for (const value of [FIXTURE_SHA, "0123456789ab", "latest", ""]) {
    assert.equal(withBuildSha(value, () => JSON.stringify(render("production"))), baseline,
      `production changed with ${STAGING_BUILD_SHA_VAR}=${JSON.stringify(value)}`);
  }
  for (const [address, component] of Object.entries(TRACKED)) {
    const image = imagesOf(JSON.parse(baseline))[address];
    if (image === undefined) continue; // production does not declare observability, Loki or Tempo yet
    assert.match(image, new RegExp(`^docker\\.io/amarquette/gauntletai:${component}-sha-[0-9a-f]{12}$`),
      `production's ${address} is not a literal ${component}-sha-<12>`);
  }
});

check("every map: staging entries track develop or are empty; no other environment tracks develop", () => {
  const maps = {
    SIDECAR_IMAGE_BY_ENV, PROXY_IMAGE_BY_ENV, LOKI_IMAGE_BY_ENV, TEMPO_IMAGE_BY_ENV,
    SECURITY_PLATFORM_IMAGE_BY_ENV,
    "OBSERVABILITY_IMAGES.prometheus": Object.fromEntries(Object.entries(OBSERVABILITY_IMAGES).map(([e, p]) => [e, p.prometheus])),
    "OBSERVABILITY_IMAGES.grafana": Object.fromEntries(Object.entries(OBSERVABILITY_IMAGES).map(([e, p]) => [e, p.grafana])),
  };
  for (const [name, map] of Object.entries(maps)) {
    for (const [env, pin] of Object.entries(map)) {
      if (env === "staging") {
        assert.ok(pin === TRACKS_DEVELOP || pin === "", `${name}.staging is the literal "${pin}"; only production is pinned`);
      } else {
        assert.notEqual(pin, TRACKS_DEVELOP, `${name}.${env} tracks develop; only staging may`);
      }
    }
  }
  // The two services staging must always run are never emptied.
  assert.equal(SIDECAR_IMAGE_BY_ENV.staging, TRACKS_DEVELOP);
  assert.equal(PROXY_IMAGE_BY_ENV.staging, TRACKS_DEVELOP);
});

// THE RULING OF 2026-09-24, pinned as state: Tempo live on staging, Loki off, neither
// in production. A STATE TRIPWIRE - switching Loki on is one line in railway.ts and one here, and
// both are meant to be a deliberate edit rather than a side effect.
check("as declared: staging runs Tempo and not Loki; production has neither key", () => {
  assert.equal(LOKI_IMAGE_BY_ENV.staging, "", "staging Loki is switched on; the 2026-09-24 ruling holds it off");
  assert.equal(TEMPO_IMAGE_BY_ENV.staging, TRACKS_DEVELOP, "staging Tempo no longer tracks develop");
  assert.equal(Object.hasOwn(LOKI_IMAGE_BY_ENV, "production"), false, "production has a Loki key");
  assert.equal(Object.hasOwn(TEMPO_IMAGE_BY_ENV, "production"), false, "production has a Tempo key");
  const res = render("staging").resources.map((r) => r.address);
  assert.ok(!res.includes("service.loki") && !res.includes("volume.loki-data"), "staging renders Loki");
  assert.ok(res.includes("service.tempo") && res.includes("volume.tempo-data"), "staging does not render Tempo");
  const sidecar = render("staging").resources.find((r) => r.address === "service.agent-forge-api");
  assert.equal(Object.hasOwn(sidecar.variables, "Observability__LokiOtlpEndpoint"), false,
    "staging's sidecar is told to ship logs to a Loki that is not there");
});

// the deterministic replayer image is staging-only and unpublished. Production has no key.
check("security-platform is staging-only, unpublished, and tracks this develop build", () => {
  assert.equal(Object.hasOwn(SECURITY_PLATFORM_IMAGE_BY_ENV, "production"), false,
    "production has a security-platform key");
  assert.equal(SECURITY_PLATFORM_IMAGE_BY_ENV.staging, TRACKS_DEVELOP);
  const staging = render("staging").resources.find((r) => r.address === "service.security-platform");
  assert.ok(staging, "staging does not render security-platform");
  assert.equal(staging.source.image,
    `docker.io/amarquette/gauntletai:security-platform-sha-${FIXTURE_SHA}`);
  assert.equal(staging.networking, undefined,
    `security-platform is published: ${JSON.stringify(staging.networking)}`);
  const keys = Object.keys(staging.variables ?? {});
  assert.ok(!keys.some((k) => /api.?key|token|secret/i.test(k)),
    `security-platform carries a credential: ${keys.join(",")}`);
  assert.equal(
    render("production").resources.find((r) => r.address === "service.security-platform"),
    undefined,
    "production renders security-platform",
  );
});

// The resolver's own refusal, for when a map is edited wrong and the case above is not run.
check("resolvePin refuses TRACKS_DEVELOP outside staging, and passes literals through untouched", () => {
  for (const env of ["production", undefined, "scratch-env", "constructor"]) {
    assert.throws(() => resolvePin("agent-forge", env, TRACKS_DEVELOP), /only staging may track develop/,
      `TRACKS_DEVELOP resolved for ${JSON.stringify(env)}`);
  }
  assert.equal(resolvePin("agent-forge", "production", "docker.io/x:agent-forge-sha-000000000000"),
    "docker.io/x:agent-forge-sha-000000000000");
  assert.equal(resolvePin("loki", "staging", "a-literal"), "a-literal");
  // And through the rendered graph: production given TRACKS_DEVELOP does not render at all.
  const saved = SIDECAR_IMAGE_BY_ENV.production;
  SIDECAR_IMAGE_BY_ENV.production = TRACKS_DEVELOP;
  try {
    assert.throws(() => render("production"), /only staging may track develop/);
  } finally {
    SIDECAR_IMAGE_BY_ENV.production = saved;
  }
});

// a separate change. railway-destructive-guard.sh refuses any plan row that changes a service's build config,
// on the premise that every service here is an image - so such a row builds nothing and only
// redeploys. Pin the premise, and pin that the one declared build is the inert value each environment
// stores (openemr, staging only: production holds none, so declaring it there plans a redeploy).
console.log("== every service is an image, and only the stored openemr build is declared ==");

for (const env of ["staging", "production"]) {
  check(`${env}: every declared service is image-sourced`, () => {
    const services = render(env).resources.filter((r) => r.address.startsWith("service."));
    assert.ok(services.length > 0, "no services rendered");
    for (const s of services) assert.equal(s.source?.type, "image", `${s.address} is not an image`);
  });
  check(`${env}: no service declares a build except openemr's stored one`, () => {
    for (const s of render(env).resources.filter((r) => r.address.startsWith("service."))) {
      const want = s.address === "service.openemr" && Object.hasOwn(OPENEMR_STORED_BUILD_BY_ENV, env)
        ? OPENEMR_STORED_BUILD_BY_ENV[env]
        : undefined;
      assert.deepEqual(s.build, want, `${s.address} build`);
    }
  });
}

check("the stored openemr build is declared for staging only", () => {
  assert.deepEqual(Object.keys(OPENEMR_STORED_BUILD_BY_ENV), ["staging"]);
  assert.deepEqual(OPENEMR_STORED_BUILD_BY_ENV.staging,
    { builder: "DOCKERFILE", dockerfilePath: "docker/railway/Dockerfile" });
});

if (failures > 0) {
  console.log(`\nSELF-TEST FAILED  ${failures} of ${cases} case(s)`);
  process.exit(1);
}
// THE COUNT, not just the word: "no FAIL line" is the shape of a pass AND of a suite that
// never reached its cases, and this line could not tell them apart before (platform.md,
// "Read a gate's POSITIVE statement").
console.log(
  `\nSELF-TEST PASSED  ${cases} of ${cases} cases - the sites volume resolves per environment, ` +
    "/ready probes Prometheus exactly where it is declared, the proxy routes /grafana " +
    "exactly where grafana is declared, production pins explicit -sha-<12> observability images " +
    "on a sized, retained, unpublished TSDB, Loki and Tempo are staging-only " +
    "and leave no trace where absent, the proxy pins an explicit proxy-sha build, and staging tracks " +
    "develop while only production is pinned, and every service is an image declaring no build " +
    "but the one each environment stores",
);
