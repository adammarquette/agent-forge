#!/usr/bin/env bash
# railway-stays-up-selftest.sh — make scripts/railway-stays-up.sh go RED for the reason it exists:
# a service that deployed, reported SUCCESS, and did not stay up.
#
# THE CASE THAT MATTERS IS THE RECORDED ONE. scripts/fixtures/railway-stays-up/ holds the
# `deploymentLogs` response Railway returns today for staging prometheus deployment 8eead98c, the
# crash loop a separate change was filed for (read-only, 2026-09-25), and a healthy prometheus deployment read
# the same way. Case 2 serves that crash log under a deployment record saying everything is fine -
# SUCCESS, not stopped, instance RUNNING - which is what Railway reported while it happened. The
# check must redden on the log alone. Every other signal gets its own case, so deleting any one of
# them reddens exactly the case that names it.
#
# No network, no Railway account, no token: the subject's one seam, RAILWAY_STAYS_UP_TRANSPORT,
# is stubbed with a script that answers each GraphQL query from files on disk.
#
# Run it:  bash scripts/railway-stays-up-selftest.sh
# Under the image CI uses:
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c 'apk add -q bash jq && bash scripts/railway-stays-up-selftest.sh'
#
# DEPLOYMENT.md §9

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/railway-stays-up.sh"
FIX="$HERE/fixtures/railway-stays-up"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# FIRST, AND ON PURPOSE: EX_ENV is 1, which is also what bash exits with on a file it cannot
# parse, so a broken subject would pass every usage case below.
if ! bash -n "$GATE" 2>"$tmp/parse.err"; then
    printf '\033[31mSELF-TEST FAILED\033[0m  %s is not parseable — nothing below was a real test\n' "$GATE" >&2
    sed 's/^/      /' "$tmp/parse.err" >&2
    exit 1
fi
for f in crashloop-prometheus-8eead98c.logs.json healthy-prometheus-240e5078.logs.json; do
    [ -f "$FIX/$f" ] || { printf '\033[31mSELF-TEST FAILED\033[0m  fixture %s is missing\n' "$FIX/$f" >&2; exit 1; }
done

# --- the stubbed transport ----------------------------------------------------------------------
# scope.json answers the scope query. graph.N.json answers the Nth environment read when it exists,
# graph.json otherwise - so a service that moves between polls is expressible. dep.<id>.N.json
# answers the Nth read of a deployment the same way, falling back to dep.<id>.json. logs.<id>.json
# answers per deployment; a missing dep file, or a file holding the word ERROR, is a
# transport failure. logs default to the healthy recorded fixture.
cat >"$tmp/stub.sh" <<'STUB'
body="$(cat)"
id="$(printf '%s' "$body" | "$REAL_JQ" -r '.variables.id // empty')"
case "$body" in
    *projectToken*) f="$TMPD/scope.json" ;;
    *environments*)
        n=$(( $(cat "$TMPD/graph.count" 2>/dev/null || echo 0) + 1 )); echo "$n" >"$TMPD/graph.count"
        f="$TMPD/graph.$n.json"; [ -f "$f" ] || f="$TMPD/graph.json" ;;
    *deploymentLogs*) f="$TMPD/logs.$id.json"; [ -f "$f" ] || f="$TMPD/logs.json" ;;
    *deployment*)
        n=$(( $(cat "$TMPD/dep.$id.count" 2>/dev/null || echo 0) + 1 )); echo "$n" >"$TMPD/dep.$id.count"
        f="$TMPD/dep.$id.$n.json"; [ -f "$f" ] || f="$TMPD/dep.$id.json" ;;
    *) echo "stub: unknown query" >&2; exit 1 ;;
esac
[ -f "$f" ] || { echo "stub: no $f" >&2; exit 1; }
if [ "$(head -c 5 "$f")" = "ERROR" ]; then echo "stub: transport failure" >&2; exit 1; fi
cat "$f"
STUB

export TMPD="$tmp"
# The stub's own jq call is pinned to the REAL binary by full path, resolved once here and exported,
# so a later case that puts a CRLF-emitting jq first on PATH (below) corrupts only the
# SUBJECT's reads, not this test double's.
export REAL_JQ="$(command -v jq)"
export RAILWAY_STAYS_UP_TRANSPORT="bash $tmp/stub.sh"
export RAILWAY_STAYS_UP_POLL=1

ENV_A="43e5ee34-ad72-4dbf-81ac-b52bd138dae4"
ENV_B="0e2d543a-5776-46af-992e-5f60e91aa726"
PROJ="8f007871-c6ee-483a-ba93-1d65673dee6f"
PROM="d9a808fe-9cdc-4766-ab4f-129aaa63cf3f"
API="a1a1a1a1-0000-4000-8000-000000000001"
OLD_PROM="5a5b716b-3c9a-4f64-995e-e9f8b44dabc6"
OLD_API="b2b2b2b2-0000-4000-8000-000000000002"
NEW_PROM="8eead98c-1ff9-4886-8dbb-f634cf8d6594"
NEW_API="c3c3c3c3-0000-4000-8000-000000000003"

scope() { jq -n --arg p "$PROJ" --arg e "$1" '{data: {projectToken: {projectId: $p, environmentId: $e}}}' >"$tmp/scope.json"; }

si() { # si <serviceId> <serviceName> <deploymentId-or-empty>
    if [ -z "$3" ]; then jq -nc --arg i "$1" --arg n "$2" '{serviceId: $i, serviceName: $n, latestDeployment: null}'
    else jq -nc --arg i "$1" --arg n "$2" --arg d "$3" '{serviceId: $i, serviceName: $n, latestDeployment: {id: $d, status: "SUCCESS"}}'; fi
}

graph() { # graph <file> <environment-id> <service-instance-json-array> - in the API's own nesting
    jq -n --arg e "$2" --argjson s "$3" '{data: {project: {name: "fearless-abundance", environments: {edges: [
        {node: {id: $e, name: "stub", serviceInstances: {edges: [$s[] | {node: .}]}}}]}}}}' >"$tmp/$1"
}

dep() { # dep <deployment-id>[.N] <status> <deploymentStopped> <instance-status...>
    local id="$1" st="$2" stopped="$3"; shift 3
    jq -n --arg i "${id%%.*}" --arg s "$st" --argjson x "$stopped" --args \
        '{data: {deployment: {id: $i, status: $s, deploymentStopped: $x, instances: [$ARGS.positional[] | {status: .}]}}}' "$@" >"$tmp/dep.$id.json"
}

capture() { # capture <environment-id> <service-json-array>: a railway-deploy-identity.sh state file
    jq -n --arg e "$1" --arg p "$PROJ" --argjson s "$2" '{kind: "railway.deploy.identity", version: 1,
        capturedAt: "2026-09-23T19:52:00Z", projectId: $p, environmentId: $e,
        services: [$s[] | {serviceId, serviceName, deploymentId: (.latestDeployment.id // null), status: (.latestDeployment.status // null)}]}' >"$tmp/state.json"
}

reset() { # a clean world: staging token, prometheus moved OLD -> NEW, api unmoved, all healthy
    rm -f "$tmp"/graph.* "$tmp"/dep.* "$tmp"/logs.* "$tmp/state.json"
    scope "$ENV_A"
    capture "$ENV_A" "[$(si "$PROM" prometheus "$OLD_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
    graph graph.json "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
    dep "$NEW_PROM" SUCCESS false RUNNING
    cp "$FIX/healthy-prometheus-240e5078.logs.json" "$tmp/logs.json"
}

cat >"$tmp/plan-empty.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "changes": [] } }
EOF
cat >"$tmp/plan-changes.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "changes": [
  { "address": "service.prometheus", "kind": "resource.update", "severity": "safe", "summary": "Update prometheus source.image" }
] } }
EOF

# --- harness ------------------------------------------------------------------------------------
fails=0
ran=0
# expect <exit> <text the output must contain> <description> [gate args...]; --state defaults in.
expect() {
    local want="$1" saying="$2" what="$3"; shift 3
    ran=$((ran + 1))
    local out got
    out="$(bash "$GATE" "$@" 2>&1)"; got=$?
    if [ "$got" -ne "$want" ]; then
        printf '\033[31mSELF-TEST FAILED\033[0m  %s — expected exit %s, got %s\n' "$what" "$want" "$got" >&2
        printf '%s\n' "$out" | sed 's/^/      /' >&2
        fails=$((fails + 1)); return
    fi
    case "$out" in
        *"$saying"*) ;;
        *) printf '\033[31mSELF-TEST FAILED\033[0m  %s — exit %s was right but the output never said "%s"\n' "$what" "$want" "$saying" >&2
           printf '%s\n' "$out" | sed 's/^/      /' >&2
           fails=$((fails + 1)) ;;
    esac
}
S=(--state "$tmp/state.json" --settle 0 --wait 0)

# --- green ----------------------------------------------------------------------------------------
reset
expect 0 "STAYED UP" "a fresh deployment, SUCCESS, RUNNING, with a healthy recorded log, stays up" "${S[@]}"

# --- THE RECORDED CRASH LOOP --------------------------------------------------------------------
# Status SUCCESS, not stopped, instance RUNNING: what the API said while prometheus panicked eleven
# times. Only the log knows. Remove the CRASH_MARKERS test and this goes green.
reset
cp "$FIX/crashloop-prometheus-8eead98c.logs.json" "$tmp/logs.$NEW_PROM.json"
expect 10 "panic: Unable to create mmap-ed active query log" \
    "THE RECORDED CRASH LOOP reddens on its log alone, under a record that says SUCCESS/RUNNING" "${S[@]}"

# The same, with a settle window: the verdict is reached on the first poll, not after it.
reset
cp "$FIX/crashloop-prometheus-8eead98c.logs.json" "$tmp/logs.$NEW_PROM.json"
expect 10 "is not staying up" "the recorded crash loop reddens inside a settle window too" --state "$tmp/state.json" --settle 30 --wait 60

# --- each API signal alone, under a healthy log ------------------------------------------------
reset; dep "$NEW_PROM" SUCCESS true RUNNING
expect 10 "deploymentStopped is true" "deploymentStopped=true reddens though the status says SUCCESS" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false CRASHED
expect 10 "instance status CRASHED" "an instance CRASHED under a SUCCESS deployment reddens" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false EXITED
expect 10 "instance status EXITED" "an instance EXITED reddens" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false RESTARTING
expect 10 "instance status RESTARTING" "an instance RESTARTING reddens - a restart after a deploy is a crash" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false RUNNING CRASHED
expect 10 "instance status CRASHED" "one crashed replica among running ones reddens" "${S[@]}"

reset; dep "$NEW_PROM" CRASHED false RUNNING
expect 10 "deployment status CRASHED" "a deployment Railway itself marks CRASHED reddens" "${S[@]}"

reset; dep "$NEW_PROM" FAILED false
expect 10 "deployment status FAILED" "a FAILED deployment reddens" "${S[@]}"

# A second `Starting Container` in one deployment's log is a restart, whatever else it says. The
# healthy recorded log already carries the first.
reset
jq '.data.deploymentLogs += [{message: "Starting Container", severity: "info", timestamp: "2026-09-25T01:10:05Z"}]' \
    "$FIX/healthy-prometheus-240e5078.logs.json" >"$tmp/logs.$NEW_PROM.json"
expect 10 "started 2 times" "a container that started twice reddens" "${S[@]}"

# Each other marker, one at a time, into an otherwise healthy log.
for m in "fatal error: all goroutines are asleep - deadlock!" \
         "Unhandled exception. System.InvalidOperationException: boom" \
         "Segmentation fault (core dumped)" \
         "PHP Fatal error:  Uncaught Error: Call to undefined function" \
         "2026/09/25 01:10:00 [emerg] 1#1: host not found in upstream \"agent-forge-api\""; do
    reset
    jq --arg m "$m" '.data.deploymentLogs += [{message: $m, severity: "error", timestamp: "2026-09-25T01:10:00Z"}]' \
        "$FIX/healthy-prometheus-240e5078.logs.json" >"$tmp/logs.$NEW_PROM.json"
    expect 10 "crash marker" "crash marker reddens: ${m:0:40}" "${S[@]}"
done

# ...and the line that must NOT: ASP.NET logs a per-request failure this way, and it is not a crash.
reset
jq '.data.deploymentLogs += [{message: "An unhandled exception has occurred while executing the request.", severity: "error", timestamp: "2026-09-25T01:10:00Z"}]' \
    "$FIX/healthy-prometheus-240e5078.logs.json" >"$tmp/logs.$NEW_PROM.json"
expect 0 "STAYED UP" "ASP.NET's per-request 'unhandled exception' line is not read as a crash" "${S[@]}"

# --- only what the apply deployed is judged ----------------------------------------------------
# The api service did not move, and its (old) deployment would be red. It is not this apply's.
reset; dep "$OLD_API" CRASHED true CRASHED
expect 0 "STAYED UP" "a service the apply did not move is not judged" "${S[@]}"

# Two services moved; the second is crashed, and the first being fine does not carry it.
reset
graph graph.json "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$NEW_API")]"
dep "$NEW_API" SUCCESS false EXITED
expect 10 "agent-forge-api" "every moved service is judged, not just the first" "${S[@]}"

# A service that moves on the SECOND read is picked up and judged. Needs a window, so two polls run.
reset
graph graph.2.json "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$NEW_API")]"
dep "$NEW_API" SUCCESS true RUNNING
expect 10 "agent-forge-api" "a service that moves after the first read is watched too" --state "$tmp/state.json" --settle 2 --wait 10

# --- the settle window --------------------------------------------------------------------------
reset; dep "$NEW_PROM" DEPLOYING false
expect 11 "settle window" "a deployment that never reaches SUCCESS within --wait is undecidable, not a pass" "${S[@]}"

reset
expect 11 "settle window" "a window longer than the wait cannot be met: healthy once is not healthy for --settle" \
    --state "$tmp/state.json" --settle 5 --wait 1

reset
expect 0 "STAYED UP" "healthy on every poll across a real 2s window passes" --state "$tmp/state.json" --settle 2 --wait 10

# THE WINDOW IS PER DEPLOYMENT, not per run. Both deployments first look healthy on poll 3, by
# which time the RUN is 2s old; the api crashes on poll 4. A window measured from the start of the
# run passes on poll 3 and never sees the crash. Only "each healthy for --settle" reaches poll 4.
reset
graph graph.1.json "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
graph graph.2.json "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
graph graph.json   "$ENV_A" "[$(si "$PROM" prometheus "$NEW_PROM"),$(si "$API" agent-forge-api "$NEW_API")]"
dep "$NEW_PROM.1" DEPLOYING false
dep "$NEW_PROM.2" DEPLOYING false
dep "$NEW_API.1" SUCCESS false RUNNING
dep "$NEW_API" SUCCESS true RUNNING
expect 10 "deploymentStopped is true" "each deployment must be healthy for the whole window, not the run" \
    --state "$tmp/state.json" --settle 2 --wait 20

reset; dep "$NEW_PROM" SUCCESS false
expect 11 "no instance is reported" "SUCCESS with no instance at all is not a pass" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false CREATED
expect 11 "still starting" "an instance still CREATED is not yet up" "${S[@]}"

# --- superseded ---------------------------------------------------------------------------------
reset
graph graph.2.json "$ENV_A" "[$(si "$PROM" prometheus "d4d4d4d4-0000-4000-8000-000000000004"),$(si "$API" agent-forge-api "$OLD_API")]"
expect 11 "no longer the active one" "a watched deployment replaced mid-window is undecidable" --state "$tmp/state.json" --settle 3 --wait 10

# --- fail closed: nothing it cannot read is a pass --------------------------------------------
reset; echo ERROR >"$tmp/logs.$NEW_PROM.json"
expect 11 "logs of deployment" "logs that cannot be read are undecidable, never a pass" "${S[@]}"

reset; echo '{"errors":[{"message":"Not Authorized"}]}' >"$tmp/logs.$NEW_PROM.json"
expect 11 "Not Authorized" "a GraphQL error on the logs read (HTTP 200 + errors) is undecidable" "${S[@]}"

reset; echo '{"data":{"deploymentLogs":null}}' >"$tmp/logs.$NEW_PROM.json"
expect 11 "not an array of messages" "a logs response without the array is undecidable, not 'no markers'" "${S[@]}"

reset; rm -f "$tmp/dep.$NEW_PROM.json"
expect 11 "could not be read" "a deployment record that cannot be read is undecidable" "${S[@]}"

reset; echo '{"data":{"deployment":{"id":"x","status":"SUCCESS","instances":[{"status":"RUNNING"}]}}}' >"$tmp/dep.$NEW_PROM.json"
expect 11 "API shape changed" "a deployment record missing deploymentStopped is undecidable" "${S[@]}"

reset; dep "$NEW_PROM" TELEPORTING false RUNNING
expect 11 "not one this check knows" "a deployment status it has never heard of is undecidable" "${S[@]}"

reset; dep "$NEW_PROM" SUCCESS false HOVERING
expect 11 "not one this check knows" "an instance status it has never heard of is undecidable" "${S[@]}"

reset; echo ERROR >"$tmp/graph.json"
expect 11 "could not be read at all" "an environment it cannot read at all is undecidable" "${S[@]}"

reset; echo '{"data":{"project":{"environments":{"edges":"nope"}}}}' >"$tmp/graph.json"
expect 11 "could not be read at all" "an environment response of the wrong shape is undecidable" "${S[@]}"

reset; scope "$ENV_B"
expect 11 "captured in" "a token for another environment than the capture is undecidable" "${S[@]}"

reset; echo ERROR >"$tmp/scope.json"
expect 11 "which environment" "a scope that cannot be read is undecidable" "${S[@]}"

reset; rm -f "$tmp/state.json"
expect 11 "no state file" "no state file is undecidable" "${S[@]}"

reset; echo '{"kind":"something.else","services":[]}' >"$tmp/state.json"
expect 11 "is not a" "a state file that is not a deploy-identity capture is undecidable" "${S[@]}"

# --- nothing moved ------------------------------------------------------------------------------
reset; graph graph.json "$ENV_A" "[$(si "$PROM" prometheus "$OLD_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
expect 11 "nothing to watch" "nothing moved and no --plan: undecidable, not a pass" "${S[@]}"

reset; graph graph.json "$ENV_A" "[$(si "$PROM" prometheus "$OLD_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
expect 11 "nothing to watch" "nothing moved under a NON-empty plan is undecidable" "${S[@]}" --plan "$tmp/plan-changes.json"

reset; graph graph.json "$ENV_A" "[$(si "$PROM" prometheus "$OLD_PROM"),$(si "$API" agent-forge-api "$OLD_API")]"
expect 0 "NO-OP" "nothing moved under a provably empty plan is a pass" "${S[@]}" --plan "$tmp/plan-empty.json"

# --- usage ----------------------------------------------------------------------------------------
expect 1 "--state is required" "no --state is a usage error"
expect 1 "whole seconds" "a non-numeric --settle is a usage error" --state "$tmp/state.json" --settle soon

# --- CRLF-emitting jq --------------------------------------------------------------------
# Windows Git Bash's jq (measured: 1.8.2) writes CRLF line endings. `$(jq ...)` strips only the
# trailing `\n`, never a `\r`, so every read into a variable ends with a literal carriage return and
# every comparison against it — including `deploymentId != null` — silently fails, which is why this
# script read `deployment null` for every service run natively on Windows though the identical check
# passed under alpine:3.21. Simulate it with a jq STUB placed first on PATH that pipes the real jq's
# output through `awk` to append `\r` before every newline, so this case exercises the override
# (scripts/lib/jq-crlf.sh) rather than trusting it never regresses. Without that override this case
# fails exactly as the recorded bug did: a healthy world reads as `nothing moved`/UNDECIDABLE rather
# than STAYED UP. A separate change
#
# `exit "${PIPESTATUS[0]}"` IS LOAD-BEARING. Without it the stub's own exit status is awk's, not the
# real jq's — awk always exits 0, so a `jq -e` call that correctly found `false` (exit 1) would be
# reported as exit 0 by the stub, which is a property of the TEST DOUBLE piping jq into awk, not of
# real Windows jq (whose own process sets its own exit code; nothing internal to it pipes into
# anything). Confirmed by hand: without this line the stub silently turns every `jq -e` guard in the
# subject into a pass, which would make this case validate the wrong thing.
mkdir -p "$tmp/bin"
cat >"$tmp/bin/jq" <<STUB2
#!/usr/bin/env bash
"$REAL_JQ" "\$@" | awk '{printf "%s\r\n", \$0}'
exit "\${PIPESTATUS[0]}"
STUB2
chmod +x "$tmp/bin/jq"

reset
ran=$((ran + 1))
out="$(PATH="$tmp/bin:$PATH" bash "$GATE" "${S[@]}" 2>&1)"; got=$?
case "$out" in
    *"STAYED UP"*) [ "$got" -eq 0 ] || { printf '\033[31mSELF-TEST FAILED\033[0m  a CRLF-emitting jq first on PATH still reaches STAYED UP text but exits %s, not 0\n' "$got" >&2; fails=$((fails + 1)); } ;;
    *) printf '\033[31mSELF-TEST FAILED\033[0m  a healthy world under a CRLF-emitting jq does not read STAYED UP (exit %s)\n' "$got" >&2
       printf '%s\n' "$out" | sed 's/^/      /' >&2
       fails=$((fails + 1)) ;;
esac

# ...and the CRLF stub must not launder a real crash away either — the override has to survive in
# both directions, not just turn every read into a pass.
reset
cp "$FIX/crashloop-prometheus-8eead98c.logs.json" "$tmp/logs.$NEW_PROM.json"
ran=$((ran + 1))
out="$(PATH="$tmp/bin:$PATH" bash "$GATE" "${S[@]}" 2>&1)"; got=$?
case "$out" in
    *"panic: Unable to create mmap-ed active query log"*) [ "$got" -eq 10 ] || { printf '\033[31mSELF-TEST FAILED\033[0m  the recorded crash loop under a CRLF-emitting jq names the panic but exits %s, not 10\n' "$got" >&2; fails=$((fails + 1)); } ;;
    *) printf '\033[31mSELF-TEST FAILED\033[0m  the recorded crash loop under a CRLF-emitting jq did not name the panic (exit %s)\n' "$got" >&2
       printf '%s\n' "$out" | sed 's/^/      /' >&2
       fails=$((fails + 1)) ;;
esac

# --- the count --------------------------------------------------------------------------------------
EXPECTED_ASSERTIONS=47
if [ "$ran" -ne "$EXPECTED_ASSERTIONS" ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  ran %s of %s cases\n' "$ran" "$EXPECTED_ASSERTIONS" >&2
    fails=$((fails + 1))
fi
if [ "$fails" -gt 0 ]; then
    printf '\n\033[31m%d self-test failure(s).\033[0m railway-stays-up.sh is not behaving as documented.\n' "$fails" >&2
    exit 1
fi
printf 'railway-stays-up-selftest: SELF-TEST PASSED - %s of %s cases\n' "$ran" "$EXPECTED_ASSERTIONS"
