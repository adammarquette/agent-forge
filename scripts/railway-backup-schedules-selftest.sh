#!/usr/bin/env bash
# railway-backup-schedules-selftest.sh — prove the schedule setter still refuses, still sets, and still
# tells the two apart.
#
# THE SUBJECT'S WHOLE VALUE IS THAT IT DOES NOT LIE ABOUT STATE IT DID NOT ESTABLISH. Every gate in this
# repository fails PERMISSIVE when it breaks, and this one has three specific ways to do that:
#
#   * `volumeInstanceBackupScheduleUpdate` returns a BOOLEAN. A version that stops re-reading the list
#     and trusts `true` reports "verified DAILY schedule" for work that produced nothing. That is the
#     headline case, and it is the same separation the snapshot guard draws between `workflowStatus`
#     saying Complete and a labelled backup actually existing.
#   * `kinds` REPLACES the whole set rather than adding to it. A version that sends ["DAILY"] over a
#     volume that carried WEEKLY silently deletes the weekly schedule — a backup regression with a
#     green log. The union case below asserts on the REQUEST BODY, because nothing in the response
#     distinguishes the two.
#   * `check` must report a schedule list it could not READ as wrong, never as the intended state. An
#     unreadable list is exactly the state the 2026-09-18 incident was in.
#
# AND SINCE a separate change THERE IS A FOURTH, WHICH IS THE ONE A READER WILL NOT EXPECT. The expected state is
# no longer "DAILY everywhere": it comes from `scripts/railway-data-refreshable.json`, and where that
# document's `schedules` field says `"none"` it is "no schedule at all". A check whose expectation is
# "nothing" and whose every other answer is a shrug can never go red — so the cases below assert BOTH
# directions, including a DAILY that reappeared where none belongs, which must be reported as drift. The
# `remove` verb is gated the other way: it refuses unless `schedules` positively says `"none"`.
#
# AND A FIFTH, WHICH THIS SUITE PREVIOUSLY ASSERTED BACKWARDS — the failure this project keeps paying
# for, a guard agreeing with the bug. The subject read ONE field, `refreshable`, for TWO independent
# questions: may the snapshot guard skip its backup, and should this environment carry schedules.
# Production answered them differently — not refreshable, and no schedules — so the single boolean made
# `check` demand a DAILY schedule on every production volume and point the operator at the `apply` that
# re-creates what the maintainer's ruling retired. Every case here encoded that as intended, so nothing
# reddened. The PRODUCTION-SHAPED block below is what would have caught it, and the pair that matters is
# `refreshable: false` WITH `schedules: "none"`: compliant-with-no-schedules, and still DRIFT if a
# schedule turns up.
#
# EVERY RED CASE ASSERTS ON THE REASON, not only on the exit code. Exit 2 is this script's answer to
# "no credential", "unreadable graph", "unknown volume name", "the mutation failed" and more, so a case
# asserting only on the code passes when it refused for a reason the case was not written about —
# indistinguishable from working.
#
# AND THE SUITE STATES HOW MANY ASSERTIONS IT RAN. A parser that stops matching, a fixture path that
# moves, a `case` that stops firing — all of them turn a suite into one that reaches no cases and
# prints no failure lines, which is the shape of both a pass and a no-op. The count at the bottom is
# checked against EXPECTED_ASSERTIONS, so "0 of 0" cannot read as a pass.
#
# No network and no Railway account: the subject's transport is a seam
# (RAILWAY_BACKUP_SCHEDULES_TRANSPORT) and this file drives it with canned GraphQL documents.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SUBJECT="$HERE/railway-backup-schedules.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

command -v jq >/dev/null 2>&1 || { echo "SELF-TEST CANNOT RUN: jq is required" >&2; exit 1; }

# IS THE SUBJECT EVEN A PROGRAM? This runs before case 1 because the suite cannot tell otherwise: bash's
# own exit code for a SYNTAX ERROR is 2, and 2 is also this script's "refused". A subject that does not
# parse would therefore PASS every refusal case, and those are most of them.
if ! bash -n "$SUBJECT" 2>"$tmp/syntax.err"; then
  echo "SELF-TEST CANNOT RUN: $SUBJECT is not valid shell — every refusal case below would pass for the" >&2
  echo "wrong reason, because a bash syntax error and this script's 'refused' are both exit 2." >&2
  cat "$tmp/syntax.err" >&2
  exit 1
fi
# AND SO IS THE FILE IT SOURCES. `bash -n` on the subject does not parse what the subject `.`s at run
# time, so a syntax error in the shared declaration reader would reach the cases as an exit 2 that every
# refusal case accepts — the same aliasing the check above exists for, one file further out.
if ! bash -n "$HERE/railway-data-refreshable.sh" 2>"$tmp/syntax.err"; then
  echo "SELF-TEST CANNOT RUN: railway-data-refreshable.sh, which the subject sources, is not valid shell." >&2
  cat "$tmp/syntax.err" >&2
  exit 1
fi

FIX="$tmp/fix"
REQUESTS="$tmp/requests.log"

# --- the stub Railway API -------------------------------------------------------------------------
# Reads a GraphQL request body on stdin, logs it, and answers from $FIX. The schedule LIST is answered
# from schedules-1.json until an update has been sent and from schedules-2.json afterwards, so "before"
# and "after" can differ — or, in the headline case, deliberately not.
cat > "$tmp/transport.sh" <<'STUB'
#!/usr/bin/env bash
set -eu
body="$(cat)"
# LOGGED COMPACT, ON PURPOSE. `jq -n` pretty-prints, so the raw body spreads `kinds` over three lines
# and a request assertion written the way anyone would write it — '"kinds":["DAILY"]' — matches
# nothing and passes every "must NOT contain" case for free. That is a self-test that cannot fail.
printf '%s' "$body" | jq -c . >> "$REQUESTS" 2>/dev/null || printf '%s\n' "$body" >> "$REQUESTS"
# PER INSTANCE, NOT GLOBAL. The before/after switch has to follow the volume instance the request names:
# with one global marker, instance 2's PRE-read is answered with instance 1's POST fixture, so the
# suite's own baseline reports "already DAILY" for a volume nothing touched.
vi="$(printf '%s' "$body" | jq -r '.variables.id // empty' 2>/dev/null || true)"
case "$body" in
  *projectToken*)                            f="projectToken.json" ;;
  *volumeInstanceBackupScheduleUpdate*)      : > "$FIX/.updated-$vi"; f="update.json" ;;
  *volumeInstanceBackupScheduleList*)
      if [ -f "$FIX/.updated-$vi" ] && [ -f "$FIX/schedules-2.json" ]; then f="schedules-2.json"
      else f="schedules-1.json"; fi ;;
  *volumeInstances*)                         f="volumes.json" ;;
  *) echo '{"errors":[{"message":"stub: unrecognised query"}]}'; exit 0 ;;
esac
cat "$FIX/$f"
STUB
chmod +x "$tmp/transport.sh"

export FIX REQUESTS
export RAILWAY_BACKUP_SCHEDULES_TRANSPORT="bash $tmp/transport.sh"
export RAILWAY_TOKEN="stub-project-token"
unset RAILWAY_ENVIRONMENT_ID RAILWAY_API_TOKEN RAILWAY_PROJECT_ID 2>/dev/null || true

# --- fixtures ---------------------------------------------------------------------------------------
# One instance in the target environment (ENV-A) and one in another (ENV-B), because filtering by
# environment is what separates this from the mistake a separate change is about: a volume is PROJECT-scoped, so
# another environment's instance is reachable through the same graph.
volumes_json() { # volumes_json [state-of-ENV-A-instance]
  cat <<JSON
{"data":{"project":{"name":"stub","volumes":{"edges":[
 {"node":{"id":"VOL-mysql","name":"mysql-data","volumeInstances":{"edges":[
   {"node":{"id":"VI-mysql-a","environmentId":"ENV-A","mountPath":"/var/lib/mysql","state":"${1:-READY}"}},
   {"node":{"id":"VI-mysql-b","environmentId":"ENV-B","mountPath":"/var/lib/mysql","state":"READY"}}
 ]}}},
 {"node":{"id":"VOL-pg","name":"postgres-data","volumeInstances":{"edges":[
   {"node":{"id":"VI-pg-a","environmentId":"ENV-A","mountPath":"/var/lib/postgresql/data","state":"READY"}}
 ]}}}
]}}}}
JSON
}

# THE DATA-REFRESHABLE DECLARATION IS A FIXTURE TOO, and the baseline is DELIBERATELY "no file at all".
# The subject's default path is the real scripts/railway-data-refreshable.json in the checkout, which
# declares the live staging environment — so a suite that did not override this would be reading
# production policy, and would start passing or failing on an edit to a file it is not testing.
# ENV-A absent from the declaration means "not refreshable", which is the earlier expectation, so
# every case written before this existed is unchanged.
declare_refreshable() { # declare_refreshable <json-body|NONE>
  if [ "$1" = "NONE" ]; then
    export RAILWAY_DATA_REFRESHABLE_FILE="$FIX/no-such-declaration.json"
  else
    printf '%s\n' "$1" > "$FIX/refreshable.json"
    export RAILWAY_DATA_REFRESHABLE_FILE="$FIX/refreshable.json"
  fi
}
#
# THE FIXTURES NAME BOTH FIELDS, BECAUSE THE TWO QUESTIONS ARE INDEPENDENT and the interesting shapes are
# the ones where they disagree. `refreshable` is what railway-snapshot-guard.sh reads; `schedules` is
# what the subject reads. A fixture that sets only the first is not "staging" — it is a document that
# answers the subject's question with silence, which must fall to the strict `daily`.
DECL_ENV_A_STAGING='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub-staging","refreshable":true,"schedules":"none","reason":"synthetic demo data, re-seedable","schedulesReason":"ruling: neither environment carries schedules"}]}'
# PRODUCTION'S SHAPE FROM a separate change TO a separate change, AND THE ONE THE FIRST VERSION OF THIS CHANGE COULD NOT
# REPRESENT: the guard must still snapshot here, AND no schedule belongs here. Driving both from
# `refreshable` made the second answer "DAILY everywhere". Production no longer declares it;
# the case stays, because the questions are still independent.
DECL_ENV_A_PRODUCTION='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub-production","refreshable":false,"schedules":"none","reason":"fill never measured; the guard is the only control in front of the apply","schedulesReason":"ruling: neither environment carries schedules"}]}'
# THE OTHER DISAGREEMENT, asserted so the separation is proven in both directions rather than in one:
# throwaway data that is nonetheless declared to carry DAILY schedules.
DECL_ENV_A_REFRESHABLE_BUT_DAILY='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub","refreshable":true,"schedules":"daily","reason":"throwaway","schedulesReason":"kept deliberately"}]}'
DECL_ENV_A_NOT='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub-staging","refreshable":false,"reason":"holds something"}]}'
DECL_ENV_B_REFRESHABLE='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-B","name":"other","refreshable":true,"schedules":"none","reason":"a DIFFERENT environment","schedulesReason":"a DIFFERENT environment"}]}'

fixture() { # the green baseline: two READY instances in ENV-A, neither carrying any schedule
  rm -rf "$FIX"; mkdir -p "$FIX"; : > "$REQUESTS"
  declare_refreshable NONE
  echo '{"data":{"projectToken":{"projectId":"PROJ","environmentId":"ENV-A"}}}' > "$FIX/projectToken.json"
  volumes_json                                                                 > "$FIX/volumes.json"
  echo '{"data":{"volumeInstanceBackupScheduleList":[]}}'                      > "$FIX/schedules-1.json"
  echo '{"data":{"volumeInstanceBackupScheduleUpdate":true}}'                  > "$FIX/update.json"
  printf '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}\n' \
                                                                               > "$FIX/schedules-2.json"
}

asserts=0
fails=0
pass() { asserts=$((asserts + 1)); printf '  ok    %s\n' "$1"; }
fail() { asserts=$((asserts + 1)); fails=$((fails + 1)); printf '\033[31m  FAIL  %s\033[0m  %s\n' "$1" "$2" >&2; }

# Runs the subject and asserts BOTH the exit code AND a substring of its output. One assertion, two
# checks, because either alone is the permissive direction.
expect() { # expect <exit> <substring> <description> <mode> [args...]
  local want="$1" needle="$2" desc="$3"; shift 3
  set +e; out="$(bash "$SUBJECT" "$@" 2>&1)"; got=$?; set -e
  LAST_OUT="$out"
  if [ "$got" -ne "$want" ]; then
    fail "$desc" "expected exit $want, got $got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-200)"; return
  fi
  case "$out" in
    *"$needle"*) pass "$desc" ;;
    *)           fail "$desc" "exit $got was right but not for the reason under test — wanted '$needle', got $(printf '%s' "$out" | tr '\n' '|' | cut -c1-200)" ;;
  esac
}

# An assertion about the REQUESTS the subject made, which is the only place some mutants are visible.
expect_request() { # expect_request <yes|no> <substring> <description>
  local want="$1" needle="$2" desc="$3" saw="no" how="NOT contain"
  grep -qF -- "$needle" "$REQUESTS" 2>/dev/null && saw="yes"
  [ "$want" = "yes" ] && how="contain"
  if [ "$saw" = "$want" ]; then pass "$desc"
  else fail "$desc" "wanted the request log to $how '$needle'"; fi
}

echo "== check =="

fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 0 "BACKUP SCHEDULES OK - 2 of 2" "check passes when every volume carries DAILY" check

fixture
expect 3 "no DAILY schedule" "check fails when no schedule exists at all" check
# a separate change (review round 1, note 86582): the MISSING remedy must send the operator to the declaration
# BEFORE it sends them to `apply`, because both environments are declared schedules: none, so this
# branch firing at all means the document changed or could not be read — telling `apply` first risks
# re-creating exactly what the maintainer's ruling retired. The first assertion here only required
# SOME "Check " to appear before SOME "apply" mention anywhere in the whole output, which two mutants
# passed: the declaration path replaced by the literal word "nothing", and the file check and apply
# lines swapped. Cutting the output at the FIRST mention of `apply` and requiring the ACTUAL resolved
# path (via $RAILWAY_DATA_REFRESHABLE_FILE, which this fixture exports) to appear before that cut
# closes both: a wrong or missing path fails, and apply appearing first leaves nothing before the cut
# for the path to be found in.
head="${LAST_OUT%%railway-backup-schedules.sh apply*}"
case "$head" in
  *"Check ${RAILWAY_DATA_REFRESHABLE_FILE} first"*)
    pass "check's MISSING remedy names the declaration's ACTUAL path before the first mention of apply" ;;
  *)
    fail "check's MISSING remedy names the declaration's ACTUAL path before the first mention of apply" \
         "wanted 'Check ${RAILWAY_DATA_REFRESHABLE_FILE} first' before the first 'railway-backup-schedules.sh apply' in: $(printf '%s' "${LAST_OUT:-}" | tr '\n' '|' | cut -c1-300)" ;;
esac

fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"WEEKLY","cron":"0 3 * * 0"}]}}' > "$FIX/schedules-1.json"
expect 3 "no DAILY schedule" "check fails when the only schedule is WEEKLY — a weekly is not a daily" check

# FAILS CLOSED IN THE DIRECTION THAT COSTS NOTHING. An unreadable schedule list is reported MISSING.
# A version that skipped it, or treated the empty result as "no problem found", would report a green
# check over a project in exactly the state the 2026-09-18 incident was in.
fixture
echo '{"errors":[{"message":"Not Authorized"}]}' > "$FIX/schedules-1.json"
expect 3 "could not be read, which is never reported as the intended state" "check reports an UNREADABLE schedule list as wrong, not as present" check

fixture
expect 2 "no READY volume instance named" "check refuses a --volume name that matches nothing rather than checking nothing" check --volume mysql_data

fixture
volumes_json DELETING > "$FIX/volumes.json"
expect 3 "1 of 1" "a non-READY instance is excluded from the set entirely" check

echo "== apply =="

fixture
expect 0 "BACKUP SCHEDULES APPLIED - 2 of 2" "apply sets DAILY where there is none, and verifies it" apply
expect_request yes '"kinds":["DAILY"]' "apply sends DAILY when the volume carried no schedules"

# THE UNION, AND THIS IS THE MUTANT THE RESPONSE CANNOT SHOW YOU. `kinds` REPLACES the set, so sending
# ["DAILY"] to a volume carrying WEEKLY deletes the weekly schedule — and the mutation answers `true`
# either way. Only the request body distinguishes them.
fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"WEEKLY","cron":"0 3 * * 0"}]}}' > "$FIX/schedules-1.json"
printf '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"WEEKLY","cron":"0 3 * * 0"},{"id":"S2","kind":"DAILY","cron":"0 3 * * *"}]}}\n' > "$FIX/schedules-2.json"
expect 0 "APPLIED" "apply adds DAILY beside an existing WEEKLY" apply
expect_request yes '"kinds":["DAILY","WEEKLY"]' "apply sends the UNION, so an existing WEEKLY survives"
expect_request no  '"kinds":["DAILY"]}' "apply never sends [DAILY] alone over a volume that had other kinds"

fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 0 "already DAILY" "apply is idempotent — a volume already carrying DAILY is left alone" apply
expect_request no 'volumeInstanceBackupScheduleUpdate' "apply sends no mutation at all when DAILY is already set"

# THE HEADLINE CASE. `true` is the API's word for it; the list is the evidence. A version that trusts
# the boolean passes every other case in this file and fails this one.
fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[]}}' > "$FIX/schedules-2.json"
expect 2 "still shows no DAILY schedule" "apply refuses when the mutation returns true but the schedule list does not show it" apply

fixture
echo '{"data":{"volumeInstanceBackupScheduleUpdate":false}}' > "$FIX/update.json"
expect 2 "returned 'false' rather than true" "apply refuses when the mutation itself reports failure" apply

fixture
echo '{"errors":[{"message":"boom"}]}' > "$FIX/update.json"
expect 2 "the schedule mutation failed" "apply refuses when the mutation errors" apply

# REFUSING TO SEND OVER SCHEDULES IT CANNOT SEE is a consequence of `kinds` replacing the set: without
# the pre-read there is no union to compute, and proceeding would mean sending ["DAILY"] blind.
fixture
echo '{"errors":[{"message":"Not Authorized"}]}' > "$FIX/schedules-1.json"
expect 2 "Refusing rather than sending" "apply refuses when it cannot READ the existing schedules first" apply
expect_request no 'volumeInstanceBackupScheduleUpdate' "apply sends no mutation when the pre-read failed"

echo "== scope and credential =="

fixture
expect 0 "APPLIED - 1 of 1" "--volume narrows the set to the named volume" apply --volume mysql-data
expect_request yes 'VI-mysql-a' "--volume touched the named instance"
expect_request no  'VI-pg-a'    "--volume left the other volume alone"
expect_request no  'VI-mysql-b' "another ENVIRONMENT's instance of the same volume is never touched"

fixture
echo '{"errors":[{"message":"Not Authorized"}]}' > "$FIX/projectToken.json"
expect 2 "could not read the project token's scope" "an unreadable token scope refuses rather than guessing an environment" check

fixture
RAILWAY_ENVIRONMENT_ID=ENV-B; export RAILWAY_ENVIRONMENT_ID
expect 2 "Refusing rather than guessing which one you meant" "a RAILWAY_ENVIRONMENT_ID disagreeing with the token's own scope refuses" check
unset RAILWAY_ENVIRONMENT_ID

fixture
echo '{"errors":[{"message":"Not Authorized"}]}' > "$FIX/volumes.json"
expect 2 "could not read the project's volume graph" "an unreadable volume graph refuses rather than reporting nothing to do" check

echo '== the `schedules` declaration flips what `check` EXPECTS =='

# a separate change retired the schedules in both environments. The hazard in that change is not the removal, it is
# what `check` becomes afterwards: a check whose expected state is "nothing" and whose every other answer
# is a shrug can never go red, and a gate that cannot fail is indistinguishable from a deleted one. So
# the expectation FLIPS rather than going away, and both directions below are cases.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
expect 0 "carry no schedule, as declared" "check PASSES on no schedules when the environment declares schedules: none" check

# THE NEGATIVE DIRECTION, and the one that proves the flip is a real expectation rather than a bypass.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 3 "carries a schedule the declaration says it should not" "check FAILS on a schedule that reappeared where none is declared" check

# "I could not see it" is not "as declared" any more than it was "present" — the direction that costs
# nothing, kept in the flipped mode too.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"errors":[{"message":"Not Authorized"}]}' > "$FIX/schedules-1.json"
expect 3 "could not be read, which is never reported as the intended state" \
  "an UNREADABLE list is a failure in the no-schedules direction too" check

# EVERY WAY THE DECLARATION CAN FAIL TO SAY "none" LEAVES THE DAILY EXPECTATION STANDING — the strict
# direction for THIS question, and the earlier behaviour for an environment nobody has ruled on. Each
# of these would otherwise be a way to authorise `remove` by accident.
fixture
declare_refreshable "$DECL_ENV_B_REFRESHABLE"
expect 3 "no DAILY schedule" "a declaration naming a DIFFERENT environment does not apply to this one" check

fixture
declare_refreshable '{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub","refreshable":"true","reason":"typed as a string"}]}'
expect 3 "no DAILY schedule" "an entry that declares no schedules field at all is read as DAILY-expected" check

fixture
declare_refreshable 'not json at all'
expect 3 "no DAILY schedule" "an unparseable declaration is read as DAILY-expected" check

fixture
# schedules:"none" ON PURPOSE: without a decisive value here, data_schedules_decl's own "neither none
# nor daily" fallback ALSO answers "daily" once the entry is matched, backstopping the kind check
# invisibly — deleting it left this case green. "none" is what a working match on this entry would
# answer, so only the kind guard's own fallback can still produce "daily" here.
declare_refreshable '{"environments":[{"environmentId":"ENV-A","schedules":"none","schedulesReason":"stub"}]}'
expect 3 "no DAILY schedule" "a declaration that does not name its kind is read as DAILY-expected" check

# EXACT STRING, not a spelling of it. "NONE" is not "none", and a declaration has to say the word to
# authorise the removal — the same instinct as `refreshable` demanding the boolean rather than "true".
fixture
declare_refreshable '{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub","refreshable":false,"schedules":"NONE","schedulesReason":"shouted"}]}'
expect 3 "no DAILY schedule" "schedules: \"NONE\" is not the declared value \"none\"" check

echo '== PRODUCTION SHAPE: not refreshable AND no schedules (the two questions disagree) =='

# THE BLOCK THAT WOULD HAVE CAUGHT THE DEFECT the REVIEW FOUND, and every assertion in it FAILS
# against the version of the subject that read `refreshable` for this question. Production was then declared
# `refreshable: false` — its fill was unmeasured and the snapshot guard the only control in front of
# railway-apply-production; a separate change has since declared it refreshable — and that single boolean also made `check` demand a DAILY schedule
# on every production volume, report them all MISSING, exit 3, and tell the operator to run the `apply`
# that re-creates exactly what the maintainer's ruling retired. Three documents shipped in the same
# commit said "nothing is owed here". The suite agreed with the code rather than with the decision.
#
# So: `schedules` governs these three verbs, `refreshable` governs the guard, and nothing crosses over.
fixture
declare_refreshable "$DECL_ENV_A_PRODUCTION"
expect 0 "carry no schedule, as declared" \
  "check reports a NOT-refreshable environment that declares schedules: none as COMPLIANT" check

# ... and the flip is a real expectation there too, not a bypass bought with the compliant case above.
fixture
declare_refreshable "$DECL_ENV_A_PRODUCTION"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 3 "carries a schedule the declaration says it should not" \
  "a schedule that unexpectedly EXISTS in that same environment is still DRIFT" check

fixture
declare_refreshable "$DECL_ENV_A_PRODUCTION"
expect 2 "declared to carry NO scheduled backups" \
  "apply REFUSES there too — the ruling covered both environments" apply
expect_request no 'volumeInstanceBackupScheduleUpdate' "the refused apply sent no mutation"

# `remove` BELONGS THERE — an environment the ruling says should carry none — and it says out
# loud what it costs, because `refreshable: false` means the guard still takes a manual snapshot whose
# FIRST one now meets Railway's 50% cap. The declaration decided that; this only reports it.
fixture
declare_refreshable "$DECL_ENV_A_PRODUCTION"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
echo '{"data":{"volumeInstanceBackupScheduleList":[]}}' > "$FIX/schedules-2.json"
expect 0 "BACKUP SCHEDULES REMOVED - 2 of 2" \
  "remove is PERMITTED in a not-refreshable environment that declares schedules: none" remove
case "${LAST_OUT:-}" in
  *"UNFUNDING"*"50%"*) pass "that removal warns that it unfunds the snapshot guard's first manual backup" ;;
  *) fail "that removal warns that it unfunds the snapshot guard's first manual backup" "not in output: $(printf '%s' "${LAST_OUT:-}" | tr '\n' '|' | cut -c1-200)" ;;
esac

# THE OTHER DISAGREEMENT, so the separation is proven in both directions rather than in one: throwaway
# data that is nonetheless declared to carry DAILY schedules. A subject still reading `refreshable`
# would expect none here, permit `remove` and refuse `apply` — every one of them backwards.
fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE_BUT_DAILY"
expect 3 "no DAILY schedule" "a REFRESHABLE environment declaring schedules: daily still expects DAILY" check

fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE_BUT_DAILY"
expect 0 "BACKUP SCHEDULES APPLIED - 2 of 2" "apply is permitted there" apply

fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE_BUT_DAILY"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 2 "NOT declared to carry no scheduled backups" "remove REFUSES there" remove

echo "== remove (the recorded operation that retires a schedule) =="

fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
echo '{"data":{"volumeInstanceBackupScheduleList":[]}}' > "$FIX/schedules-2.json"
expect 0 "BACKUP SCHEDULES REMOVED - 2 of 2" "remove takes DAILY away and verifies it is gone" remove
expect_request yes '"kinds":[]' "remove sends the empty set when DAILY was the only kind"
# DECL_ENV_A_STAGING declares refreshable:true, so the UNFUNDING warning must NOT fire here — it
# exists to flag environments where the guard STILL snapshots, and this one is not one of them. Proved by
# mutation: forcing scripts/railway-backup-schedules.sh:401's `[ "$DATA_REFRESHABLE" != "yes" ]` to
# `true` reddens this exact assertion (verified by hand, not asserted here — that mutation is of the
# subject, not something this fixture can drive).
case "${LAST_OUT:-}" in
  *"UNFUNDING"*) fail "the UNFUNDING warning does NOT fire in a refreshable environment" \
                       "found UNFUNDING though ENV-A is declared refreshable: $(printf '%s' "${LAST_OUT:-}" | tr '\n' '|' | cut -c1-200)" ;;
  *)             pass "the UNFUNDING warning does NOT fire in a refreshable environment" ;;
esac

# THE SAME MUTANT THE RESPONSE CANNOT SHOW YOU, RUN THE OTHER WAY. `kinds` REPLACES the set, so sending
# [] to a volume carrying WEEKLY deletes the weekly schedule as well — a removal larger than the one
# anybody asked for, and the mutation answers `true` either way.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"},{"id":"S2","kind":"WEEKLY","cron":"0 3 * * 0"}]}}' > "$FIX/schedules-1.json"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S2","kind":"WEEKLY","cron":"0 3 * * 0"}]}}' > "$FIX/schedules-2.json"
expect 0 "REMOVED" "remove takes DAILY away from beside an existing WEEKLY" remove
expect_request yes '"kinds":["WEEKLY"]' "remove sends the set MINUS DAILY, so an existing WEEKLY survives"
expect_request no  '"kinds":[]' "remove never sends the empty set over a volume that had other kinds"

fixture
declare_refreshable "$DECL_ENV_A_STAGING"
expect 0 "nothing sent" "remove is idempotent — a volume carrying no DAILY is left alone" remove
expect_request no 'volumeInstanceBackupScheduleUpdate' "remove sends no mutation at all when there is no DAILY"

# THE HEADLINE CASE FOR THIS VERB. `true` is the API's word for it; the list is the evidence. A removal
# that did not happen leaves a schedule running under a log line saying it is gone.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
cp "$FIX/schedules-1.json" "$FIX/schedules-2.json"
expect 2 "still shows a DAILY schedule" "remove refuses when the mutation returns true but the schedule survives" remove

fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
echo '{"data":{"volumeInstanceBackupScheduleUpdate":false}}' > "$FIX/update.json"
expect 2 "returned 'false' rather than true" "remove refuses when the mutation itself reports failure" remove

# A KINDS LIST LONGER THAN A PIPE BUFFER. Both of remove's DAILY reads were `printf | grep -qx` under
# pipefail: grep exits at the match, printf dies of SIGPIPE writing the rest, and the 141 read as "no
# DAILY". Before the mutation that skipped a volume still carrying one; after it, it passed a removal
# that had not happened. DAILY first, then ~280 KB of WEEKLY, so every line after the match is the
# SIGPIPE. One case per read.
long_schedules() { # long_schedules <DAILY|none> <file>
  awk -v daily="$1" 'BEGIN {
    printf "{\"data\":{\"volumeInstanceBackupScheduleList\":["
    if (daily == "DAILY") printf "{\"id\":\"S-DAILY\",\"kind\":\"DAILY\",\"cron\":\"0 3 * * *\"},"
    for (i = 0; i < 40000; i++) printf "%s{\"id\":\"S%06d\",\"kind\":\"WEEKLY\",\"cron\":\"0 3 * * 0\"}", (i ? "," : ""), i
    printf "]}}\n"
  }' > "$2"
}
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
long_schedules DAILY "$FIX/schedules-1.json"
long_schedules none  "$FIX/schedules-2.json"
long_kinds="$(jq -r '.data.volumeInstanceBackupScheduleList[].kind' "$FIX/schedules-1.json" | wc -c)"
if [ "$long_kinds" -gt 262144 ]; then pass "the long kinds list is longer than a pipe buffer ($long_kinds bytes)"
else fail "the long kinds list is longer than a pipe buffer" "only $long_kinds bytes - the cases below would be coin flips"; fi
expect 0 "BACKUP SCHEDULES REMOVED - 2 of 2" "remove sees DAILY at the head of a kinds list however long the rest" remove
expect_request yes '"kinds":["WEEKLY"]' "and sends the set minus DAILY for it"

fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
long_schedules DAILY "$FIX/schedules-2.json"
expect 2 "still shows a DAILY schedule" "remove refuses a surviving DAILY at the head of a kinds list however long the rest" remove

# AND A grep THAT GIVES NO COUNT IS NOT A "NO". A here-string larger than a pipe is spooled to a temp
# file, and when bash cannot create one the redirection fails with 1 - grep's own "no match" - before
# grep starts; a separate change measured it. The shim stands in for that, for the DAILY read only.
mkdir -p "$tmp/mute-count-grep"
printf '#!/bin/sh\ncase "$1" in -v*) ;; *) for a; do [ "$a" = DAILY ] && exit 1; done ;; esac\nexec %s "$@"\n' "$(command -v grep)" > "$tmp/mute-count-grep/grep"
chmod +x "$tmp/mute-count-grep/grep"
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
PATH="$tmp/mute-count-grep:$PATH" expect 2 "grep returned no count" \
  "remove refuses a DAILY read that returns no count rather than reading it as 'no DAILY'" remove

echo "== the two verbs are gated on the declaration, in opposite directions =="

# THE TRAP THE WHOLE OF a separate change IS ABOUT. Taking the schedules away where no document says to removes the
# backups AND, where the guard still snapshots, unfunds its manual snapshot in one step — and both
# failures surface later and somewhere else. An entry that answers the OTHER question and says nothing
# about this one is exactly that case: silence is not permission.
fixture
declare_refreshable "$DECL_ENV_A_NOT"
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 2 "NOT declared to carry no scheduled backups" "remove REFUSES against an entry with no schedules field" remove
expect_request no 'volumeInstanceBackupScheduleUpdate' "the refused remove sent no mutation"

fixture
echo '{"data":{"volumeInstanceBackupScheduleList":[{"id":"S1","kind":"DAILY","cron":"0 3 * * *"}]}}' > "$FIX/schedules-1.json"
expect 2 "NOT declared to carry no scheduled backups" "remove REFUSES when there is no declaration at all" remove

# ... and the mirror image: setting a schedule where a written decision says none belongs.
fixture
declare_refreshable "$DECL_ENV_A_STAGING"
expect 2 "declared to carry NO scheduled backups" "apply REFUSES where none is declared rather than contradicting it" apply
expect_request no 'volumeInstanceBackupScheduleUpdate' "the refused apply sent no mutation"

echo "== the reader answers nothing about an environment nobody named =="

# NOT REACHABLE THROUGH THE SUBJECT — both callers die on an empty scope before they ask — so it is
# probed at the reader directly. `.environmentId? // "" == $env` matches when BOTH sides are empty, so
# an empty argument against an entry that has no `environmentId` key would otherwise match and answer
# out of an entry written for nothing. The file promises "nothing here can make an environment
# refreshable by breaking" without qualification, and this is the one input that could.
# shellcheck source=scripts/railway-data-refreshable.sh
. "$HERE/railway-data-refreshable.sh"
printf '%s\n' '{"kind":"agentforge.railway.data-refreshable","environments":[{"name":"no id at all","refreshable":true,"schedules":"none","reason":"r","schedulesReason":"r"}]}' > "$tmp/no-id.json"
RAILWAY_DATA_REFRESHABLE_FILE="$tmp/no-id.json" data_refreshable_resolve ""
case "$DATA_REFRESHABLE" in
  no) pass "an EMPTY environment id is NOT refreshable, even against an entry that has no environmentId" ;;
  *)  fail "an EMPTY environment id is NOT refreshable, even against an entry that has no environmentId" "got '$DATA_REFRESHABLE' — $DATA_REFRESHABLE_WHY" ;;
esac
RAILWAY_DATA_REFRESHABLE_FILE="$tmp/no-id.json" data_schedules_resolve ""
case "$DATA_SCHEDULES" in
  daily) pass "an EMPTY environment id expects DAILY, so it can never authorise a removal" ;;
  *)     fail "an EMPTY environment id expects DAILY, so it can never authorise a removal" "got '$DATA_SCHEDULES' — $DATA_SCHEDULES_WHY" ;;
esac

echo "== the declaration SHIPPED in this repository parses and means something =="

# THE ONE CASE THAT READS THE REAL FILE. Every case above drives a fixture, which proves the reader and
# proves nothing about the document the deploy path will actually read: a typo there is invisible to a
# suite that never opens it, and its only symptom is a guard silently reverting to strict — or, worse,
# an environment silently declared throwaway. Shape, not content, so adding an environment does not
# redden this.
#
# BOTH QUESTIONS ARE PINNED, EACH WITH ITS OWN JUSTIFICATION. An entry that answers `refreshable` and
# leaves `schedules` absent still reads as DAILY-expected — safe, but silently wrong for an environment
# somebody meant to retire — and an answer with no `schedulesReason` is a decision nobody can review.
DECL_REAL="$HERE/railway-data-refreshable.json"
if jq -e '
      (.kind == "agentforge.railway.data-refreshable")
      and ((.environments | type) == "array") and ((.environments | length) > 0)
      and (all(.environments[];
              ((.environmentId | type) == "string") and ((.environmentId | length) == 36)
              and ((.name | type) == "string") and ((.name | length) > 0)
              and ((.refreshable | type) == "boolean")
              and ((.reason | type) == "string") and ((.reason | length) > 0)
              and ((.schedules == "none") or (.schedules == "daily"))
              and ((.schedulesReason | type) == "string") and ((.schedulesReason | length) > 0)))
      and ((.environments | map(.environmentId) | unique | length) == (.environments | length))
    ' "$DECL_REAL" >/dev/null 2>&1; then
  pass "the shipped railway-data-refreshable.json answers BOTH questions, with a reason each, per environment"
else
  fail "the shipped railway-data-refreshable.json answers BOTH questions, with a reason each, per environment" \
       "$DECL_REAL is missing, unparseable, or has an entry without a 36-character environmentId, a name, a BOOLEAN refreshable with a reason, and a schedules of \"none\" or \"daily\" with a schedulesReason"
fi

echo

# THE COUNT IS THE POINT OF THIS BLOCK. A suite that never reached its cases prints no failure lines
# either, so "no FAIL in the output" is the shape of both a pass and a no-op. Raise this number when
# you add a case; a mismatch is a red suite.
EXPECTED_ASSERTIONS=66
if [ "$asserts" -ne "$EXPECTED_ASSERTIONS" ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  ran %s assertion(s), expected %s — a case stopped firing, or one was added without updating EXPECTED_ASSERTIONS.\n' \
    "$asserts" "$EXPECTED_ASSERTIONS" >&2
  exit 1
fi
if [ "$fails" -ne 0 ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  %s of %s assertion(s)\n' "$fails" "$asserts" >&2
  exit 1
fi
printf '\033[32mSELF-TEST PASSED\033[0m  %s of %s assertions\n' "$asserts" "$asserts"
