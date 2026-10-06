#!/usr/bin/env bash
# post-deploy-verify.sh — assert the deployed system actually answers, at the FRONT DOOR.
#
#   scripts/post-deploy-verify.sh <front-door-base-url> [--globals] [--grafana] [--wait <seconds>]
#
#   scripts/post-deploy-verify.sh http://localhost:8080
#   scripts/post-deploy-verify.sh https://reverse-proxy-production-395f.up.railway.app --wait 120
#   MYSQL_ROOT_PASSWORD=… scripts/post-deploy-verify.sh http://localhost:8080 --globals
#   scripts/post-deploy-verify.sh https://<front door> --grafana    # an environment running Grafana
#
# --grafana asserts Grafana answers at <front door>/grafana AND REFUSES admin/admin. Grafana
# fails OPEN on an unset GF_SECURITY_ADMIN_PASSWORD and that login page is public, so "the tier is
# up" is not a pass on its own. Opt-in, because an environment without Grafana answers /grafana 404.
# ONE login attempt per run, outside --wait's retry loop: Grafana locks a user name out after
# repeated failures, so a retried probe could lock out a real account that is named "admin".
#
# WHY THIS EXISTS
#
# Nothing asserted that the application worked after a deploy. Every container could be healthy, the
# pipeline green and the stack unusable — and on 2026-09-18 it was: each failure was silent, and each
# was visible in one HTTP response nobody was making. Separate changes
#
# It happened again on 2026-09-23 with this script in place: it probed /agentforge/health, which
# is liveness only, and passed while /agentforge/ready answered 503. /ready is probed now.
#
# TWO HALVES, AND THE SCRIPT SAYS WHICH ONE IT RAN. The HTTP half needs nothing but the front door.
# The globals half needs the OpenEMR *database*, which no CI runner here can reach (on Railway the
# private network is not reachable from outside the environment — DEPLOYMENT.md §4), so it is opt-in
# and normally an operator step. A check that quietly skips half its job is the failure this script
# exists to end, so the summary names both halves every run, run or not.
#
# THE FRONT DOOR, NEVER A CONTAINER'S OWN HOSTNAME. DEPLOYMENT.md §2: each environment's
# `OpenEmr__BaseUrl`, OpenEMR's `site_addr_oath` and every check below are the one published origin.
# Probing `http://openemr/` would pass on a stack where every SMART launch is dead, so an internal
# hostname is refused rather than checked — the same guard tools/BootstrapOpenEmr carries.
#
# FAIL CLOSED, IN THREE DIRECTIONS. A check that cannot reach the host, a probe that returns nothing
# readable, and a check SET that shrank below EXPECTED_CHECKS are each RED — never "nothing to
# report". The third is why that count is a literal rather than ${#CHECKS[@]}: an array compared
# against its own length can never differ, so the guard would assert nothing. The globals half has
# the same shape — it asserts the tool's POSITIVE invariant (`Done: 0 changed`) and not only a list
# of drift verbs, because a denylist over another program's free text reads whatever it has not
# heard of as clean. scripts/post-deploy-verify-selftest.sh drives all three directions plus every
# assertion below; run it after changing this script.
#
# Seams, for that self-test only — both default to the real thing and neither is set in CI:
#   PDV_CURL            the HTTP client                  (default: curl)
#   PDV_BOOTSTRAP_CMD   the globals half's command       (default: dotnet run --project tools/BootstrapOpenEmr --)
#   PDV_TIMEOUT         per-request timeout, seconds     (default: 15)

set -euo pipefail

CURL="${PDV_CURL:-curl}"
BOOTSTRAP_CMD="${PDV_BOOTSTRAP_CMD:-dotnet run --project tools/BootstrapOpenEmr --}"
TIMEOUT="${PDV_TIMEOUT:-15}"

red()   { printf '\033[31m%s\033[0m' "$1"; }
green() { printf '\033[32m%s\033[0m' "$1"; }

die() { # <exit-code> <message...>
  local code="$1"; shift
  printf '%s  %s\n' "$(red 'REFUSED')" "$1" >&2
  shift || true
  for line in "$@"; do printf '         %s\n' "$line" >&2; done
  exit "$code"
}

usage() {
  printf 'Usage: %s <front-door-base-url> [--globals] [--grafana] [--wait <seconds>]\n' "${0##*/}" >&2
  exit 2
}

FRONT_DOOR=""
WANT_GLOBALS=0
WANT_GRAFANA=0
WAIT_SECONDS=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --globals) WANT_GLOBALS=1 ;;
    --grafana) WANT_GRAFANA=1 ;;
    --wait)
      shift || usage
      [ "$#" -gt 0 ] || usage
      case "$1" in ''|*[!0-9]*) usage ;; esac
      WAIT_SECONDS="$1"
      ;;
    -h|--help) usage ;;
    -*) printf 'Unknown option: %s\n' "$1" >&2; usage ;;
    *)
      [ -z "$FRONT_DOOR" ] || usage
      FRONT_DOOR="$1"
      ;;
  esac
  shift
done

[ -n "$FRONT_DOOR" ] || usage

FRONT_DOOR="${FRONT_DOOR%/}"
case "$FRONT_DOOR" in
  http://*|https://*) ;;
  *) die 2 "'$FRONT_DOOR' is not an absolute http(s) URL." \
           "Pass the published front door, e.g. http://localhost:8080." ;;
esac

# Host only: strip scheme, any userinfo, the port, and anything from the first path/query/fragment;
# then normalise the way DNS and Uri.Host do — lower case, trailing root label dropped. Without that
# this guard is strictly weaker than the C# one it claims parity with (tools/BootstrapOpenEmr matches
# a lower-cased Uri.Host), and http://OPENEMR/ reaches the container unchecked. The arms stay anchored
# globs, never substrings, so openemr.example.com and my-openemr.hospital.org still pass.
HOST="$(printf '%s' "$FRONT_DOOR" | sed -e 's#^[A-Za-z][A-Za-z0-9+.-]*://##' -e 's#[/?#].*$##' -e 's#^.*@##' -e 's#:[0-9]*$##' -e 's#[.]*$##' | tr '[:upper:]' '[:lower:]')"
case "$HOST" in
  openemr|agent-forge-api|reverse-proxy|mysql|postgres|railway.internal|*.railway.internal)
    die 2 "'$HOST' is a service-internal host, not the front door." \
          "DEPLOYMENT.md §2: everything is reached through the ONE published origin. A probe of a" \
          "container's own hostname passes on a stack where every SMART launch is already dead." ;;
esac

# path | expected status | substring the Location header must contain (empty = not checked) | what a failure means
CHECKS=(
  # No leading slash on the Location substring, deliberately: OpenEMR answers `/` with a
  # RELATIVE redirect - `Location: interface/login/login.php?site=default` - which is valid
  # HTTP (RFC 7231 §7.1.2) and which every browser and curl resolve correctly. Requiring
  # `/interface/...` made this probe FAIL against both live environments while they were
  # healthy: a false negative in the one check that is supposed to say a deploy worked.
  "/|302|interface/login/login.php|OpenEMR is not answering at the front door's root (DEPLOYMENT.md §2)"
  "/agentforge/health|200||the sidecar is not up behind the proxy, or the proxy cannot reach it"
  "/apis/default/fhir/metadata|200||OpenEMR's FHIR REST API is OFF — rest_api / rest_fhir_api are 0 (DEPLOYMENT.md §4)"
  # /health is liveness only and answered 200 through the crash loop while /ready said 503:
  # the sidecar was up and could not reach Prometheus. /ready is the dependency check (NFR-HEALTH-1).
  # 200 is required, and a 200 must carry the readiness document (check_ready_body). DEGRADED PASSES,
  # deliberately: the sidecar maps Degraded to 200 for dependencies that are optional by design - an
  # unconfigured Prometheus URL, a Week 1 stack with no vector index - and a local compose stack
  # without the observability overlay is exactly that. A configured dependency that does not answer
  # is Unhealthy, which is 503, which is red. Degraded checks are named in the output, not hidden.
  "/agentforge/ready|200||the sidecar is up but a dependency it needs is not — the body below names it"
)

# How many there are supposed to be. A LITERAL, deliberately: what this guards against is CHECKS
# losing an entry — an edit, a bad merge, a conditional that emptied it — and a count derived from
# the array shrinks with it, leaving a comparison that cannot fail. Add or remove a probe above and
# this number moves in the same commit. A separate change
EXPECTED_CHECKS=4

contains() { case "$1" in *"$2"*) return 0 ;; *) return 1 ;; esac; }

# The last probe's response body. /ready's is the only one read.
BODY_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE"' EXIT

# "<name>=<status>" per /ready check that is not Healthy, one per line. sed rather than jq: the
# GitLab job installs only bash and curl, and ReadinessResponse writes name then status, in order.
unhealthy_checks() {
  grep -o '"name":"[^"]*","status":"[^"]*"' "$BODY_FILE" 2>/dev/null \
    | sed 's/^"name":"\([^"]*\)","status":"\([^"]*\)"$/\1=\2/' | grep -v '=Healthy$' || true
}

# A 200 from /ready must be the readiness document with a status that maps to 200. An HTML page or
# an empty body answering 200 - a misrouted proxy location, say - proves nothing about dependencies.
check_ready_body() {
  local top degraded
  top="$(sed -n 's/^{"status":"\([A-Za-z]*\)".*/\1/p' "$BODY_FILE" 2>/dev/null | head -n 1)"
  case "$top" in
    Healthy) return 0 ;;
    Degraded)
      degraded="$(unhealthy_checks | tr '\n' ' ')"
      printf '  warn  /ready is Degraded (a 200, and a pass - see CHECKS): %s\n' "${degraded:-no check named}"
      return 0 ;;
    *)
      printf '  %s  GET %-32s 200, but the body does not say Healthy or Degraded (status %s)\n' \
        "$(red 'FAIL')" "/agentforge/ready" "'${top:-none}'"
      return 1 ;;
  esac
}

# Prints "<status>\t<location>" on a completed request; returns non-zero when the request did not
# complete, or completed with nothing readable in it. Both are failures, never silence.
probe() { # <url> [extra curl args...] - the URL stays LAST, which the self-test's stand-in relies on
  local url="$1" out status location
  shift
  : > "$BODY_FILE"
  if ! out="$("$CURL" -sS --max-time "$TIMEOUT" "$@" -o "$BODY_FILE" -D - -w 'PDV_STATUS:%{http_code}\n' "$url" 2>&1)"; then
    printf 'transport failure: %s\n' "$(printf '%s' "$out" | tr '\n' ' ')" >&2
    return 1
  fi
  status="$(printf '%s\n' "$out" | sed -n 's/^PDV_STATUS:\([0-9][0-9][0-9]\)$/\1/p' | tail -n 1)"
  if [ -z "$status" ] || [ "$status" = "000" ]; then
    printf 'no HTTP status in the response — the request did not complete\n' >&2
    return 1
  fi
  location="$(printf '%s\n' "$out" | sed -n 's/^[Ll]ocation:[[:space:]]*//p' | tr -d '\r' | tail -n 1)"
  printf '%s\t%s\n' "$status" "$location"
}

run_http_checks() { # prints results; returns 0 only when every check passed AND every check ran
  local failed=0 ran=0 spec path want_status want_location meaning result status location
  for spec in "${CHECKS[@]}"; do
    IFS='|' read -r path want_status want_location meaning <<<"$spec"
    ran=$((ran + 1))
    if ! result="$(probe "${FRONT_DOOR}${path}")"; then
      printf '  %s  GET %-32s could not be probed — %s\n' "$(red 'FAIL')" "$path" "$meaning"
      failed=$((failed + 1))
      continue
    fi
    status="${result%%$'	'*}"
    location="${result#*$'	'}"
    if [ "$status" != "$want_status" ]; then
      printf '  %s  GET %-32s expected %s, got %s — %s\n' "$(red 'FAIL')" "$path" "$want_status" "$status" "$meaning"
      if [ "$path" = "/agentforge/ready" ]; then
        printf '        not Healthy: %s\n' "$(unhealthy_checks | tr '\n' ' ' | sed 's/ $//' | grep . || echo 'the body named no check')"
      fi
      failed=$((failed + 1))
      continue
    fi
    if [ "$path" = "/agentforge/ready" ] && ! check_ready_body; then
      failed=$((failed + 1))
      continue
    fi
    if [ -n "$want_location" ] && ! contains "$location" "$want_location"; then
      printf '  %s  GET %-32s %s, but Location was '\''%s'\'' (expected it to contain %s)\n' \
        "$(red 'FAIL')" "$path" "$status" "$location" "$want_location"
      failed=$((failed + 1))
      continue
    fi
    printf '  %s      GET %-32s %s\n' "$(green 'ok')" "$path" "$status"
  done

  # The false-green guard, in miniature: a loop that ran nothing exits 0 on its own. Against the
  # LITERAL count, so a CHECKS array that lost an entry reddens instead of passing with fewer.
  if [ "$ran" -ne "$EXPECTED_CHECKS" ]; then
    printf '  %s  ran %s of %s checks — the check set did not execute\n' "$(red 'FAIL')" "$ran" "$EXPECTED_CHECKS"
    return 1
  fi
  [ "$failed" -eq 0 ]
}

# The globals half: tools/BootstrapOpenEmr is idempotent and prints `ok` or `SET`/`ENABLED` per
# setting, so a converged deploy is all `ok`. Anything else means the deploy did not carry the
# settings the copilot needs and the tool has just repaired them — which is a red result, not a
# quiet fix. A separate change, DEPLOYMENT.md §4
#
# TWO TESTS, AND THE SECOND IS THE LOAD-BEARING ONE. Matching the drift verbs SET/ENABLED is a
# DENYLIST over another program's free-text stdout: rename a verb or add a third and drift reads as
# clean — and tools/BootstrapOpenEmr exits 0 whether or not it changed anything, so rc cannot back it
# up. So the decisive test is the POSITIVE invariant the tool actually promises, `Done: 0 changed`,
# which survives any rename. Those format strings are a gate input; Program.cs says so where it emits
# them. A separate change
run_globals_check() {
  local out rc=0 reported drift
  printf '\nglobals (OpenEMR database state, DEPLOYMENT.md §4):\n'
  set +e
  out="$($BOOTSTRAP_CMD "$FRONT_DOOR" 2>&1)"
  rc=$?
  set -e
  printf '%s\n' "$out" | sed 's/^/  | /'

  reported="$(printf '%s\n' "$out" | grep -cE '^[[:space:]]*(ok|SET|ENABLED)([[:space:]]|$)' || true)"
  if [ "${reported:-0}" -eq 0 ]; then
    printf '  %s  the bootstrap reported no settings at all — it did not run, or it changed shape\n' "$(red 'FAIL')"
    return 1
  fi
  if [ "$rc" -ne 0 ]; then
    printf '  %s  the bootstrap exited %s\n' "$(red 'FAIL')" "$rc"
    return 1
  fi
  drift="$(printf '%s\n' "$out" | grep -E '^[[:space:]]*(SET|ENABLED)([[:space:]]|$)' || true)"
  if [ -n "$drift" ]; then
    printf '  %s  the deploy did NOT carry these settings; the bootstrap has just repaired them:\n' "$(red 'FAIL')"
    printf '%s\n' "$drift" | sed 's/^[[:space:]]*/        /'
    return 1
  fi
  # The positive invariant, and the only one of the two that still holds if the tool renames its
  # drift verbs or grows a third one.
  if ! printf '%s\n' "$out" | grep -qE '^[[:space:]]*Done:[[:space:]]+0[[:space:]]+changed'; then
    printf '  %s  the bootstrap did not report `Done: 0 changed` — the deploy did not carry these\n' "$(red 'FAIL')"
    printf '         settings and the tool has just repaired them, or its output changed shape\n'
    return 1
  fi
  printf '  %s      %s settings already correct\n' "$(green 'ok')" "$reported"
}

# The Grafana half. Two probes in order, and the second is the point: a Grafana that is up
# and accepts admin/admin is green on every other check in this script. EXACTLY 401 passes, not
# "anything but 200", so a 404 (no route) or a 5xx can never read as "refused".
run_grafana_check() {
  local result status
  printf '\ngrafana (<front door>/grafana):\n'
  if ! result="$(probe "${FRONT_DOOR}/grafana/api/health")"; then
    printf '  %s  GET /grafana/api/health could not be probed\n' "$(red 'FAIL')"
    return 1
  fi
  status="${result%%$'\t'*}"
  if [ "$status" != "200" ]; then
    printf '  %s  GET /grafana/api/health expected 200, got %s — Grafana is not reachable through the proxy\n' \
      "$(red 'FAIL')" "$status"
    return 1
  fi
  printf '  %s      GET %-32s %s\n' "$(green 'ok')" "/grafana/api/health" "$status"
  if ! result="$(probe "${FRONT_DOOR}/grafana/login" -X POST -H 'Content-Type: application/json' \
      --data '{"user":"admin","password":"admin"}')"; then
    printf '  %s  POST /grafana/login could not be probed — admin/admin is NOT proven refused\n' "$(red 'FAIL')"
    return 1
  fi
  status="${result%%$'\t'*}"
  case "$status" in
    401)
      printf '  %s      POST %-31s %s — admin/admin refused\n' "$(green 'ok')" "/grafana/login" "$status" ;;
    200)
      printf '  %s  POST /grafana/login ACCEPTED admin/admin. Set GRAFANA_ADMIN_USER and\n' "$(red 'FAIL')"
      printf '         GRAFANA_ADMIN_PASSWORD as shared variables, then redeploy grafana (DEPLOYMENT.md §9)\n'
      return 1 ;;
    *)
      printf '  %s  POST /grafana/login expected 401, got %s — admin/admin is NOT proven refused\n' \
        "$(red 'FAIL')" "$status"
      return 1 ;;
  esac
}

printf 'Verifying the deployed system at its front door: %s\n\n' "$FRONT_DOOR"
printf 'http (front door, DEPLOYMENT.md §2):\n'

# --wait re-runs the whole HTTP set until the deadline. A deploy that is still rolling is not a
# defect; a deploy that is still broken at the deadline is. The deadline always arrives.
deadline=$(( $(date +%s) + WAIT_SECONDS ))
http_ok=0
while :; do
  if run_http_checks; then http_ok=1; break; fi
  [ "$(date +%s)" -lt "$deadline" ] || break
  printf '  … retrying (up to %ss total)\n' "$WAIT_SECONDS"
  sleep 5
done

grafana_ok=skipped
if [ "$WANT_GRAFANA" -eq 1 ]; then
  if run_grafana_check; then grafana_ok=pass; else grafana_ok=fail; fi
fi

globals_ok=skipped
if [ "$WANT_GLOBALS" -eq 1 ]; then
  if run_globals_check; then globals_ok=pass; else globals_ok=fail; fi
fi

printf '\nsummary:\n'
printf '  http     %s (%s checks against %s)\n' \
  "$([ "$http_ok" -eq 1 ] && green PASS || red FAIL)" "${#CHECKS[@]}" "$FRONT_DOOR"
case "$globals_ok" in
  pass) printf '  globals  %s\n' "$(green PASS)" ;;
  fail) printf '  globals  %s\n' "$(red FAIL)" ;;
  *)    printf '  globals  %s — pass --globals with MySQL reachable to assert them (DEPLOYMENT.md §4)\n' \
          "$(red 'NOT CHECKED')" ;;
esac

case "$grafana_ok" in
  pass) printf '  grafana  %s (admin/admin refused)\n' "$(green PASS)" ;;
  fail) printf '  grafana  %s\n' "$(red FAIL)" ;;
  *)    printf '  grafana  not checked — pass --grafana where the environment runs Grafana\n' ;;
esac

if [ "$http_ok" -eq 1 ] && [ "$globals_ok" != "fail" ] && [ "$grafana_ok" != "fail" ]; then
  exit 0
fi
exit 1
