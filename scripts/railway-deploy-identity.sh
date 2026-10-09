#!/usr/bin/env bash
# railway-deploy-identity.sh — prove a deploy SHIPPED rather than REPLAYED, by requiring that the
# environment produced a deployment id it did not have before.
#
#   scripts/railway-deploy-identity.sh capture <state-file>
#   scripts/railway-deploy-identity.sh assert  <state-file> [--plan FILE] [--wait SECONDS]
#   scripts/railway-deploy-identity.sh run [--state FILE] [--plan FILE] [--wait SECONDS] -- <argv...>
#
# `run` is the call site. It captures, runs the deploy, and asserts — so the two halves cannot be
# half-wired, which is the way a paired gate rots.
#
# WHY THIS EXISTS
#
# `railway redeploy` re-runs the PREVIOUS deployment's captured configuration. It does not re-read
# service config — `startCommand`, region, replicas. On 2026-09-18 `startCommand` was changed three
# times and three consecutive `railway redeploy` runs each reported SUCCESS while re-running the
# stale snapshot: the container started, logged one `Starting Container` line, and did nothing. Only
# a variable change, which creates a genuinely new deployment, picked the config up.
#
# THE FIRST HALF OF a separate change WAS ALREADY TRUE WHEN THIS WAS WRITTEN, and that is worth stating because
# the issue says "IF railway-apply deploys via redeploy". It does not, on either host. Both applies
# go through `railway config apply --plan`, which computes its change set from `.railway/railway.ts`.
# HOW AN APPLY USES IT — deploy-identity OUTSIDE and the snapshot guard INSIDE it:
#
#   railway-deploy-identity.sh run --plan railway-plan.json
#     -- railway-snapshot-guard.sh run --plan railway-plan.json
#          -- railway config apply --plan railway-plan.json --yes
#
# (production: the same nesting; the destructive guard refuses a destructive plan first). Where the apply
# is not a command line `run` could exec, bracket it instead: `capture` before, `assert` after.
#
# No job in this repository has ever called `railway redeploy`. So the fix is not to move off a path
# nothing uses; it is the SECOND half, which is the durable one: a deploy step that cannot tell
# "shipped" from "replayed" is the same defect class as a gate that passes without running, and the
# next regression of that shape is invisible again without an assertion. This script is that
# assertion, and it is deliberately indifferent to HOW the deploy was performed — it reads the
# environment before and after and asks whether anything new came out of it.
#
# WHAT IT READS, established by introspecting the live API on 2026-09-20 rather than assumed:
#
#   query { projectToken { projectId environmentId } }
#   query($pid: String!) { project(id: $pid) { environments { edges { node {
#           id serviceInstances { edges { node {
#             serviceId serviceName latestDeployment { id status } } } } } } } } }
#
# THREE THINGS ABOUT THAT QUERY ARE NOT OBVIOUS AND COST A REWRITE EACH:
#
#   1. `Service` HAS NO `serviceInstances` FIELD. The obvious traversal — project -> services ->
#      serviceInstances — does not exist in the schema. A `ServiceInstance` hangs off the
#      ENVIRONMENT, and carries `serviceId`, `serviceName` and `latestDeployment`. This is the same
#      project-level/environment-level split that `railway-snapshot-guard.sh` documents for volumes,
#      and it bites here for the same reason.
#   2. THE TRAVERSAL ROOT IS `project(id:)`, NOT `environment(id:)`, DELIBERATELY. It is the root
#      `railway-snapshot-guard.sh` already reaches under a CI project token in production, so it is
#      the one path here with live evidence behind it rather than a schema reading. The environment
#      is then selected by id from `projectToken`, exactly as that guard selects volume instances.
#   3. AUTH IS NOT ONE HEADER, for the same reason it is not there: a Railway PROJECT token — what
#      CI holds — authenticates with `Project-Access-Token`. A personal or team token uses
#      `Authorization: Bearer`. Sending a project token as a Bearer credential answers
#      `Not Authorized`, which reads like a permissions problem and is a header problem.
#
# FAIL CLOSED, AND IN PARTICULAR NEVER FAIL QUIET. Everything in this repository's CI fails
# PERMISSIVE when it breaks, and an assertion about a deploy is the worst place for that: it runs
# after a green apply, so "no complaint" reads as "shipped". So a pass is earned, never defaulted.
# It is UNDECIDABLE — red — when there is no credential, when the API cannot be read, when the
# response does not carry the shape above, when the state file is not one of ours, when the token's
# environment is not the one the state file was captured from, and when the deadline arrives having
# never completed a single read. "I could not tell" and "nothing shipped" are different answers and
# get different exit codes, but both are red.
#
# THE ONE PASS THAT IS NOT A MOVED DEPLOYMENT ID is an apply whose plan was provably empty, and it
# has to be proven, not inferred: `--plan` must name a `railway.config.plan` document whose
# `changeSet.changes` is an empty array. An apply with nothing to apply legitimately produces no new
# deployment, and without this branch every such pipeline would go red for being correct. Without
# `--plan` there is nothing to prove it with, so an unmoved environment is simply red.
#
# WHAT IT DOES NOT DO: decide which changes OUGHT to produce a deployment. Railway owns that opinion
# and a second one built here would drift out of step with it — the mistake `railway-destructive-guard.sh`
# records having made once already. A non-empty plan that genuinely redeploys nothing (a domain added,
# say) is therefore a FALSE RED, and that is the direction chosen on purpose: it goes red AFTER a
# successful apply, so nothing is broken and a human reads a message that prints the plan's own change
# list and says exactly this. The opposite error is the silent one this file exists to end.
#
# EXIT CODES
#   0   SHIPPED — at least one service's latest deployment id differs from the captured one.
#       Or NO-OP, proven by an empty `--plan`.
#   1   usage or environment error — bad arguments, no jq, no credential
#  10   REPLAYED — the deploy reported success and the environment produced no new deployment
#  11   UNDECIDABLE — nothing could be proven; treated exactly as REPLAYED by every caller
#
# PORTABILITY. Kept BusyBox-safe (the earlier CI jobs ran `alpine:3.21`), where `date -d` parses
# nothing and awk is not gawk. Nothing here needs either — `date -u +%s` and `+%Y-%m-%dT%H:%M:%SZ`
# are formatting, which BusyBox does fine, and the parsing is jq. Verify with
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c \
#     'apk add -q bash jq curl && bash scripts/railway-deploy-identity-selftest.sh'
#
# reference: DEPLOYMENT.md §9

set -uo pipefail

EX_OK=0
EX_ENV=1
EX_REPLAYED=10
EX_UNDECIDABLE=11

STATE_KIND="railway.deploy.identity"
ENDPOINT="${RAILWAY_DEPLOY_IDENTITY_ENDPOINT:-https://backboard.railway.com/graphql/v2}"
POLL_SECONDS="${RAILWAY_DEPLOY_IDENTITY_POLL:-5}"
DEFAULT_WAIT="${RAILWAY_DEPLOY_IDENTITY_WAIT:-180}"

say()  { printf '%s\n' "railway-deploy-identity: $*" >&2; }
red()  { printf '\033[31m%s\033[0m %s\n' "$1" "$2" >&2; }

usage() {
    cat >&2 <<'USAGE'
usage: railway-deploy-identity.sh capture <state-file>
       railway-deploy-identity.sh assert  <state-file> [--plan FILE] [--wait SECONDS]
       railway-deploy-identity.sh run [--state FILE] [--plan FILE] [--wait SECONDS] -- <argv...>

  capture   record every service instance's latest deployment id, before the deploy.
  assert    re-read them after the deploy and require that at least one moved.
  run       capture, run <argv...>, then assert. The call site: the pair cannot be half-wired.

  --plan FILE    the pinned `railway config plan --out` JSON. Its only use is to EXCUSE an
                 unmoved environment when the plan is provably empty.
  --wait N       seconds to keep re-reading before deciding nothing moved (default 180).
                 An apply returns while the redeploy is still rolling; the id appears first.
USAGE
}

command -v jq >/dev/null 2>&1 \
    || { red "CANNOT RUN" "jq is required and is not on PATH. The .railway CI jobs install it (apk add jq); a workstation must too."; exit "$EX_ENV"; }
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" \
    || { red "CANNOT RUN" "scripts/lib/jq-crlf.sh refused to load."; exit "$EX_ENV"; }

# --- transport ------------------------------------------------------------------------------------
# One seam, so the self-test can drive every branch below with no network and no Railway account.
# RAILWAY_DEPLOY_IDENTITY_TRANSPORT is a command that reads the GraphQL request body on stdin and
# writes the GraphQL response on stdout. Unset, it is curl against $ENDPOINT.
gql() { # gql <query> <variables-json>
    local body
    body="$(jq -n --arg q "$1" --argjson v "$2" '{query: $q, variables: $v}')"
    if [ -n "${RAILWAY_DEPLOY_IDENTITY_TRANSPORT:-}" ]; then
        printf '%s' "$body" | $RAILWAY_DEPLOY_IDENTITY_TRANSPORT
        return
    fi
    local auth_header
    if [ -n "${RAILWAY_TOKEN:-}" ]; then
        auth_header="Project-Access-Token: ${RAILWAY_TOKEN}"
    elif [ -n "${RAILWAY_API_TOKEN:-}" ]; then
        auth_header="Authorization: Bearer ${RAILWAY_API_TOKEN}"
    else
        say "no credential: set RAILWAY_TOKEN (project token) or RAILWAY_API_TOKEN (personal/team token)."
        return 1
    fi
    curl -sS --fail-with-body -X POST "$ENDPOINT" \
        -H "$auth_header" -H 'Content-Type: application/json' --data-binary "$body"
}

# Prints the response on success; on failure prints the reason to stderr and returns 1. A GraphQL
# error is an HTTP 200 carrying an `errors` array, so the status alone proves nothing and the result
# is captured into a variable rather than read out of a pipeline — a pipeline reports only its last
# command, and would report jq's opinion of an error document as success.
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
# Whole project, then select the environment by id — see note 2 in the header for why the root is
# `project` and not `environment`.
GRAPH_QUERY='query($pid: String!) { project(id: $pid) { name environments { edges { node { id name serviceInstances { edges { node { serviceId serviceName latestDeployment { id status } } } } } } } } }'

# Echoes `<projectId> <environmentId>`, or returns 1.
read_scope() {
    local out pid eid
    out="$(gql_checked "reading the token's scope" "$SCOPE_QUERY" '{}')" || return 1
    pid="$(printf '%s' "$out" | jq -r '.data.projectToken.projectId // empty')"
    eid="$(printf '%s' "$out" | jq -r '.data.projectToken.environmentId // empty')"
    if [ -z "$pid" ] || [ -z "$eid" ]; then
        say "the token did not resolve to a project and environment. A PROJECT token is required here;"
        say "a personal or team token has no single scope and cannot say which environment this is."
        return 1
    fi
    printf '%s %s' "$pid" "$eid"
}

# Echoes the service array for $2 within project $1 as compact JSON, or returns 1.
#
# THE SHAPE CHECK IS THE GUARD'S OWN GUARD. Every failure below would otherwise evaluate to "no
# services found", which `assert` would read as "nothing moved" — a wrong red — or, worse, an empty
# before-state would make every later comparison vacuous. Demand the shape and go red when it is
# absent, rather than inferring an environment from silence.
read_services() { # read_services <project-id> <environment-id>
    local out envnode
    out="$(gql_checked "reading the environment's service instances" "$GRAPH_QUERY" "$(jq -n --arg p "$1" '{pid: $p}')")" || return 1

    if [ "$(printf '%s' "$out" | jq -r 'if (.data.project.environments.edges | type) == "array" then "ok" else "no" end')" != "ok" ]; then
        say "project.environments.edges is missing or not an array — the API shape changed."
        return 1
    fi
    envnode="$(printf '%s' "$out" | jq -c --arg e "$2" '[.data.project.environments.edges[].node | select(.id == $e)]')"
    if [ "$(printf '%s' "$envnode" | jq -r 'length')" != "1" ]; then
        say "environment $2 resolved to $(printf '%s' "$envnode" | jq -r 'length') entries in this project, not 1."
        return 1
    fi
    if [ "$(printf '%s' "$envnode" | jq -r 'if (.[0].serviceInstances.edges | type) == "array" then "ok" else "no" end')" != "ok" ]; then
        say "environment $2 carries no serviceInstances.edges array — the API shape changed."
        return 1
    fi
    # `latestDeployment` is legitimately null for a service that has never deployed, so null is
    # carried through rather than rejected: null -> an id IS a service shipping for the first time.
    printf '%s' "$envnode" | jq -c '[.[0].serviceInstances.edges[].node
        | {serviceId, serviceName, deploymentId: (.latestDeployment.id // null), status: (.latestDeployment.status // null)}]
        | sort_by(.serviceName)'
}

# Prints the id map as one aligned line per service.
print_services() { # print_services <services-json> <label>
    printf '%s\n' "  $2:" >&2
    printf '%s' "$1" | jq -r '.[] | "    \(.serviceName)  \(.deploymentId // "<never deployed>")  \(.status // "-")"' >&2
}

# --- capture --------------------------------------------------------------------------------------

do_capture() { # do_capture <state-file>
    local state="$1" scope pid eid services
    [ -n "$state" ] || { usage; exit "$EX_ENV"; }

    scope="$(read_scope)" || { red "UNDECIDABLE" "could not establish which environment this token is for."; exit "$EX_UNDECIDABLE"; }
    pid="${scope%% *}"; eid="${scope##* }"
    services="$(read_services "$pid" "$eid")" || { red "UNDECIDABLE" "could not read the environment's deployments."; exit "$EX_UNDECIDABLE"; }

    jq -n --arg k "$STATE_KIND" --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
          --arg p "$pid" --arg e "$eid" --argjson s "$services" \
          '{kind: $k, version: 1, capturedAt: $at, projectId: $p, environmentId: $e, services: $s}' > "$state" \
        || { red "UNDECIDABLE" "could not write the state file $state."; exit "$EX_UNDECIDABLE"; }

    say "captured $(printf '%s' "$services" | jq -r 'length') service instance(s) in environment $eid -> $state"
    print_services "$services" "before"
}

# --- assert ---------------------------------------------------------------------------------------

# Echoes `empty` | `changes` | `unreadable` for a plan file. Only `empty` may excuse an unmoved
# environment, and it has to be positively proven — an unreadable plan excuses nothing.
classify_plan() { # classify_plan <plan-file>
    local file="$1"
    [ -f "$file" ] || { say "--plan $file does not exist."; echo unreadable; return; }
    if ! jq -e . "$file" >/dev/null 2>&1; then say "--plan $file is not valid JSON."; echo unreadable; return; fi
    if [ "$(jq -r '.kind // empty' "$file")" != "railway.config.plan" ]; then
        say "--plan $file does not name itself 'railway.config.plan' — this is not a pinned plan."
        echo unreadable; return
    fi
    if [ "$(jq -r 'if (.changeSet.changes | type) == "array" then "ok" else "no" end' "$file")" != "ok" ]; then
        say "--plan $file has no .changeSet.changes array — plan schema changed."
        echo unreadable; return
    fi
    if [ "$(jq -r '.changeSet.changes | length' "$file")" = "0" ]; then echo empty; else echo changes; fi
}

do_assert() { # do_assert <state-file> <plan-file-or-empty> <wait-seconds>
    local state="$1" plan="$2" wait="$3"
    local scope pid eid before after snapshot deadline read_ok=0 last_ok=0 moved plan_verdict

    [ -f "$state" ] || { red "UNDECIDABLE" "no state file at $state — nothing was captured before the deploy."; exit "$EX_UNDECIDABLE"; }
    if [ "$(jq -r '.kind // empty' "$state" 2>/dev/null)" != "$STATE_KIND" ]; then
        red "UNDECIDABLE" "$state does not name itself '$STATE_KIND' — this is not a capture of ours."
        exit "$EX_UNDECIDABLE"
    fi
    before="$(jq -c '.services' "$state")"
    if [ "$(printf '%s' "$before" | jq -r 'type')" != "array" ]; then
        red "UNDECIDABLE" "$state carries no services array."
        exit "$EX_UNDECIDABLE"
    fi

    scope="$(read_scope)" || { red "UNDECIDABLE" "could not establish which environment this token is for."; exit "$EX_UNDECIDABLE"; }
    pid="${scope%% *}"; eid="${scope##* }"
    # A comparison across two environments is not a weaker answer, it is a meaningless one.
    if [ "$eid" != "$(jq -r '.environmentId' "$state")" ]; then
        red "UNDECIDABLE" "this token is scoped to environment $eid; $state was captured in $(jq -r '.environmentId' "$state")."
        exit "$EX_UNDECIDABLE"
    fi

    deadline=$(( $(date -u +%s) + wait ))
    while :; do
        # READ INTO `snapshot`, NOT INTO `after`. A failed command substitution blanks the variable
        # it is assigned to, and `after` is what the verdict below is read from — so assigning it
        # directly meant one good read followed by failures produced `read_ok=1` with an empty
        # `after`, and the deadline branch then reported 10 (*nothing shipped*) over an environment
        # it had stopped being able to look at. The documented exit is 11 there, and 11 is the
        # honest answer: "I stopped being able to look" is not evidence that nothing shipped.
        # Keeping the last SUCCESSFUL read separate from whether the LAST read succeeded is what
        # makes the two distinguishable. A separate change review finding 2
        if snapshot="$(read_services "$pid" "$eid")"; then
            read_ok=1
            last_ok=1
            after="$snapshot"
            # A service present in `after` carrying a deployment id that is not the one `before`
            # recorded — including a service `before` had never seen, and including null -> an id.
            # `deploymentId != null` is required and not merely "different": a service whose latest
            # deployment went AWAY has changed, but it has not shipped anything, and this check
            # answers "did something ship", never "did something change".
            moved="$(jq -n --argjson b "$before" --argjson a "$after" -c '
                ($b | map({key: .serviceId, value: .deploymentId}) | from_entries) as $prev
                | [$a[] | select(.deploymentId != null and (($prev[.serviceId]) // null) != .deploymentId)]')"
            if [ "$(printf '%s' "$moved" | jq -r 'length')" != "0" ]; then
                say "SHIPPED — $(printf '%s' "$moved" | jq -r 'length') service(s) produced a new deployment:"
                printf '%s' "$moved" | jq -r '.[] | "    \(.serviceName)  \(.deploymentId // "<none>")  \(.status // "-")"' >&2
                exit "$EX_OK"
            fi
        else
            last_ok=0
        fi
        [ "$(date -u +%s)" -lt "$deadline" ] || break
        sleep "$POLL_SECONDS"
    done

    # The deadline arrived. Which of the two red answers it is depends on whether we ever managed to
    # look — "every read failed" is not evidence that nothing shipped.
    if [ "$read_ok" != "1" ]; then
        red "UNDECIDABLE" "the environment could not be read at all within ${wait}s, so whether anything shipped is unknown."
        exit "$EX_UNDECIDABLE"
    fi
    if [ "$last_ok" != "1" ]; then
        red "UNDECIDABLE" "the environment was readable at the start of the ${wait}s window and not at the end, so the rest of it went unobserved."
        printf '%s\n' "  A deploy that landed after the last successful read would look exactly like this." >&2
        printf '%s\n' "  Re-run the assertion against the same state file rather than reading it as a verdict." >&2
        exit "$EX_UNDECIDABLE"
    fi
    if [ "$(printf '%s' "$after" | jq -r 'length')" = "0" ] && [ "$(printf '%s' "$before" | jq -r 'length')" = "0" ]; then
        red "UNDECIDABLE" "the environment has no service instances before or after, so there is nothing this check could have observed."
        exit "$EX_UNDECIDABLE"
    fi

    if [ -n "$plan" ]; then
        plan_verdict="$(classify_plan "$plan")"
        if [ "$plan_verdict" = "empty" ]; then
            say "NO-OP — no deployment id moved, and $plan proves the plan was empty. Nothing was"
            say "supposed to ship, and nothing did."
            exit "$EX_OK"
        fi
    fi

    red "SHIPPED NOTHING" "no service's deployment id changed in ${wait}s. The deploy reported success and the environment is running what it was already running."
    print_services "$before" "before"
    print_services "$after"  "after"
    if [ -n "$plan" ] && [ "${plan_verdict:-}" = "changes" ]; then
        printf '%s\n' "  the plan that was applied asked for:" >&2
        jq -r '.changeSet.changes[] | "    \(.kind // "?")  \(.address // "?")  \(.summary // "")"' "$plan" >&2
        printf '%s\n' "  If every change above is one Railway does not redeploy for — a domain, a" >&2
        printf '%s\n' "  rename — then this red is wrong and the deploy was fine. That case is rare and" >&2
        printf '%s\n' "  deliberately not guessed at here: judging which changes ought to redeploy is" >&2
        printf '%s\n' "  Railway's opinion, and a second one kept in this script would drift out of step" >&2
        printf '%s\n' "  with it. Record the exception in DEPLOYMENT.md §7 and re-run." >&2
    elif [ -n "$plan" ]; then
        printf '%s\n' "  $plan could not be read, so it cannot excuse this either." >&2
    else
        printf '%s\n' "  No --plan was given, so an empty plan cannot be ruled out as the reason." >&2
    fi
    exit "$EX_REPLAYED"
}

# --- argument handling ------------------------------------------------------------------------------

[ "$#" -gt 0 ] || { usage; exit "$EX_ENV"; }
mode="$1"; shift

case "$mode" in
capture)
    [ "$#" -eq 1 ] || { usage; exit "$EX_ENV"; }
    do_capture "$1"
    ;;
assert)
    [ "$#" -ge 1 ] || { usage; exit "$EX_ENV"; }
    state="$1"; shift
    plan=""; wait="$DEFAULT_WAIT"
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --plan) [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; plan="$2"; shift 2 ;;
            --wait) [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; wait="$2"; shift 2 ;;
            *) say "unknown argument '$1'."; usage; exit "$EX_ENV" ;;
        esac
    done
    case "$wait" in ''|*[!0-9]*) say "--wait takes whole seconds, not '$wait'."; exit "$EX_ENV" ;; esac
    do_assert "$state" "$plan" "$wait"
    ;;
run)
    state=""; plan=""; wait="$DEFAULT_WAIT"
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --state) [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; state="$2"; shift 2 ;;
            --plan)  [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; plan="$2";  shift 2 ;;
            --wait)  [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; wait="$2";  shift 2 ;;
            --) shift; break ;;
            *) say "unknown argument '$1'."; usage; exit "$EX_ENV" ;;
        esac
    done
    [ "$#" -gt 0 ] || { say "run needs a command after --."; usage; exit "$EX_ENV"; }
    case "$wait" in ''|*[!0-9]*) say "--wait takes whole seconds, not '$wait'."; exit "$EX_ENV" ;; esac
    [ -n "$state" ] || state="railway-deploy-identity.json"

    do_capture "$state"
    say "running: $*"
    "$@"
    rc=$?
    if [ "$rc" != "0" ]; then
        # A failed deploy is not a replay, and asserting against it would only bury the real error
        # under a second one. Hand the deploy's own exit code back unchanged.
        say "the deploy command exited $rc — not asserting, because it did not report success."
        exit "$rc"
    fi
    do_assert "$state" "$plan" "$wait"
    ;;
-h|--help|help)
    usage; exit "$EX_OK"
    ;;
*)
    say "unknown mode '$mode'."
    usage; exit "$EX_ENV"
    ;;
esac
