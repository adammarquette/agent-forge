#!/usr/bin/env bash
# railway-stays-up.sh — prove every service an apply deployed is still UP after a settle window,
# rather than merely deployed.
#
#   scripts/railway-stays-up.sh --state <deploy-identity-state> [--plan FILE] [--settle SECONDS] [--wait SECONDS]
#
# Runs AFTER `railway-deploy-identity.sh run`, and reads the same state file: the services whose
# latest deployment id moved away from what that file captured are the ones this apply deployed,
# and they are the ones watched.
#
# WHY THIS EXISTS
#
# On 2026-09-23 staging's prometheus crash-looped on every deployment since 2026-09-20: eleven
# starts in nine seconds, `panic: Unable to create mmap-ed active query log` each time, then
# stopped. Every check passed. Railway kept the deployment at SUCCESS until the next one replaced
# it 78 minutes later; railway-deploy-identity.sh said SHIPPED, which it was; post-deploy-verify.sh
# never asked the one endpoint that was answering 503. The maintainer caught it by eye.
#
# SO THE DEPLOYMENT STATUS IS NOT THE EVIDENCE. It is read, and CRASHED or FAILED reddens, but the
# recorded crash loop shows SUCCESS can sit over a dead container. Four things are read per fresh
# deployment, and any one of them reddens:
#
#   1. `deployment.status`         CRASHED / FAILED / REMOVED / SKIPPED / SLEEPING ... is red.
#   2. `deployment.deploymentStopped`  true means every instance has stopped: red.
#   3. `deployment.instances[].status`  CRASHED / EXITED / RESTARTING / STOPPED ... is red. A pass
#                                  needs at least one instance and every one RUNNING.
#   4. the deployment's own LOGS   a crash marker (CRASH_MARKERS below) or more than one
#                                  `Starting Container` line is red. This is the one that caught the
#                                  recorded loop: its status said SUCCESS, its log said panic x11.
#
# THE MARKERS WERE CALIBRATED, NOT GUESSED. Read-only, on 2026-09-25: zero hits across the latest
# deployment of all 15 services in both environments (every one healthy), and 11 in the recorded
# crash loop (scripts/fixtures/railway-stays-up/). A marker that fires on a healthy service is a
# false RED after a successful apply, the direction chosen on purpose, as railway-deploy-identity.sh
# chose it: nothing is broken, and a human reads the matching lines, which are printed.
#
# THE SETTLE WINDOW. A pass needs every watched deployment to have looked healthy on every poll for
# --settle seconds (default 60) - the recorded loop took nine seconds to die, so a single read just
# after the apply could have caught it RUNNING. --wait (default 300) bounds how long a deployment
# may take to reach SUCCESS at all. A service that moves AFTER the check passed is not watched; the
# set is re-read on every poll, so one that moves while it runs is.
#
# FAIL CLOSED. Everything that stops it looking is UNDECIDABLE and red: no credential, an API error,
# a response without the shape below, a state file that is not a deploy-identity capture, a token
# for another environment, logs it cannot read, a status it has never heard of, a watched deployment
# superseded by one it did not see start, and a deadline reached before the window settled.
#
# WHAT IT READS (checked against the live schema on 2026-09-25 with `railway api describe`):
#
#   query { projectToken { projectId environmentId } }
#   project(id:) { environments { ... serviceInstances { serviceId serviceName latestDeployment { id status } } } }
#   deployment(id:) { id status deploymentStopped instances { status } }
#   deploymentLogs(deploymentId:, limit:) { message }
#
# The first two are railway-deploy-identity.sh's, proven under a CI project token. The last two
# are not yet proven under one: if a project token cannot read them, this reddens UNDECIDABLE on
# the first run rather than passing, and the message says which read failed.
#
# EXIT CODES
#   0   STAYED UP — every deployment the apply produced was healthy for the whole settle window.
#       Or NO-OP: nothing moved and `--plan` proves the plan was empty.
#   1   usage or environment error
#  10   CRASHED — positive evidence that a fresh deployment is not staying up
#  11   UNDECIDABLE — it could not tell; every caller treats it exactly as CRASHED
#
# Seams, for scripts/railway-stays-up-selftest.sh only:
#   RAILWAY_STAYS_UP_TRANSPORT  a command reading the GraphQL body on stdin, writing the response
#   RAILWAY_STAYS_UP_POLL       seconds between reads (default 10)
#
# reference: DEPLOYMENT.md §9

set -uo pipefail

EX_OK=0
EX_ENV=1
EX_CRASHED=10
EX_UNDECIDABLE=11

STATE_KIND="railway.deploy.identity"
ENDPOINT="${RAILWAY_STAYS_UP_ENDPOINT:-https://backboard.railway.com/graphql/v2}"
POLL_SECONDS="${RAILWAY_STAYS_UP_POLL:-10}"
LOG_LIMIT=500

# ERE over each log line's message. Go's runtime panic and fatal error, .NET's unhandled-exception
# crash (capital U and the full stop: ASP.NET's per-request "An unhandled exception has occurred"
# is not a crash), a segfault, PHP's fatal, and nginx refusing its config.
CRASH_MARKERS='^(panic: |fatal error: |Unhandled exception\. )|Segmentation fault|PHP Fatal error|\[emerg\]'

say() { printf '%s\n' "railway-stays-up: $*" >&2; }
red() { printf '\033[31m%s\033[0m %s\n' "$1" "$2" >&2; }

usage() {
    cat >&2 <<'USAGE'
usage: railway-stays-up.sh --state FILE [--plan FILE] [--settle SECONDS] [--wait SECONDS]

  --state FILE   the railway-deploy-identity.sh capture taken before the apply. Required.
  --plan FILE    the pinned plan. Its only use is to excuse an environment where nothing moved,
                 when the plan is provably empty.
  --settle N     seconds every fresh deployment must stay healthy (default 60).
  --wait N       seconds to reach that, from the start (default 300).
USAGE
}

command -v jq >/dev/null 2>&1 \
    || { red "CANNOT RUN" "jq is required and is not on PATH."; exit "$EX_ENV"; }
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" \
    || { red "CANNOT RUN" "scripts/lib/jq-crlf.sh refused to load."; exit "$EX_ENV"; }

# --- transport (same seam shape and auth as railway-deploy-identity.sh) -------------------------
gql() { # gql <query> <variables-json>
    local body auth_header
    body="$(jq -n --arg q "$1" --argjson v "$2" '{query: $q, variables: $v}')"
    if [ -n "${RAILWAY_STAYS_UP_TRANSPORT:-}" ]; then
        printf '%s' "$body" | $RAILWAY_STAYS_UP_TRANSPORT
        return
    fi
    if [ -n "${RAILWAY_TOKEN:-}" ]; then
        auth_header="Project-Access-Token: ${RAILWAY_TOKEN}"
    elif [ -n "${RAILWAY_API_TOKEN:-}" ]; then
        auth_header="Authorization: Bearer ${RAILWAY_API_TOKEN}"
    else
        say "no credential: set RAILWAY_TOKEN (project token) or RAILWAY_API_TOKEN."
        return 1
    fi
    curl -sS --fail-with-body -X POST "$ENDPOINT" \
        -H "$auth_header" -H 'Content-Type: application/json' --data-binary "$body"
}

# A GraphQL error is an HTTP 200 with an `errors` array, so the status alone proves nothing.
gql_checked() { # gql_checked <what> <query> <variables-json>
    local what="$1" out
    if ! out="$(gql "$2" "$3" 2>&1)"; then
        say "$what: the API call failed — $(printf '%s' "$out" | tr '\n' ' ' | cut -c1-300)"
        return 1
    fi
    if ! printf '%s' "$out" | jq -e . >/dev/null 2>&1; then
        say "$what: the API returned something that is not JSON — $(printf '%s' "$out" | tr '\n' ' ' | cut -c1-200)"
        return 1
    fi
    if printf '%s' "$out" | jq -e '(.errors // []) | length > 0' >/dev/null 2>&1; then
        say "$what: $(printf '%s' "$out" | jq -r '[.errors[].message] | join("; ")' | cut -c1-300)"
        return 1
    fi
    printf '%s' "$out"
}

SCOPE_QUERY='query { projectToken { projectId environmentId } }'
GRAPH_QUERY='query($pid: String!) { project(id: $pid) { name environments { edges { node { id name serviceInstances { edges { node { serviceId serviceName latestDeployment { id status } } } } } } } } }'
DEPLOYMENT_QUERY='query($id: String!) { deployment(id: $id) { id status deploymentStopped instances { status } } }'
LOGS_QUERY='query($id: String!, $limit: Int) { deploymentLogs(deploymentId: $id, limit: $limit) { message } }'

read_scope() {
    local out pid eid
    out="$(gql_checked "reading the token's scope" "$SCOPE_QUERY" '{}')" || return 1
    pid="$(printf '%s' "$out" | jq -r '.data.projectToken.projectId // empty')"
    eid="$(printf '%s' "$out" | jq -r '.data.projectToken.environmentId // empty')"
    if [ -z "$pid" ] || [ -z "$eid" ]; then
        say "the token did not resolve to a project and environment; a PROJECT token is required."
        return 1
    fi
    printf '%s %s' "$pid" "$eid"
}

read_services() { # read_services <project-id> <environment-id> -> [{serviceId, serviceName, deploymentId}]
    local out envnode
    out="$(gql_checked "reading the environment's service instances" "$GRAPH_QUERY" "$(jq -n --arg p "$1" '{pid: $p}')")" || return 1
    envnode="$(printf '%s' "$out" | jq -c --arg e "$2" '
        if (.data.project.environments.edges | type) == "array"
        then [.data.project.environments.edges[].node | select(.id == $e)] else null end')"
    if [ "$envnode" = "null" ] || [ "$(printf '%s' "$envnode" | jq -r 'length')" != "1" ] \
        || [ "$(printf '%s' "$envnode" | jq -r '.[0].serviceInstances.edges | type')" != "array" ]; then
        say "environment $2 is not in the response exactly once with a serviceInstances.edges array — the API shape changed."
        return 1
    fi
    printf '%s' "$envnode" | jq -c '[.[0].serviceInstances.edges[].node
        | {serviceId, serviceName, deploymentId: (.latestDeployment.id // null)}]'
}

# Echoes one line: `healthy`, `pending <why>`, `crashed <why>` or `undecidable <why>`. Log evidence
# is printed to stderr as it is found.
judge_deployment() { # judge_deployment <deployment-id> <service-name>
    local id="$1" name="$2" out dep status stopped n_inst bad_inst pending_inst unknown_inst logs msgs markers starts
    out="$(gql_checked "reading deployment $id ($name)" "$DEPLOYMENT_QUERY" "$(jq -n --arg i "$id" '{id: $i}')")" \
        || { echo "undecidable deployment $id could not be read"; return; }
    dep="$(printf '%s' "$out" | jq -c '.data.deployment // null')"
    if [ "$(printf '%s' "$dep" | jq -r '(.status | type) == "string" and (.deploymentStopped | type) == "boolean" and (.instances | type) == "array"')" != "true" ]; then
        echo "undecidable deployment $id carries no status/deploymentStopped/instances - the API shape changed"; return
    fi
    status="$(printf '%s' "$dep" | jq -r '.status')"
    stopped="$(printf '%s' "$dep" | jq -r '.deploymentStopped')"

    # Logs first: they are the only evidence the recorded crash loop left, so they are read on every
    # poll whatever the status says, and an unreadable log is never a pass.
    logs="$(gql_checked "reading the logs of deployment $id ($name)" "$LOGS_QUERY" \
        "$(jq -n --arg i "$id" --argjson l "$LOG_LIMIT" '{id: $i, limit: $l}')")" \
        || { echo "undecidable the logs of deployment $id could not be read"; return; }
    if [ "$(printf '%s' "$logs" | jq -r '(.data.deploymentLogs | type) == "array" and all(.data.deploymentLogs[]; (.message | type) == "string")')" != "true" ]; then
        echo "undecidable deploymentLogs for $id is not an array of messages - the API shape changed"; return
    fi
    msgs="$(printf '%s' "$logs" | jq -r '.data.deploymentLogs[].message')"
    markers="$(printf '%s\n' "$msgs" | grep -E "$CRASH_MARKERS" || true)"
    if [ -n "$markers" ]; then
        say "$name: $(printf '%s\n' "$markers" | grep -c .) crash marker line(s) in deployment $id's log, first five:"
        printf '%s\n' "$markers" | head -n 5 | sed 's/^/      /' >&2
        echo "crashed its log carries crash markers"; return
    fi
    starts="$(printf '%s\n' "$msgs" | grep -c '^Starting Container$' || true)"
    if [ "${starts:-0}" -gt 1 ]; then
        echo "crashed its container started ${starts} times"; return
    fi

    case "$status" in
        SUCCESS) ;;
        QUEUED|WAITING|INITIALIZING|BUILDING|DEPLOYING|NEEDS_APPROVAL) echo "pending deployment status $status"; return ;;
        CRASHED|FAILED|REMOVED|REMOVING|SKIPPED|SLEEPING) echo "crashed deployment status $status"; return ;;
        *) echo "undecidable deployment status '$status' is not one this check knows"; return ;;
    esac
    if [ "$stopped" = "true" ]; then
        echo "crashed deploymentStopped is true - every instance has stopped, though the status says SUCCESS"; return
    fi
    n_inst="$(printf '%s' "$dep" | jq -r '.instances | length')"
    bad_inst="$(printf '%s' "$dep" | jq -r '[.instances[].status | select(IN("CRASHED","EXITED","RESTARTING","STOPPED","REMOVED","REMOVING","SKIPPED"))] | join(",")')"
    pending_inst="$(printf '%s' "$dep" | jq -r '[.instances[].status | select(IN("CREATED","INITIALIZING"))] | length')"
    unknown_inst="$(printf '%s' "$dep" | jq -r '[.instances[].status | select(IN("RUNNING","CRASHED","EXITED","RESTARTING","STOPPED","REMOVED","REMOVING","SKIPPED","CREATED","INITIALIZING") | not)] | join(",")')"
    if [ -n "$bad_inst" ]; then echo "crashed instance status $bad_inst while the deployment says SUCCESS"; return; fi
    if [ -n "$unknown_inst" ]; then echo "undecidable instance status '$unknown_inst' is not one this check knows"; return; fi
    if [ "$n_inst" = "0" ]; then echo "pending no instance is reported yet"; return; fi
    if [ "$pending_inst" != "0" ]; then echo "pending an instance is still starting"; return; fi
    echo healthy
}

classify_plan() { # same contract as railway-deploy-identity.sh: only a proven `empty` excuses anything
    local file="$1"
    [ -f "$file" ] || { echo unreadable; return; }
    jq -e . "$file" >/dev/null 2>&1 || { echo unreadable; return; }
    [ "$(jq -r '.kind // empty' "$file")" = "railway.config.plan" ] || { echo unreadable; return; }
    [ "$(jq -r '.changeSet.changes | type' "$file")" = "array" ] || { echo unreadable; return; }
    if [ "$(jq -r '.changeSet.changes | length' "$file")" = "0" ]; then echo empty; else echo changes; fi
}

# --- arguments ----------------------------------------------------------------------------------
state=""; plan=""; settle=60; wait=300
while [ "$#" -gt 0 ]; do
    case "$1" in
        --state)  [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; state="$2";  shift 2 ;;
        --plan)   [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; plan="$2";   shift 2 ;;
        --settle) [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; settle="$2"; shift 2 ;;
        --wait)   [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; wait="$2";   shift 2 ;;
        -h|--help) usage; exit "$EX_OK" ;;
        *) say "unknown argument '$1'."; usage; exit "$EX_ENV" ;;
    esac
done
[ -n "$state" ] || { say "--state is required."; usage; exit "$EX_ENV"; }
for n in "$settle" "$wait"; do
    case "$n" in ''|*[!0-9]*) say "--settle and --wait take whole seconds, not '$n'."; exit "$EX_ENV" ;; esac
done

# --- the check ----------------------------------------------------------------------------------
[ -f "$state" ] || { red "UNDECIDABLE" "no state file at $state — railway-deploy-identity.sh captured nothing to compare against."; exit "$EX_UNDECIDABLE"; }
if [ "$(jq -r '.kind // empty' "$state" 2>/dev/null)" != "$STATE_KIND" ] \
    || [ "$(jq -r '.services | type' "$state" 2>/dev/null)" != "array" ]; then
    red "UNDECIDABLE" "$state is not a '$STATE_KIND' capture with a services array."
    exit "$EX_UNDECIDABLE"
fi
before="$(jq -c '.services | map({key: .serviceId, value: .deploymentId}) | from_entries' "$state")"

scope="$(read_scope)" || { red "UNDECIDABLE" "could not establish which environment this token is for."; exit "$EX_UNDECIDABLE"; }
pid="${scope%% *}"; eid="${scope##* }"
if [ "$eid" != "$(jq -r '.environmentId' "$state")" ]; then
    red "UNDECIDABLE" "this token is scoped to environment $eid; $state was captured in $(jq -r '.environmentId' "$state")."
    exit "$EX_UNDECIDABLE"
fi

start=$(date -u +%s)
deadline=$(( start + wait ))
# serviceId -> {serviceName, deploymentId, since (epoch seconds healthy since, or null), last}
tracked='{}'
ever_read=0
last_problem=""

while :; do
    now=$(date -u +%s)
    poll_ok=1
    if ! services="$(read_services "$pid" "$eid")"; then
        poll_ok=0; last_problem="the environment's service instances could not be read"
    else
        ever_read=1
        # Start watching every service whose latest deployment is not the one captured before.
        tracked="$(jq -c -n --argjson t "$tracked" --argjson b "$before" --argjson s "$services" '
            reduce ($s[] | select(.deploymentId != null and ($b[.serviceId] // null) != .deploymentId)) as $x
              ($t; if has($x.serviceId) then . else .[$x.serviceId] = {serviceName: $x.serviceName, deploymentId: $x.deploymentId, since: null, last: "not yet judged"} end)')"
        for sid in $(printf '%s' "$tracked" | jq -r 'keys[]'); do
            name="$(printf '%s' "$tracked" | jq -r --arg k "$sid" '.[$k].serviceName')"
            dep="$(printf '%s' "$tracked" | jq -r --arg k "$sid" '.[$k].deploymentId')"
            current="$(printf '%s' "$services" | jq -r --arg k "$sid" '[.[] | select(.serviceId == $k)][0].deploymentId // "<gone>"')"
            if [ "$current" != "$dep" ]; then
                red "UNDECIDABLE" "$name: deployment $dep is no longer the active one ($current replaced it), so whether it stayed up went unobserved."
                exit "$EX_UNDECIDABLE"
            fi
            verdict="$(judge_deployment "$dep" "$name")"
            case "$verdict" in
                healthy)
                    tracked="$(printf '%s' "$tracked" | jq -c --arg k "$sid" --argjson n "$now" '.[$k].since = (.[$k].since // $n) | .[$k].last = "healthy"')" ;;
                pending\ *)
                    tracked="$(printf '%s' "$tracked" | jq -c --arg k "$sid" --arg w "${verdict#pending }" '.[$k].since = null | .[$k].last = $w')" ;;
                crashed\ *)
                    red "CRASHED" "$name: deployment $dep is not staying up — ${verdict#crashed }."
                    printf '%s\n' "  Railway's own status is not the evidence here: the recorded crash loop sat at" >&2
                    printf '%s\n' "  SUCCESS for 78 minutes. Read that deployment's logs before trusting the apply." >&2
                    exit "$EX_CRASHED" ;;
                *)
                    poll_ok=0; last_problem="$name: ${verdict#undecidable }" ;;
            esac
        done
    fi

    if [ "$poll_ok" = "1" ] && [ "$(printf '%s' "$tracked" | jq -r 'length')" != "0" ] \
        && [ "$(printf '%s' "$tracked" | jq -r --argjson n "$now" --argjson s "$settle" 'all(.[]; .since != null and ($n - .since) >= $s)')" = "true" ] \
        && [ $(( now - start )) -ge "$settle" ]; then
        say "STAYED UP — $(printf '%s' "$tracked" | jq -r 'length') fresh deployment(s) healthy for ${settle}s:"
        printf '%s' "$tracked" | jq -r '.[] | "    \(.serviceName)  \(.deploymentId)"' >&2
        exit "$EX_OK"
    fi
    [ "$(date -u +%s)" -lt "$deadline" ] || break
    sleep "$POLL_SECONDS"
done

if [ "$ever_read" != "1" ]; then
    red "UNDECIDABLE" "the environment could not be read at all within ${wait}s — $last_problem."
    exit "$EX_UNDECIDABLE"
fi
if [ "$(printf '%s' "$tracked" | jq -r 'length')" = "0" ]; then
    if [ -n "$plan" ] && [ "$(classify_plan "$plan")" = "empty" ]; then
        say "NO-OP — no service moved, and $plan proves the plan was empty. Nothing to watch."
        exit "$EX_OK"
    fi
    red "UNDECIDABLE" "no service's deployment moved from $state, so there was nothing to watch - and no empty --plan says there should not have been."
    exit "$EX_UNDECIDABLE"
fi
red "UNDECIDABLE" "the settle window of ${settle}s was not reached within ${wait}s${last_problem:+ — last read problem: $last_problem}."
printf '%s' "$tracked" | jq -r '.[] | "    \(.serviceName)  \(.deploymentId)  \(.last)"' >&2
exit "$EX_UNDECIDABLE"
