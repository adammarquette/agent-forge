#!/usr/bin/env bash
# railway-deploy-identity-selftest.sh — make scripts/railway-deploy-identity.sh go RED for the
# reason it exists, and keep it going red.
#
# That script runs AFTER a green apply, which is the worst possible place for a check that fails
# permissive: with nothing to say, silence reads as "shipped". Every way it can break — an API it
# cannot reach, a response whose shape moved, a state file from another environment, a plan it
# cannot parse — is a way of finding no difference and reporting none. So each of those is asserted
# here to be RED, and each for its own exit code, because "I could not tell" (11) and "nothing
# shipped" (10) are different answers and the runbook sends the reader somewhere different for each.
#
# IT NEEDS NO NETWORK, NO RAILWAY ACCOUNT AND NO TOKEN. The subject talks to the Railway API through
# one seam — RAILWAY_DEPLOY_IDENTITY_TRANSPORT, a command that reads the GraphQL request body on
# stdin and writes the response on stdout — and this file stubs it with a script that answers from
# two files on disk. Rewriting the graph file between `capture` and `assert` is what "a deploy
# happened" means here.
#
# Run it:  bash scripts/railway-deploy-identity-selftest.sh
# Under the image CI actually uses:
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c \
#     'apk add -q bash jq && bash scripts/railway-deploy-identity-selftest.sh'
#
# DEPLOYMENT.md §9

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/railway-deploy-identity.sh"
GUARD="$HERE/railway-snapshot-guard.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fails=0
asserts=0

# CASE 0, AND IT COMES FIRST ON PURPOSE. EX_ENV is 1, which is also what bash exits with when it
# cannot parse a file at all — so a syntax error anywhere in the subject would let every usage case
# below "pass" while the script was not a program. Establish that it parses before asking what it
# says. The same collision cost this repository a whole green self-test once already.
# `railway-snapshot-guard.sh` is checked too:
# the composed-nesting case below runs it for real, not as a fixture.
if ! bash -n "$GATE" 2>"$tmp/parse.err"; then
    printf '\033[31mSELF-TEST FAILED\033[0m  %s is not parseable — nothing below was a real test\n' "$GATE" >&2
    sed 's/^/      /' "$tmp/parse.err" >&2
    exit 1
fi
if ! bash -n "$GUARD" 2>"$tmp/parse-guard.err"; then
    printf '\033[31mSELF-TEST FAILED\033[0m  %s is not parseable — the composed-nesting case below was not a real test\n' "$GUARD" >&2
    sed 's/^/      /' "$tmp/parse-guard.err" >&2
    exit 1
fi

# --- the stubbed transport --------------------------------------------------------------------
# Answers the scope query from $tmp/scope.json and the graph query from $tmp/graph.json. Which
# query it is, is decided by the body on stdin. A file containing the single word ERROR stands for
# a transport that fails outright, so the `gql` failure branch is reachable too.
cat >"$tmp/stub.sh" <<'STUB'
body="$(cat)"
case "$body" in
    *projectToken*) f="$TMPD/scope.json" ;;
    *)              f="$TMPD/graph.json" ;;
esac
[ -f "$f" ] || { echo "stub: no $f" >&2; exit 1; }
if [ "$(cat "$f")" = "ERROR" ]; then echo "stub: transport failure" >&2; exit 1; fi
cat "$f"
# One-shot: answer this graph query and then start failing, so a read that succeeds and later
# stops succeeding is reachable. $TMPD/graph-breaks-after-first arms it.
if [ "$f" = "$TMPD/graph.json" ] && [ -f "$TMPD/graph-breaks-after-first" ]; then
    rm -f "$TMPD/graph-breaks-after-first"
    echo ERROR >"$TMPD/graph.json"
fi
STUB

export TMPD="$tmp"
export RAILWAY_DEPLOY_IDENTITY_TRANSPORT="bash $tmp/stub.sh"
export RAILWAY_DEPLOY_IDENTITY_POLL=1

ENV_A="43e5ee34-ad72-4dbf-81ac-b52bd138dae4"
ENV_B="0e2d543a-5776-46af-992e-5f60e91aa726"
PROJ="8f007871-c6ee-483a-ba93-1d65673dee6f"

scope() { # scope <environment-id>
    jq -n --arg p "$PROJ" --arg e "$1" \
        '{data: {projectToken: {projectId: $p, environmentId: $e}}}' >"$tmp/scope.json"
}

# graph <environment-id> <service-instance-json-array>
# The array is written in the API's own nesting so that a shape change in the subject's jq path is
# caught here rather than in production: edges -> node -> latestDeployment.
graph() {
    jq -n --arg e "$1" --argjson s "$2" '{
        data: { project: { name: "fearless-abundance", environments: { edges: [
            { node: { id: $e, name: "stub", serviceInstances: { edges: [ $s[] | { node: . } ] } } }
        ] } } }
    }' >"$tmp/graph.json"
}

si() { # si <serviceId> <serviceName> <deploymentId-or-empty> -> one serviceInstance node
    if [ -z "$3" ]; then
        jq -nc --arg i "$1" --arg n "$2" '{serviceId: $i, serviceName: $n, latestDeployment: null}'
    else
        jq -nc --arg i "$1" --arg n "$2" --arg d "$3" \
            '{serviceId: $i, serviceName: $n, latestDeployment: {id: $d, status: "SUCCESS"}}'
    fi
}

# --- plan fixtures ------------------------------------------------------------------------------
# Trimmed from the real `railway config plan --out` capture the destructive-guard self-test uses,
# so the shape read here is the shape Railway writes.
cat >"$tmp/plan-empty.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "changes": [] } }
EOF
cat >"$tmp/plan-changes.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "changes": [
  { "address": "service.agent-forge-api", "kind": "resource.update", "severity": "safe", "summary": "Update agent-forge-api config.startCommand" }
] } }
EOF
cat >"$tmp/plan-truncated.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destr
EOF
# Names itself, parses, and has no changes array at all: the schema-drift case, which must NOT be
# read as "no changes" and must therefore never excuse an unmoved environment.
cat >"$tmp/plan-schema-drift.json" <<'EOF'
{ "kind": "railway.config.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "diffs": [] } }
EOF
# PARSES, AND CARRIES AN EMPTY CHANGE LIST, AND IS NOT A RAILWAY PLAN. This is the fixture that
# makes the `kind` check load-bearing: without it, deleting that check leaves this document
# classified `empty`, which EXCUSES an unmoved environment — a green deploy proven by a file from
# some other tool. It was the one mutant that survived review. A separate change finding 3
cat >"$tmp/plan-foreign-kind.json" <<'EOF'
{ "kind": "terraform.plan", "version": 1, "destructive": false, "changeSet": { "version": 1, "changes": [] } }
EOF

# --- harness -------------------------------------------------------------------------------------

# COUNTED, BECAUSE A SUITE THAT NEVER REACHED ITS CASES PRINTS NO FAILURE LINES EITHER — see
# `railway-snapshot-guard-selftest.sh`, which this mirrors. `asserts` is checked against
# EXPECTED_ASSERTIONS at the bottom, so a case that stops firing (or one added without updating the
# constant) is red rather than silently green.
pass() { asserts=$((asserts + 1)); }
fail() { # fail <what> <detail...>
    asserts=$((asserts + 1))
    fails=$((fails + 1))
    printf '\033[31mSELF-TEST FAILED\033[0m  %s\n' "$1" >&2
    shift
    [ "$#" -gt 0 ] && printf '%s\n' "$*" | sed 's/^/      /' >&2
    return 0
}

expect() { # expect <exit> <description> -- <gate args...>
    local want="$1" what="$2"; shift 2
    [ "${1:-}" = "--" ] && shift
    local out got
    out="$(bash "$GATE" "$@" 2>&1)"; got=$?
    if [ "$got" -ne "$want" ]; then
        fail "$what — expected exit $want, got $got" "$out"
        return
    fi
    pass
}

# The subject's own message has to name the thing, or the exit code is the only diagnosis anyone
# gets from a job log. Asserting the code alone would let every message rot into the same sentence.
expect_saying() { # expect_saying <exit> <substring> <description> -- <gate args...>
    local want="$1" needle="$2" what="$3"; shift 3
    [ "${1:-}" = "--" ] && shift
    local out got
    out="$(bash "$GATE" "$@" 2>&1)"; got=$?
    if [ "$got" -ne "$want" ]; then
        fail "$what — expected exit $want, got $got" "$out"
        return
    fi
    case "$out" in
        *"$needle"*) pass ;;
        *) fail "$what — exit $want was right but the message never said \"$needle\"" "$out" ;;
    esac
}

# expect_cmd_says <exit> <substring> <description> -- <argv...> — like expect_saying, but for a
# command line whose subject is not `bash "$GATE" ...`. Used below for the composed-with-the-guard
# case, where the program under test is `railway-snapshot-guard.sh`.
expect_cmd_says() {
    local want="$1" needle="$2" what="$3"; shift 3
    [ "${1:-}" = "--" ] && shift
    local out got
    out="$("$@" 2>&1)"; got=$?
    if [ "$got" -ne "$want" ]; then
        fail "$what — expected exit $want, got $got" "$out"
        return
    fi
    case "$out" in
        *"$needle"*) pass ;;
        *) fail "$what — exit $want was right but the message never said \"$needle\"" "$out" ;;
    esac
}

# capture_now <state-file> — a capture that is expected to succeed, for use as a fixture.
capture_now() {
    if ! bash "$GATE" capture "$1" >/dev/null 2>&1; then
        fail "fixture capture into $1 failed"
    fi
}

A_BEFORE="$(si svc-api agent-forge-api dep-1111)"
A_AFTER="$(si svc-api agent-forge-api dep-2222)"
PROXY="$(si svc-proxy reverse-proxy dep-9999)"

# =================================================================================================
# GREEN — the three ways something genuinely shipped, and the one no-op that is provably fine.
# =================================================================================================

scope "$ENV_A"
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
capture_now "$tmp/s1.json"
graph "$ENV_A" "[$A_AFTER, $PROXY]"
expect_saying 0 "SHIPPED" "a service's deployment id moved" -- assert "$tmp/s1.json" --wait 0

# A service that had never deployed and now has: null -> an id is shipping, not noise.
graph "$ENV_A" "[$(si svc-api agent-forge-api ''), $PROXY]"
capture_now "$tmp/s2.json"
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
expect 0 "a never-deployed service produced its first deployment" -- assert "$tmp/s2.json" --wait 0

# A service the capture had never seen at all, arriving with a deployment.
graph "$ENV_A" "[$A_BEFORE]"
capture_now "$tmp/s3.json"
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
expect 0 "a newly created service produced a deployment" -- assert "$tmp/s3.json" --wait 0

# The only pass that is not a moved deployment id, and it is PROVEN, not inferred.
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
capture_now "$tmp/s4.json"
expect_saying 0 "NO-OP" "an empty plan excuses an unmoved environment" \
    -- assert "$tmp/s4.json" --wait 0 --plan "$tmp/plan-empty.json"

# =================================================================================================
# RED, EXIT 10 — the environment is running what it was already running. The defect itself.
# =================================================================================================

graph "$ENV_A" "[$A_BEFORE, $PROXY]"
capture_now "$tmp/r1.json"
expect_saying 10 "SHIPPED NOTHING" "nothing moved and no plan was offered" -- assert "$tmp/r1.json" --wait 0

expect_saying 10 "startCommand" "nothing moved while the plan asked for a change — and the message prints the change" \
    -- assert "$tmp/r1.json" --wait 0 --plan "$tmp/plan-changes.json"

# AN UNREADABLE PLAN EXCUSES NOTHING. This is the permissive direction: a plan that cannot be parsed
# has no changes the script can see, and "no changes seen" must never become "the plan was empty".
expect 10 "a truncated plan does not excuse an unmoved environment" \
    -- assert "$tmp/r1.json" --wait 0 --plan "$tmp/plan-truncated.json"
expect 10 "a plan whose changes array was renamed does not excuse an unmoved environment" \
    -- assert "$tmp/r1.json" --wait 0 --plan "$tmp/plan-schema-drift.json"
expect 10 "a --plan naming a file that does not exist does not excuse an unmoved environment" \
    -- assert "$tmp/r1.json" --wait 0 --plan "$tmp/nope.json"
# The empty change list is REAL here; only the document's identity is wrong. Every other fixture
# above fails to parse or fails the shape check, so this is the only one that reaches the `kind`
# test with something it would otherwise call `empty`.
expect 10 "a plan from some other tool does not excuse an unmoved environment, empty change list and all" \
    -- assert "$tmp/r1.json" --wait 0 --plan "$tmp/plan-foreign-kind.json"

# A service whose latest deployment went AWAY has changed and has not shipped. "Different" is not
# the question this script answers.
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
capture_now "$tmp/r2.json"
graph "$ENV_A" "[$(si svc-api agent-forge-api ''), $PROXY]"
expect 10 "a deployment disappearing is not a deployment shipping" -- assert "$tmp/r2.json" --wait 0

# =================================================================================================
# RED, EXIT 11 — could not tell. Every one of these would otherwise find no difference and report
# none, which is the exact permissive failure this file exists against.
# =================================================================================================

expect_saying 11 "no state file" "asserting with nothing captured beforehand" -- assert "$tmp/absent.json" --wait 0

echo '{"kind":"something.else","services":[]}' >"$tmp/foreign.json"
expect_saying 11 "does not name itself" "a state file that is not one of ours" -- assert "$tmp/foreign.json" --wait 0

jq -n '{kind: "railway.deploy.identity", version: 1, environmentId: "e", services: "not-an-array"}' >"$tmp/bent.json"
expect_saying 11 "no services array" "a state file whose services are not a list" -- assert "$tmp/bent.json" --wait 0

# THE CROSS-ENVIRONMENT COMPARISON. Not a weaker answer — a meaningless one, and it would usually
# read as "nothing moved", i.e. a red for the wrong reason about the wrong environment.
graph "$ENV_A" "[$A_BEFORE]"
capture_now "$tmp/u1.json"
scope "$ENV_B"
expect_saying 11 "scoped to environment" "the token is for a different environment than the capture" \
    -- assert "$tmp/u1.json" --wait 0
scope "$ENV_A"

# The API shape moved: `serviceInstances.edges` renamed. Finding no services must not read as
# finding no changes.
jq -n --arg e "$ENV_A" '{data: {project: {name: "p", environments: {edges: [
    {node: {id: $e, name: "stub", serviceInstances: {nodes: []}}}]}}}}' >"$tmp/graph.json"
expect_saying 11 "shape changed" "serviceInstances.edges renamed under the subject" -- assert "$tmp/u1.json" --wait 0

jq -n '{data: {project: {name: "p", environments: {}}}}' >"$tmp/graph.json"
expect_saying 11 "shape changed" "project.environments.edges missing" -- assert "$tmp/u1.json" --wait 0

# The token's environment is not in the project graph at all.
graph "$ENV_B" "[$A_BEFORE]"
expect_saying 11 "resolved to 0 entries" "the environment is absent from the project graph" -- assert "$tmp/u1.json" --wait 0

# Every read failed. NOT evidence that nothing shipped — it is evidence of nothing at all.
echo ERROR >"$tmp/graph.json"
expect_saying 11 "could not be read at all" "the API was unreachable for the whole wait" -- assert "$tmp/u1.json" --wait 0

# AND THE HALF-OBSERVED WINDOW, which is the one that used to come back 10. One good read showing
# no movement, then the API stops answering: a deploy landing after that last good read looks
# exactly like a deploy that never happened, so the answer is 11. `--wait 2` with a 1s poll is what
# buys a second and third read; the stub is armed to fail after the first.
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
capture_now "$tmp/u3.json"
touch "$tmp/graph-breaks-after-first"
expect_saying 11 "went unobserved" "the environment became unreadable partway through the wait" \
    -- assert "$tmp/u3.json" --wait 2
rm -f "$tmp/graph-breaks-after-first"

# A GraphQL error arrives as HTTP 200 with an `errors` array, so status alone proves nothing. No
# `.data` at all here, so this one is turned away by read_services' OWN shape check rather than by
# gql_checked's errors check — a different branch, kept for that reason (see the next case).
jq -n '{errors: [{message: "Not Authorized"}]}' >"$tmp/graph.json"
expect 11 "a GraphQL errors array with no data is a failure, not an empty result" -- assert "$tmp/u1.json" --wait 0

# THE SAME CLAIM, BUT SHAPE-VALID AT EVERY LAYER ABOVE gql_checked — built from the same graph()
# helper every green case uses, with an `errors` array layered on top (a sub-field error resolving
# latestDeployment, same shape a real partial GraphQL response has). Without this fixture, deleting
# the errors branch from gql_checked() left the WHOLE self-test green: the no-`.data` case above is
# turned away by read_services' shape check regardless of what gql_checked does, so it cannot catch
# that mutant. Separate changes note 71397 finding 1
graph "$ENV_A" "[$A_BEFORE]"
jq '. + {errors: [{message: "Something failed resolving latestDeployment"}]}' "$tmp/graph.json" >"$tmp/graph-errors.json"
mv "$tmp/graph-errors.json" "$tmp/graph.json"
expect_saying 11 "Something failed resolving latestDeployment" \
    "a GraphQL errors array is a failure even with a shape-valid data payload" -- assert "$tmp/u1.json" --wait 0

# Not JSON at all — an HTML error page from a proxy is the usual shape of this.
printf '<html>502</html>' >"$tmp/graph.json"
expect 11 "a non-JSON response is a failure, not an empty result" -- assert "$tmp/u1.json" --wait 0

# An environment with no service instances before OR after: nothing this check could have observed,
# so it must not report a clean bill of health.
graph "$ENV_A" "[]"
capture_now "$tmp/u2.json"
expect_saying 11 "nothing this check could have observed" "an environment with no service instances" -- assert "$tmp/u2.json" --wait 0

# Capture's own failure paths.
scope "$ENV_A"
echo ERROR >"$tmp/graph.json"
expect 11 "capture cannot read the environment" -- capture "$tmp/never.json"
jq -n '{data: {projectToken: null}}' >"$tmp/scope.json"
expect_saying 11 "PROJECT token" "capture with a credential that has no single environment scope" -- capture "$tmp/never.json"
scope "$ENV_A"

# =================================================================================================
# RED, EXIT 1 — usage. Kept distinct from 11 so a typo in a job never looks like a deploy verdict.
# =================================================================================================

expect 1 "an unknown mode"            -- frobnicate
expect 1 "assert with no state file"  -- assert
expect 1 "capture with no state file" -- capture
expect 1 "a non-numeric --wait"       -- assert "$tmp/s1.json" --wait soon
expect 1 "an unknown flag"            -- assert "$tmp/s1.json" --verbose
expect 1 "run with no command after --" -- run --state "$tmp/x.json" --
expect 1 "run with no -- at all"      -- run --state "$tmp/x.json"

# =================================================================================================
# RUN — the call site. The two halves are paired here so that a job cannot wire only one of them,
# which means `run` has its own failure modes and they are the important ones.
# =================================================================================================

# The deploy ships: the guarded command swaps in the graph an apply would have produced. Built here
# and merely COPIED by the stub — a stub that rebuilt it would put JSON through a heredoc, and what
# survives that is a test of quoting rather than of the subject.
graph "$ENV_A" "[$A_AFTER, $PROXY]"
cp "$tmp/graph.json" "$tmp/graph-after.json"
cat >"$tmp/deploy-ok.sh" <<STUB
touch "$tmp/ran-ok"
cp "$tmp/graph-after.json" "$tmp/graph.json"
STUB
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
rm -f "$tmp/ran-ok"
expect_saying 0 "SHIPPED" "run: capture, deploy, assert" \
    -- run --state "$tmp/run1.json" --wait 0 -- bash "$tmp/deploy-ok.sh"
if [ -f "$tmp/ran-ok" ]; then pass; else fail "run: the guarded command never executed"; fi

# The deploy does nothing at all — the defect, reproduced end to end through `run`.
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
expect_saying 10 "SHIPPED NOTHING" "run: a deploy that reports success and changes nothing" \
    -- run --state "$tmp/run2.json" --wait 0 -- true

# A FAILED DEPLOY IS NOT A REPLAY. Its own exit code has to survive, or the real error is buried
# under a second one that points at the wrong thing.
graph "$ENV_A" "[$A_BEFORE, $PROXY]"
expect 3 "run: the deploy's own exit code survives" \
    -- run --state "$tmp/run3.json" --wait 0 -- sh -c 'exit 3'

# ASSERTED TWICE, as the snapshot guard's self-test is: a capture that cannot be taken must stop the
# run AND leave the deploy unrun, because a run with no `before` can never answer the question and
# would otherwise proceed to a vacuous green.
echo ERROR >"$tmp/graph.json"
rm -f "$tmp/ran-ok"
expect 11 "run: an unreadable environment stops the run" \
    -- run --state "$tmp/run4.json" --wait 0 -- bash "$tmp/deploy-ok.sh"
if [ -f "$tmp/ran-ok" ]; then fail "run: the deploy ran although the capture failed"; else pass; fi

# =================================================================================================
# THE COMPOSED NESTING — a separate change finding 1. The staging and production apply
# compose the apply as deploy-identity OUTSIDE, snapshot-guard INSIDE.
# The reverse is not a style preference: `railway-snapshot-guard.sh run` execs its argv VERBATIM
# and refuses when it finds more than one `--plan` on that line, before any canonicalisation — so
# two spellings of the same file do not save it (`scripts/railway-snapshot-guard.sh`,
# `require_plan_identity`). Put outermost, the guard sees BOTH the inner `railway-deploy-identity.sh
# run`'s own `--plan` flag and the guarded apply's `--plan` flag on the one exec'd command line, and
# refuses every deploy — with a message about `--plan` that names nothing wrong with the change that
# triggered it. This runs the real `railway-snapshot-guard.sh` against the real
# `railway-deploy-identity.sh`, in the inverted order, and asserts the refusal AND its reason —
# demonstrated, not merely read off the two files. No credential or network reaches this:
# `require_plan_identity` runs before `resolve_scope`.
nest_plan="$tmp/plan-changes.json"
expect_cmd_says 2 "Name the plan once." \
    "the inverted nesting (snapshot-guard OUTSIDE, deploy-identity INSIDE) refuses every deploy" \
    -- bash "$GUARD" run --plan "$nest_plan" --label gl659-nesting-control \
         -- bash "$GATE" run --state "$tmp/nest.json" --plan "$nest_plan" \
              -- true --plan "$nest_plan" --yes

# =================================================================================================

EXPECTED_ASSERTIONS=40
if [ "$asserts" -ne "$EXPECTED_ASSERTIONS" ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  ran %s assertion(s), expected %s — a case stopped firing, or one was added without updating EXPECTED_ASSERTIONS.\n' \
        "$asserts" "$EXPECTED_ASSERTIONS" >&2
    exit 1
fi
if [ "$fails" -ne 0 ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  %s of %s assertion(s)\n' "$fails" "$asserts" >&2
    exit 1
fi
printf '\033[32mSELF-TEST PASSED\033[0m  %s of %s assertions — railway-deploy-identity-selftest: ok\n' \
    "$asserts" "$asserts"
