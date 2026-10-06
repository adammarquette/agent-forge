#!/usr/bin/env bash
# railway-snapshot-guard-selftest.sh — prove the snapshot guard still refuses, and still permits.
#
# The guard's whole value is the refusal, and a refusal is exactly the behaviour that rots silently: a
# guard that has quietly started returning 0 looks identical to one that is working, because the happy
# path is the one anybody exercises. So the cases below are weighted towards red, and every red case
# asserts TWICE — that the guard exited non-zero, and that the guarded command did not run, which is the
# property the incident actually needed.
#
# THE HEADLINE CASE IS `the workflow says Complete but no new backup exists`. That is the shape of the
# failure the guard was written for: Railway reports success for work that produced nothing, the job log
# carries a cheerful receipt, and the volume is deleted a second later. A guard that trusts the workflow
# status alone passes every other test in this file and fails that one.
#
# THE CASES ARE ALSO WRITTEN AGAINST MUTANTS, not only against regressions. Each of these kills a change
# a reasonable person might make to the guard, and each was RED against the guard as first written:
#   * a harmless plan must not clear a destructive or unrecognised argv (`if true` at `decide`);
#   * a plan schema this file does not recognise must be UNDECIDABLE, not harmless;
#   * a REAL Railway plan must be classified from `changeSet.changes[].severity` and not from the English
#     prose Railway happens to put in `.diff`, `.summary` and `.changeSet.diagnostics[].message`;
#   * the plan the guard classified must be the plan the guarded command applies;
#   * a pinned plan must not clear a `config apply` that will RE-EVALUATE the authoring file instead of
#     reading it (`require_plan_identity` returning 0 when the guarded command names no plan);
#   * `canonical_path` must resolve a path, not print its basename;
#   * `severity()` must rank UNDECIDABLE above HARMLESS, not at it;
#   * `classify_plan`'s no-verdict fallback must be UNDECIDABLE, not EX_OK;
#   * the verified backup must be THIS RUN'S, not merely a new one (a scheduled backup landing mid-run);
#   * a non-READY volume instance must refuse (dropping `select(.state == "READY")`);
#   * `--volume NAME` must refuse when it resolves to zero instances, and when it resolves to several;
#   * `snapshot` mode must be exercised at all.
#
# THE REAL-PLAN FIXTURES ARE COPIED FROM A CAPTURED PRODUCTION PLAN, not invented. Every fixture in the
# round-2 suite used key names this file made up (`{"actions":[{"type":"delete"}]}`), and against a
# genuine `railway config plan --out` document — action key `kind`, `type` carrying a DATA type, a
# `severity` on every change — the classifier they all passed could not return HARMLESS at all. Fixtures
# that agree with each other and with nothing Railway emits prove only that the author was consistent.
#
# No network and no Railway account: the guard's transport is a seam
# (RAILWAY_SNAPSHOT_GUARD_TRANSPORT), and this file drives it with canned GraphQL documents.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GUARD="$HERE/railway-snapshot-guard.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

command -v jq >/dev/null 2>&1 || { echo "SELF-TEST CANNOT RUN: jq is required" >&2; exit 1; }

# IS THE GUARD EVEN A PROGRAM? This runs before case 1 because the suite cannot tell otherwise.
# `EX_REFUSED` is 2 and bash's own exit code for a SYNTAX ERROR is also 2, so a guard that does not parse
# does not fail uniformly — it fails selectively, and the cases that stay green are exactly the refusal
# cases. Inject an unbalanced quote into the guard and twenty-four `ok` lines still print, every one of
# them reporting that the fail-closed machinery works. The suite does redden overall, so nothing broken
# ships green; what it cannot do without this line is tell "the guard refused" from "the guard is not
# shell", and that aliasing is invisible to anyone reading the cases later. A separate change
if ! bash -n "$GUARD" 2>"$tmp/syntax.err"; then
  echo "SELF-TEST CANNOT RUN: $GUARD is not valid shell — every refusal case below would pass for the" >&2
  echo "wrong reason, because a bash syntax error and EX_REFUSED are both exit 2." >&2
  cat "$tmp/syntax.err" >&2
  exit 1
fi
# AND SO IS THE FILE IT SOURCES. `bash -n` on the guard does not parse what the guard `.`s at run time,
# so a syntax error in the shared data-refreshable reader lands on the cases as an exit 2 — which every
# refusal case accepts. Same aliasing as above, one file further out. A separate change
if ! bash -n "$HERE/railway-data-refreshable.sh" 2>"$tmp/syntax.err"; then
  echo "SELF-TEST CANNOT RUN: railway-data-refreshable.sh, which the guard sources, is not valid shell." >&2
  cat "$tmp/syntax.err" >&2
  exit 1
fi

FIX="$tmp/fix"
REQUESTS="$tmp/requests.log"
SENTINEL="$tmp/the-destructive-operation-ran"
# Fixed, because the guard now proves a snapshot is ITS OWN by the backup's name. The fixtures below
# name the "after" backup with this, and two cases deliberately do not.
GUARD_LABEL="selftest-run-label"

# --- the stub Railway API -----------------------------------------------------------------------------
# Reads a GraphQL request body on stdin, answers from $FIX. volumeInstanceBackupList is answered from
# backups-1.json the first time and backups-2.json afterwards, so "before" and "after" can differ — or,
# in the headline case, deliberately not.
cat > "$tmp/transport.sh" <<'STUB'
#!/usr/bin/env bash
set -eu
body="$(cat)"
# LOGGED, COMPACT, so a case can assert on what the guard did NOT ask for. "No snapshot was taken" is
# not visible in an exit code or in the sentinel: a guard that took one and then permitted looks
# identical from outside. Compact because `jq -n` pretty-prints, and an assertion written the way
# anyone would write it would match nothing and pass for free. A separate change
printf '%s' "$body" | jq -c . >> "$REQUESTS" 2>/dev/null || printf '%s\n' "$body" >> "$REQUESTS"
# A fixture whose first line is TRANSPORT_FAIL makes this exit non-zero with nothing on stdout - curl
# dying, not GraphQL answering. The distinction is load-bearing: gql_checked writes $ERRFILE on this
# path ONLY when it is already empty, so this is the one way stale text survives into a later call.
maybe_fail() {
  if [ -f "$FIX/$1" ] && head -n1 "$FIX/$1" | grep -q '^TRANSPORT_FAIL'; then
    echo "curl: (56) Recv failure: Connection reset by peer" >&2
    exit 1
  fi
}
case "$body" in
  *projectToken*)
      # MUTATE_PLAN_EARLY rewrites the plan at the FIRST call the guard makes. The MUTATE_PLAN hook
      # below fires on the backup mutation, which is exactly the call an environment declared
      # data-refreshable never makes — so without an earlier hook the fingerprint check could not be
      # exercised on that path at all. A separate change
      [ -n "${MUTATE_PLAN_EARLY:-}" ] && printf '{"kind":"railway.config.plan","destructive":false,"changeSet":{"changes":[]}}' > "$MUTATE_PLAN_EARLY"
      f="projectToken.json" ;;
  *volumeInstanceBackupCreate*)
      # A snapshot can take fifteen minutes, and a plan file is a mutable path on a shared runner. With
      # MUTATE_PLAN set, rewrite it mid-snapshot: the guard must notice before it execs.
      [ -n "${MUTATE_PLAN:-}" ] && printf '{"kind":"railway.config.plan","destructive":false,"changeSet":{"changes":[]}}' > "$MUTATE_PLAN"
      f="create.json" ;;
  *workflowStatus*)
      # Successive answers, only when a case asks for them. Without workflow-2.json this is exactly
      # the old single-file behaviour, so every pre-existing case is untouched.
      if [ -f "$FIX/workflow-2.json" ]; then
        w=1; [ -f "$FIX/.polled" ] && w=2
        : > "$FIX/.polled"
        f="workflow-$w.json"
      else
        f="workflow.json"
      fi
      maybe_fail "$f" ;;
  *volumeInstanceBackupList*)
      case "$body" in
        *VI-mysql-a2*)
          # Second instance, its own before/after pair, so it starts from an empty list like the first.
          if [ -f "$FIX/backups-b1.json" ]; then
            n=1; [ -f "$FIX/.listed-b" ] && n=2
            : > "$FIX/.listed-b"
            f="backups-b$n.json"
          else
            n=1; [ -f "$FIX/.listed" ] && n=2
            : > "$FIX/.listed"
            f="backups-$n.json"
          fi ;;
        *)
          # THREE answers when a case supplies backups-3.json, so the label can arrive LATE and the
          # fallback's wait loop has something to wait FOR. Without backups-3.json this is exactly the
          # old 1-then-2-forever behaviour, so no existing case changes.
          if [ -f "$FIX/.listed2" ] && [ -f "$FIX/backups-3.json" ]; then
            f="backups-3.json"
          elif [ -f "$FIX/.listed" ]; then
            : > "$FIX/.listed2"; f="backups-2.json"
          else
            : > "$FIX/.listed"; f="backups-1.json"
          fi ;;
      esac ;;
  *volumeInstances*)               f="volumes.json" ;;
  *) echo '{"errors":[{"message":"stub: unrecognised query"}]}'; exit 0 ;;
esac
cat "$FIX/$f"
STUB
chmod +x "$tmp/transport.sh"

# A stand-in for `railway`, so a green run proves the guarded command really was executed and a red run
# proves it really was not.
cat > "$tmp/fake-railway" <<STUB
#!/usr/bin/env bash
touch "$SENTINEL"
echo "fake-railway: \$*"
STUB
chmod +x "$tmp/fake-railway"

export FIX SENTINEL REQUESTS
export RAILWAY_SNAPSHOT_GUARD_TRANSPORT="bash $tmp/transport.sh"
export RAILWAY_TOKEN="stub-project-token"
export RAILWAY_SNAPSHOT_GUARD_POLL=1
export RAILWAY_SNAPSHOT_GUARD_TIMEOUT=1
unset RAILWAY_ENVIRONMENT_ID RAILWAY_API_TOKEN RAILWAY_PROJECT_ID 2>/dev/null || true

# --- fixtures ------------------------------------------------------------------------------------------
# One volume instance in the target environment (ENV-A) and one in another (ENV-B), because filtering by
# environment is the thing that separates this guard from the mistake it prevents.
volumes_json() { # volumes_json <sizeMB> <currentSizeMB> [state]
  cat <<JSON
{"data":{"project":{"name":"stub","volumes":{"edges":[
 {"node":{"id":"VOL-mysql","name":"mysql-data","volumeInstances":{"edges":[
   {"node":{"id":"VI-mysql-a","environmentId":"ENV-A","serviceId":"S1","mountPath":"/var/lib/mysql","state":"${3:-READY}","sizeMB":$1,"currentSizeMB":$2}},
   {"node":{"id":"VI-mysql-b","environmentId":"ENV-B","serviceId":"S2","mountPath":"/var/lib/mysql","state":"READY","sizeMB":5120,"currentSizeMB":10}}
 ]}}}
]}}}}
JSON
}

# Two instances in ENV-A carrying the SAME volume name — the shape `staging` was in when this was written,
# with three volumes at OpenEMR's sites path and two at /var/lib/mysql. Those duplicates are being deleted
# and made the authoring file name the volume each environment uses, so this is no longer a snapshot
# of production reality. It stays because the SHAPE is what is under test: a volume is project-scoped, so
# name collisions remain reachable whenever an environment is cloned, and the resolver must still refuse
# rather than pick.
volumes_json_duplicate_names() {
  cat <<JSON
{"data":{"project":{"name":"stub","volumes":{"edges":[
 {"node":{"id":"VOL-mysql","name":"mysql-data","volumeInstances":{"edges":[
   {"node":{"id":"VI-mysql-a","environmentId":"ENV-A","serviceId":"S1","mountPath":"/var/lib/mysql","state":"READY","sizeMB":5120,"currentSizeMB":100}}
 ]}}},
 {"node":{"id":"VOL-mysql-2","name":"mysql-data","volumeInstances":{"edges":[
   {"node":{"id":"VI-mysql-a2","environmentId":"ENV-A","serviceId":"S3","mountPath":"/var/lib/mysql","state":"READY","sizeMB":5120,"currentSizeMB":100}}
 ]}}}
]}}}}
JSON
}

# THE DATA-REFRESHABLE DECLARATION IS A FIXTURE TOO, and the baseline is DELIBERATELY "no file at all".
# The guard's default path is the real scripts/railway-data-refreshable.json in the checkout, which
# declares the live staging environment — so a suite that did not override this would be asserting
# against deployment policy and would start passing or failing on an edit to a file it is not testing.
# ENV-A absent from the declaration means "not refreshable", which is the earlier behaviour, so
# every case written before this existed is unchanged. A separate change
declare_refreshable() { # declare_refreshable <json-body|NONE>
  if [ "$1" = "NONE" ]; then
    export RAILWAY_DATA_REFRESHABLE_FILE="$FIX/no-such-declaration.json"
  else
    printf '%s\n' "$1" > "$FIX/refreshable.json"
    export RAILWAY_DATA_REFRESHABLE_FILE="$FIX/refreshable.json"
  fi
}
DECL_ENV_A_REFRESHABLE='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub-staging","refreshable":true,"reason":"synthetic demo data, re-seedable"}]}'
DECL_ENV_A_NOT='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub-staging","refreshable":false,"reason":"holds something"}]}'
DECL_ENV_B_REFRESHABLE='{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-B","name":"other","refreshable":true,"reason":"a DIFFERENT environment"}]}'

fixture() { # fixture — the green baseline; individual cases overwrite one file
  rm -rf "$FIX"; mkdir -p "$FIX"; rm -f "$SENTINEL"; : > "$REQUESTS"
  declare_refreshable NONE
  echo '{"data":{"projectToken":{"projectId":"PROJ","environmentId":"ENV-A"}}}' > "$FIX/projectToken.json"
  volumes_json 5120 100                                                        > "$FIX/volumes.json"
  echo '{"data":{"volumeInstanceBackupCreate":{"workflowId":"WF-1"}}}'         > "$FIX/create.json"
  echo '{"data":{"workflowStatus":{"status":"Complete","error":null}}}'        > "$FIX/workflow.json"
  echo '{"data":{"volumeInstanceBackupList":[]}}'                              > "$FIX/backups-1.json"
  printf '{"data":{"volumeInstanceBackupList":[{"id":"BK-NEW","name":"%s","createdAt":"2026-09-18T00:00:00Z"}]}}\n' \
    "$GUARD_LABEL"                                                             > "$FIX/backups-2.json"
}

fails=0
# COUNTED, BECAUSE A SUITE THAT NEVER REACHED ITS CASES PRINTS NO FAILURE LINES EITHER. "No FAIL in the
# output" is the shape of both a pass and a no-op, and this file has grown past a hundred cases across
# five helpers - a `case` in the stub transport that stops firing, a fixture path that moves, or an early
# `exit` would silently shrink it. The total is checked against EXPECTED_ASSERTIONS at the bottom, so
# "0 of 0" cannot read as a pass. One assertion here is one CASE, and most cases check two or three
# things at once (exit code, whether the guarded command ran, and the reason in the output).
asserts=0
pass() { asserts=$((asserts + 1)); printf '  ok    %s\n' "$1"; }
fail() { asserts=$((asserts + 1)); printf '\033[31m  FAIL  %s\033[0m  %s\n' "$1" "$2" >&2; fails=$((fails + 1)); }

# Runs the guard in `run` mode over the fake railway CLI and asserts the exit code AND whether the
# guarded command was allowed to execute. Anything between the description and `--` is passed to the
# GUARD (e.g. --plan FILE); everything after `--` is the guarded command line.
expect_run() { # expect_run <exit> <ran: yes|no> <description> [guard args...] -- <argv...>
  local want="$1" want_ran="$2" desc="$3"; shift 3
  local gargs=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do gargs+=("$1"); shift; done
  [ "${1:-}" = "--" ] && shift
  rm -f "$SENTINEL" "$FIX/.listed"
  set +e
  out="$(bash "$GUARD" run --label "$GUARD_LABEL" ${gargs[@]+"${gargs[@]}"} -- "$@" 2>&1)"; got=$?
  set -e
  local ran="no"; [ -f "$SENTINEL" ] && ran="yes"
  if [ "$got" -ne "$want" ]; then fail "$desc" "expected exit $want, got $got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-160)"; return; fi
  if [ "$ran" != "$want_ran" ]; then fail "$desc" "guarded command ran=$ran, expected ran=$want_ran"; return; fi
  LAST_OUT="$out"; pass "$desc"
}

# `snapshot` mode has no guarded command: it either takes and verifies snapshots, or refuses.
expect_snapshot() { # expect_snapshot <exit> <description> [guard args...]
  local want="$1" desc="$2"; shift 2
  rm -f "$FIX/.listed"
  set +e; out="$(bash "$GUARD" snapshot --label "$GUARD_LABEL" "$@" 2>&1)"; got=$?; set -e
  if [ "$got" -ne "$want" ]; then
    fail "$desc" "expected exit $want, got $got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-160)"; return
  fi
  LAST_OUT="$out"; pass "$desc"
}

expect_classify() { # expect_classify <exit> <description> -- <argv...>
  local want="$1" desc="$2"; shift 2; [ "${1:-}" = "--" ] && shift
  set +e; out="$(bash "$GUARD" classify "$@" 2>&1)"; got=$?; set -e
  if [ "$got" -ne "$want" ]; then fail "$desc" "expected exit $want, got $got — $out"; else LAST_OUT="$out"; pass "$desc"; fi
}

# THE EXIT CODE ALONE CANNOT TELL A STRUCTURAL VERDICT FROM A LUCKY ONE. The captured production plan
# classified DESTRUCTIVE under a classifier that had never heard of `severity`, because a regex found the
# word "Delete" in an English summary. So the real-plan cases below assert on the REASON as well.
# WHY THIS EXISTS, and why every refusal case below should use it rather than expect_run.
# Exit 2 is this guard's answer to "no credential", "unreadable graph", "ambiguous volume name",
# "label already taken", "workflow failed", "nothing was snapshotted" and more. A case asserting only
# on the code passes when the guard refuses for a reason the case was not written about - which is
# indistinguishable from working, and is how `:775` spent its whole life never reaching the check it
# names. Assert the REASON. A separate change
expect_run_says() { # expect_run_says <exit> <ran> <substring> <description> [guard args...] -- <argv...>
  local want="$1" want_ran="$2" needle="$3" desc="$4"; shift 4
  local gargs=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do gargs+=("$1"); shift; done
  [ "${1:-}" = "--" ] && shift
  rm -f "$SENTINEL" "$FIX/.listed" "$FIX/.listed-b" "$FIX/.polled"
  set +e
  out="$(bash "$GUARD" run --label "$GUARD_LABEL" ${gargs[@]+"${gargs[@]}"} -- "$@" 2>&1)"; got=$?
  set -e
  local ran="no"; [ -f "$SENTINEL" ] && ran="yes"
  if [ "$got" -ne "$want" ]; then fail "$desc" "expected exit $want, got $got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-160)"; return; fi
  if [ "$ran" != "$want_ran" ]; then fail "$desc" "guarded command ran=$ran, expected ran=$want_ran"; return; fi
  case "$out" in
    *"$needle"*) LAST_OUT="$out"; pass "$desc" ;;
    *)           fail "$desc" "refused, but not for the reason under test — wanted '$needle', got $(printf '%s' "$out" | tr '\n' '|' | cut -c1-200)" ;;
  esac
}

expect_snapshot_says() { # expect_snapshot_says <exit> <substring> <description> [guard args...]
  local want="$1" needle="$2" desc="$3"; shift 3
  rm -f "$FIX/.listed" "$FIX/.listed-b" "$FIX/.polled"
  set +e; out="$(bash "$GUARD" snapshot --label "$GUARD_LABEL" "$@" 2>&1)"; got=$?; set -e
  if [ "$got" -ne "$want" ]; then
    fail "$desc" "expected exit $want, got $got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-160)"; return
  fi
  case "$out" in
    *"$needle"*) LAST_OUT="$out"; pass "$desc" ;;
    *)           fail "$desc" "refused, but not for the reason under test — wanted '$needle', got $(printf '%s' "$out" | tr '\n' '|' | cut -c1-200)" ;;
  esac
}

expect_classify_says() { # expect_classify_says <exit> <substring> <description> -- <argv...>
  local want="$1" needle="$2" desc="$3"; shift 3; [ "${1:-}" = "--" ] && shift
  set +e; out="$(bash "$GUARD" classify "$@" 2>&1)"; got=$?; set -e
  if [ "$got" -ne "$want" ]; then fail "$desc" "expected exit $want, got $got — $out"; return; fi
  case "$out" in
    *"$needle"*) pass "$desc" ;;
    *)           fail "$desc" "verdict does not say '$needle' — $out" ;;
  esac
}

# An assertion about the REQUESTS the guard made. "It took no snapshot" is invisible in an exit code
# and in the sentinel — a guard that snapshotted and then permitted looks identical from outside — so
# the absence of the mutation is the only place the claim is actually checkable. A separate change
expect_request() { # expect_request <yes|no> <substring> <description>
  local want="$1" needle="$2" desc="$3" saw="no" how="NOT contain"
  grep -qF -- "$needle" "$REQUESTS" 2>/dev/null && saw="yes"
  [ "$want" = "yes" ] && how="contain"
  if [ "$saw" = "$want" ]; then pass "$desc"
  else fail "$desc" "wanted the request log to $how '$needle'"; fi
}

echo "== refusals (the guard's reason for existing) =="

# THE HEADLINE CASE.
fixture
echo '{"data":{"volumeInstanceBackupList":[]}}' > "$FIX/backups-2.json"
expect_run 2 no "a workflow that says Complete but produces no new backup is NOT a snapshot" \
  -- "$tmp/fake-railway" volume delete --yes

# Same lie, but against a volume that already had a backup — the diff path rather than the empty path.
fixture
echo '{"data":{"volumeInstanceBackupList":[{"id":"BK-OLD","name":"old","createdAt":"2026-01-01T00:00:00Z"}]}}' > "$FIX/backups-1.json"
cp "$FIX/backups-1.json" "$FIX/backups-2.json"
expect_run 2 no "an unchanged backup list is not a new snapshot, even when older backups exist" \
  -- "$tmp/fake-railway" volume delete --yes

# IDENTITY, NOT EXISTENCE. A backup this run did not take can land between the two list calls — a late
# completion from an earlier run, a concurrent agent, or a schedule someone set in the Backups tab (this
# project runs none, which makes the last rarer rather than impossible). Diffing ids would
# accept any of them.
fixture
echo '{"data":{"volumeInstanceBackupList":[{"id":"BK-SCHEDULED","name":"daily-2026-09-18","createdAt":"2026-09-18T03:00:00Z"}]}}' > "$FIX/backups-2.json"
expect_run 2 no "a SCHEDULED backup landing mid-run is not this run's snapshot" \
  -- "$tmp/fake-railway" volume delete --yes

# ... and the same label already being present means the label proves nothing about this run.
fixture
printf '{"data":{"volumeInstanceBackupList":[{"id":"BK-DUP","name":"%s","createdAt":"2026-09-01T00:00:00Z"}]}}\n' \
  "$GUARD_LABEL" > "$FIX/backups-1.json"
expect_run 2 no "a backup already carrying this run's label refuses rather than claiming it" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"data":{"workflowStatus":{"status":"Error","error":"volume busy"}}}' > "$FIX/workflow.json"
expect_run 2 no "a failed backup workflow refuses" -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"data":{"workflowStatus":{"status":"Running","error":null}}}' > "$FIX/workflow.json"
expect_run 2 no "a backup workflow that never completes refuses at the timeout" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"data":{"workflowStatus":{"status":"Elsewhere","error":null}}}' > "$FIX/workflow.json"
expect_run 2 no "an unrecognised workflow status is not read as success" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/backups-1.json"
expect_run 2 no "a GraphQL error document is a failure, not an empty backup list" \
  -- "$tmp/fake-railway" volume delete --yes

# --- workflowStatus unreadable: WAITING degrades, PROVING does not ------------------------------------
# A team token may create a backup and be refused `workflowStatus` (observed 2026-09-19 against
# fearless-abundance/staging). The guard then waits on the labelled backup instead. These four cases pin
# both halves of that: it must still reach a real snapshot, and it must still refuse everything it
# refused before. A separate change
fixture
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
expect_run 0 yes "an unreadable workflowStatus falls back to the labelled backup and still snapshots" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
echo '{"data":{"volumeInstanceBackupList":[]}}'             > "$FIX/backups-2.json"
expect_run_says 2 no "cannot read workflow status to say why" "the fallback refuses when no labelled backup ever appears" \
  -- "$tmp/fake-railway" volume delete --yes

# The fallback must not accept "some id is new" — a scheduled backup landing mid-run carries its own
# name, and the label is the whole reason this guard can tell them apart.
fixture
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
echo '{"data":{"volumeInstanceBackupList":[{"id":"BK-SCHEDULED","name":"daily-2026-09-19","createdAt":"2026-09-19T00:00:00Z"}]}}' > "$FIX/backups-2.json"
expect_run_says 2 no "cannot read workflow status to say why" "the fallback does not accept a scheduled backup as this run's snapshot" \
  -- "$tmp/fake-railway" volume delete --yes

# The tolerance is for ONE message and no other. Anything else from workflowStatus still says the
# workflow is in a state this guard must not read as success.
fixture
echo '{"errors":[{"message":"Internal Server Error"}],"data":null}' > "$FIX/workflow.json"
expect_run_says 2 no "Internal Server Error" "a workflowStatus error that is NOT Not Authorized is still fatal" \
  -- "$tmp/fake-railway" volume delete --yes

# THE ADVERSARIAL VERSION OF THE CASE ABOVE, and the one that has teeth. `Internal Server Error` shares
# no substring with `Not Authorized`, so it passes whether the gate is an exact comparison or a `grep`.
# GraphQL may return SEVERAL errors and gql_checked joins them with "; ", so a response can contain the
# phrase without BEING it. This is an explicit abort with a permissions error stapled to it, and reading
# it as "this credential cannot answer" would wait on a backup that was aborted.
fixture
printf '%s\n' '{"errors":[{"message":"workflow WF-1 failed: backup aborted"},{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
expect_run_says 2 no "backup aborted" "a joined error merely CONTAINING Not Authorized is not a permissions answer" \
  -- "$tmp/fake-railway" volume delete --yes

# THE FALLBACK'S WAIT LOOP, PINNED. Deleting all 13 lines of it left the whole suite green: the final
# identity check backstops everything the loop does, and no case ever made the label arrive LATE - which
# is the only state the loop exists for. The suite also caps TIMEOUT=1/POLL=1, so nothing reached a
# second poll. Here the list answers empty -> a SCHEDULED backup only -> scheduled + this run's label,
# with the deadline raised so the loop actually iterates. Without the loop the final check sees the
# scheduled-only answer and refuses; with it, the guard waits and succeeds. A separate change
fixture
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
echo '{"data":{"volumeInstanceBackupList":[{"id":"BK-SCHED","name":"daily-2026-09-19","createdAt":"2026-09-19T00:00:00Z"}]}}' > "$FIX/backups-2.json"
printf '{"data":{"volumeInstanceBackupList":[{"id":"BK-SCHED","name":"daily-2026-09-19","createdAt":"2026-09-19T00:00:00Z"},{"id":"BK-LATE","name":"%s","createdAt":"2026-09-19T00:01:00Z"}]}}
' "$GUARD_LABEL" > "$FIX/backups-3.json"
RSGT_SAVED="$RAILWAY_SNAPSHOT_GUARD_TIMEOUT"
export RAILWAY_SNAPSHOT_GUARD_TIMEOUT=5
expect_run 0 yes "the fallback WAITS for a label that arrives after the first list" -- "$tmp/fake-railway" volume delete --yes
export RAILWAY_SNAPSHOT_GUARD_TIMEOUT="$RSGT_SAVED"

# A PRE-RUN LIST LONGER THAN A PIPE BUFFER. Both "was this id here before the run?" reads were
# `printf | grep -qxF` under pipefail: grep exits at the match, printf dies of SIGPIPE writing the
# rest, and the 141 read as "not here before", so a backup that PREDATES the run was accepted as its
# snapshot. Here the label lands on a backup that was already listed, first of 10 001, so the match is
# line 1 and every line after it is the SIGPIPE. One case per read, each asserting the reason the
# fixed guard gives: the fallback loop's read alone reverted refuses too, but at the final check.
long_backups() { # long_backups <name of the first, pre-existing backup>
  awk -v first="$1" 'BEGIN {
    printf "{\"data\":{\"volumeInstanceBackupList\":[{\"id\":\"BK-RENAMED\",\"name\":\"%s\",\"createdAt\":\"2026-09-01T00:00:00Z\"}", first
    for (i = 0; i < 10000; i++) printf ",{\"id\":\"BK-PADDING-%06d-0123456789abcdef\",\"name\":\"old-%06d\",\"createdAt\":\"2026-01-01T00:00:00Z\"}", i, i
    printf "]}}\n"
  }'
}
fixture
long_backups "renamed-later"  > "$FIX/backups-1.json"
long_backups "$GUARD_LABEL"   > "$FIX/backups-2.json"
# THE SIZE IS THE CASE. Under a pipe buffer the race is a coin flip under load, not a certainty.
long_ids="$(jq -r '.data.volumeInstanceBackupList[].id' "$FIX/backups-1.json" | wc -c)"
if [ "$long_ids" -gt 262144 ]; then pass "the long pre-run id list is longer than a pipe buffer ($long_ids bytes)"
else fail "the long pre-run id list is longer than a pipe buffer" "only $long_ids bytes - the cases below would be coin flips"; fi
expect_run_says 2 no "already present before this run started" \
  "a pre-existing backup renamed to this run's label refuses however long the pre-run list" \
  -- "$tmp/fake-railway" volume delete --yes
echo '{"errors":[{"message":"Not Authorized"}],"data":null}' > "$FIX/workflow.json"
expect_run_says 2 no "appeared within" \
  "on the fallback path the wait loop does not take that pre-existing backup as new, however long the list" \
  -- "$tmp/fake-railway" volume delete --yes

# AND A grep THAT GIVES NO COUNT IS NOT A "NO". A here-string larger than a pipe is spooled to a temp
# file, and when bash cannot create one the redirection fails with 1 - grep's own "no match" - before
# grep starts; a separate change measured it. The shim stands in for that: silent, exit 1, for the identity read
# only. On the green baseline, a guard reading that as "not listed" would permit.
mkdir -p "$tmp/mute-count-grep"
printf '#!/bin/sh\ncase "$1" in -*x*F*|-*F*x*) exit 1 ;; esac\nexec %s "$@"\n' "$(command -v grep)" > "$tmp/mute-count-grep/grep"
chmod +x "$tmp/mute-count-grep/grep"
fixture
PATH="$tmp/mute-count-grep:$PATH" expect_run_says 2 no "grep returned no count" \
  "an identity read that returns no count refuses rather than reading as 'not listed'" \
  -- "$tmp/fake-railway" volume delete --yes

# STALE ERROR STATE MUST NOT CARRY BETWEEN VOLUME INSTANCES. do_snapshot loops over every instance in
# the environment, and $ERRFILE is one mktemp for the whole run. Before a separate change nothing truncated it, so
# instance 1's genuine `Not Authorized` remained readable while instance 2 was handling an unrelated
# failure - and staging has five instances, so with a team token this was the DEFAULT state from the
# second instance onward, not an edge case. Two instances here: the first is refused a workflow read,
# the second gets a 500 that must still be fatal.
fixture
volumes_json_duplicate_names > "$FIX/volumes.json"
# Instance 2 needs its OWN empty-then-labelled pair. Sharing instance 1's list made it refuse at the
# "label already taken" check before reaching the poll, which is exit 2 for the wrong reason - the
# negative control caught that, twice.
echo '{"data":{"volumeInstanceBackupList":[]}}' > "$FIX/backups-b1.json"
printf '{"data":{"volumeInstanceBackupList":[{"id":"BK-B","name":"%s","createdAt":"2026-09-18T00:00:00Z"}]}}
'   "$GUARD_LABEL" > "$FIX/backups-b2.json"
echo '{"errors":[{"message":"Not Authorized"}],"data":null}'        > "$FIX/workflow-1.json"
# TRANSPORT failure, not a GraphQL error document. A GraphQL error always OVERWRITES $ERRFILE, so the
# first version of this case passed against the bug it was written to catch - the negative control
# (revert the fix, watch this go red) is what exposed that, and is how this case was validated.
echo 'TRANSPORT_FAIL' > "$FIX/workflow-2.json"
expect_run_says 2 no "curl: (56)" "a second instance's TRANSPORT failure is not excused by the first's Not Authorized" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
echo '{"data":{"volumeInstanceBackupCreate":{"workflowId":null}}}' > "$FIX/create.json"
expect_run 2 no "a mutation that returns no workflow id refuses" -- "$tmp/fake-railway" volume delete --yes

# The environment filter: every instance in the graph belongs to some OTHER environment. ASSERTS THE
# MESSAGE: an exit-code-only check here is backstopped by do_snapshot's OWN "nothing was snapshotted"
# refusal once every row is filtered out, so it stayed green with resolve_instances' "no READY volume
# instances found" check deleted outright. Separate changes note 71397 finding 4 pattern
fixture
sed 's/ENV-A/ENV-Z/' "$FIX/volumes.json" > "$tmp/v" && mv "$tmp/v" "$FIX/volumes.json"
expect_run_says 2 no "no READY volume instances found" \
  "no volume instances in the target environment refuses rather than proceeding" \
  -- "$tmp/fake-railway" volume delete --yes

# The READY filter. A volume instance that is deleting, or restoring, or in any state Railway has not
# called READY is not a thing to take a first full backup of — and dropping that one `select` from the
# graph filter is a change that otherwise passes every case in this file. ASSERTS THE MESSAGE for the
# same reason as the case above: the same do_snapshot backstop covers a dropped `select` too.
fixture
volumes_json 5120 100 DELETING > "$FIX/volumes.json"
expect_run_says 2 no "no READY volume instances found" \
  "a volume instance that is not READY refuses rather than being snapshotted" \
  -- "$tmp/fake-railway" volume delete --yes

# The project-level/environment-level confusion, from the credential's side. A separate change
fixture
RAILWAY_ENVIRONMENT_ID=ENV-B expect_run 2 no \
  "a requested environment that disagrees with the token's scope refuses" \
  -- "$tmp/fake-railway" volume delete --yes

# Railway caps a manual backup at 50% of the volume; the first backup of a mostly-full volume cannot be
# taken, and finding that out mid-deploy is finding it out too late.
fixture
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run 2 no "a first backup of a volume past the 50% manual-backup cap refuses up front" \
  -- "$tmp/fake-railway" volume delete --yes

# ASSERTS THE MESSAGE: the exit-code-only version stayed green with gql_checked's own "not JSON" check
# deleted — the malformed body still ends up as an empty $rows one call later, which do_snapshot's
# "nothing was snapshotted" backstop then catches for an unrelated reason. A separate change
fixture
echo 'not json at all' > "$FIX/volumes.json"
expect_run_says 2 no "is not JSON" "an unreadable project graph refuses" -- "$tmp/fake-railway" volume delete --yes

echo "== a harmless plan must not clear a command line the guard cannot vouch for =="

echo '{"actions":[{"type":"create","resource":"service"}]}' > "$tmp/plan-create.json"
echo '{"actions":[{"type":"delete","resource":"volume"}]}'  > "$tmp/plan-delete.json"
echo '{"frobnicate":["quux"]}'                              > "$tmp/plan-alien.json"
echo 'not json'                                             > "$tmp/plan-broken.json"

# The mutant this kills: "the plan is the more specific evidence, so let it govern". The documented
# wiring ALWAYS passes --plan, so under that mutant the argv classifier is decorative and every one of
# the command lines below executes with no snapshot at all.
expect_classify 10 "a harmless plan does not clear volume delete" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" volume delete --yes
expect_classify 10 "a harmless plan does not clear service rm" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" service rm api
expect_classify 10 "a harmless plan does not clear environment rm" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" environment rm staging
expect_classify 11 "a harmless plan does not clear volume attach --volume" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" volume attach --volume mysql-data
expect_classify 11 "a harmless plan does not clear an unenumerated verb" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" redeploy
expect_classify 11 "a harmless plan does not clear an unknown verb" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" wibble
# The one place a plan is allowed to LOWER the verdict, and the reason --plan exists at all.
expect_classify  0 "a harmless plan still clears the config apply it was written for" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" config apply --plan "$tmp/plan-create.json"
# ... but not a config apply that also names a project-level volume.
expect_classify 11 "a harmless plan does not clear a config apply that names a volume" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" config apply --volume mysql-data

# End to end, in `run` mode: the snapshot must actually be taken before the command is allowed through.
fixture
expect_run 0 yes "a destructive argv with a harmless plan still snapshots before running" \
  --plan "$tmp/plan-create.json" -- "$tmp/fake-railway" volume delete --yes
case "${LAST_OUT:-}" in
  *"backup_id=BK-NEW"*) pass "... and the snapshot it took is the one it verified" ;;
  *) fail "... and the snapshot it took is the one it verified" "no backup id in output: ${LAST_OUT:-}" ;;
esac

# THE PLAN-CLASSIFIER CASES NAME THE PLAN ON BOTH SIDES, and that is not ceremony. It is the documented
# wiring, and since round 4 it is the only shape in which a pin clears an apply at all: a `config apply`
# naming no `--plan` re-evaluates the authoring file, so no pin the guard was handed describes what it is
# about to do, and the guard refuses. A fixture written in a shape the runbook never uses proves only that
# the author was consistent. A separate change
echo "== the plan classifier proves harmless before it permits =="

# Every one of these was HARMLESS under the blocklist the guard shipped with.
echo '{"actions":[{"type":"update","resource":"volume","sizeMB":1024}]}'                     > "$tmp/p1.json"
echo '{"changes":[{"resource":"volume","op":"-"},{"resource":"service","op":"create"}]}'     > "$tmp/p2.json"
echo '{"changes":[{"action":"DROP","target":"volume"},{"action":"add","target":"service"}]}' > "$tmp/p3.json"
echo '{"steps":[{"op":"teardown","kind":"volume"},{"op":"create","kind":"service"}]}'        > "$tmp/p4.json"
echo '{"volumes":[{"name":"mysql-data","destroyed":true,"note":"set by plan"}]}'             > "$tmp/p5.json"
echo '{"actions":[{"type":"detach","resource":"volume"}]}'                                   > "$tmp/p6.json"
echo '{"actions":[{"type":"frobnicate","resource":"service"},{"type":"create","resource":"x"}]}' > "$tmp/p7.json"
echo '{"actions":[{"type":"update","resource":"service","name":"api"}]}'                     > "$tmp/p8.json"

pc() { expect_classify "$1" "$2" --plan "$3" -- "$tmp/fake-railway" config apply --plan "$3"; }
pc 10 "a plan that resizes a volume is destructive, as volume update is on the argv path" "$tmp/p1.json"
pc 10 "terraform's \`-\` marks destruction even beside a create"                          "$tmp/p2.json"
pc 10 "an action spelled DROP is destruction"                                             "$tmp/p3.json"
pc 10 "an action spelled teardown is destruction"                                         "$tmp/p4.json"
pc 10 "a boolean \"destroyed\": true is seen, though .. | strings never sees it"          "$tmp/p5.json"
pc 10 "a plan that detaches a volume is destructive, as volume detach is on the argv path" "$tmp/p6.json"
pc 11 "an action value this guard has never seen is undecidable, not harmless"             "$tmp/p7.json"
pc  0 "an in-place change to a NON-volume resource is still harmless"                      "$tmp/p8.json"

expect_classify 10 "a plan proposing a delete is destructive" --plan "$tmp/plan-delete.json" -- "$tmp/fake-railway" config apply --plan "$tmp/plan-delete.json"
expect_classify 11 "a plan in an unrecognised schema cannot be judged" --plan "$tmp/plan-alien.json" -- "$tmp/fake-railway" config apply --plan "$tmp/plan-alien.json"
expect_classify 11 "an unreadable plan cannot be judged"     --plan "$tmp/plan-broken.json" -- "$tmp/fake-railway" config apply --plan "$tmp/plan-broken.json"
expect_classify 11 "a missing plan cannot be judged"         --plan "$tmp/nope.json" -- "$tmp/fake-railway" config apply --plan "$tmp/nope.json"
# The plan escalates: a read-only-looking argv with a destructive plan is destructive.
expect_classify 10 "a destructive plan outweighs a harmless-looking command line" \
  --plan "$tmp/plan-delete.json" -- "$tmp/fake-railway" status

# And end to end: a destructive plan must actually cause a snapshot.
fixture
rm -f "$SENTINEL" "$FIX/.listed"
set +e
out="$(bash "$GUARD" run --label "$GUARD_LABEL" --plan "$tmp/plan-delete.json" -- "$tmp/fake-railway" status 2>&1)"
got=$?
set -e
if [ "$got" -eq 0 ] && [ -f "$SENTINEL" ] && printf '%s' "$out" | grep -q 'backup_id=BK-NEW'; then
  pass "a destructive plan forces a snapshot before an otherwise harmless command"
else
  fail "a destructive plan forces a snapshot before an otherwise harmless command" \
       "exit=$got — $(printf '%s' "$out" | tr '\n' '|' | cut -c1-200)"
fi

echo "== a REAL Railway plan: Railway's own severity, not this file's opinion of English =="

# Every fixture in this section is the shape `railway config plan --out` actually writes, taken from a
# captured production plan (cliVersion 5.57.2): a self-describing envelope, `kind` as the action key,
# `type` carrying a DATA type, and a `severity` of `safe` or `destructive` on every change.

# THE CENTREPIECE. The real volume re-point: `openemr-volume-ceSx` swapped out for `openemr-sites` under
# a running service — `railway volume detach` by another name, which classify_argv calls DESTRUCTIVE.
# Railway marks it `severity: destructive`; the only verb in its summary is "Update". Under the round-2
# classifier this was `11 ... carries no recognisable action key`: safe by failing to parse, not by
# classifying, and DEPLOYMENT.md §10's promise that the two paths agree was false because of it.
cat > "$tmp/rp-detach.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":true,
 "diff":"~ Update openemr","claim":false,
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"service.openemr","kind":"resource.update","field":"volumeAttachments",
    "path":"resources.service.openemr.volumeAttachments","severity":"destructive",
    "deployEffect":"deploy",
    "summary":"Update openemr volumeAttachments.openemr-sites.mountPath, volumeAttachments.openemr-sites.volume, volumeAttachments.openemr-volume-ceSx.mountPath and 1 more",
    "before":{"openemr-volume-ceSx":{"mountPath":"/var/www/localhost/htdocs/openemr/sites","volume":"volume.openemr-volume-ceSx"}},
    "after":{"openemr-sites":{"mountPath":"/var/www/localhost/htdocs/openemr/sites","volume":"volume.openemr-sites"}}}]}}
JSON
expect_classify_says 10 "Railway marks 1 of 1" \
  "the real volumeAttachments re-point is DESTRUCTIVE, and by its severity" \
  --plan "$tmp/rp-detach.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-detach.json"

# Three more real entries are `severity: destructive` with "Update" as their only verb: config.region on
# mysql-data, postgres-data and dataprotection-keys. Nothing in the prose reaches them either.
cat > "$tmp/rp-region.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":true,
 "diff":"~ Update mysql-data\n~ Update postgres-data\n~ Update dataprotection-keys","claim":false,
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"volume.mysql-data","kind":"resource.update","field":"config","severity":"destructive",
    "path":"resources.volume.mysql-data.config","summary":"Update mysql-data config.region"},
   {"address":"volume.postgres-data","kind":"resource.update","field":"config","severity":"destructive",
    "path":"resources.volume.postgres-data.config","summary":"Update postgres-data config.region"},
   {"address":"volume.dataprotection-keys","kind":"resource.update","field":"config","severity":"destructive",
    "path":"resources.volume.dataprotection-keys.config","summary":"Update dataprotection-keys config.region"}]}}
JSON
expect_classify_says 10 "Railway marks 3 of 3" \
  "a real config.region move on three volumes is DESTRUCTIVE, though the only verb is Update" \
  --plan "$tmp/rp-region.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-region.json"

# A GENUINELY SAFE PLAN MUST BE ABLE TO COME BACK HARMLESS. It could not before: `kind` was not a
# recognised action key and `type`'s data-type values (`literal`, `image`, `service`) were not in the
# safe set, so every real plan — including a pure-create one — was UNDECIDABLE. That made the whole
# `--plan` exception inert and the documented apply an unconditional snapshot forever.
cat > "$tmp/rp-create.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":false,
 "diff":"+ Create service api","claim":false,
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"service.api","kind":"resource.create","deployEffect":"deploy","severity":"safe",
    "path":"resources.service.api","summary":"Create service api",
    "resource":{"address":"service.api","kind":"docker-image","name":"api","type":"service",
                "source":{"image":"ghcr.io/org/api:1","type":"image"},
                "variables":{"PORT":{"type":"literal","value":"8080"}}}},
   {"address":"service.api","kind":"variable.set","severity":"safe","variable":"LOG_LEVEL",
    "path":"resources.service.api.variables.LOG_LEVEL","summary":"Update variable api.LOG_LEVEL",
    "before":{"type":"preserve"},"after":{"type":"literal","value":"info"}}]}}
JSON
expect_classify_says 0 "Railway marks all 2 change(s) safe" \
  "a real pure-create plan is HARMLESS — the branch no real plan could reach" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-create.json"

# ... and that is still the ONLY verdict a plan may lower, so it clears `config apply` and nothing else.
expect_classify 10 "a real harmless plan still does not clear volume delete" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" volume delete --yes

# PROSE IS NOT THE EVIDENCE. This plan's changes are all `safe`; its `.diff` and its diagnostic message
# both narrate deletions — the real plan's own diagnostic is the sentence "Volumes are never deleted by
# config apply". A classifier reading English calls this DESTRUCTIVE; Railway calls it safe, and Railway
# owns the schema.
cat > "$tmp/rp-prose.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":false,
 "diff":"+ Create volume openemr-sites","claim":false,
 "changeSet":{"version":1,"declared":{},"diagnostics":[
   {"severity":"warning","path":"resources.volume.openemr-volume-ceSx",
    "message":"Volume openemr-volume-ceSx exists on Railway but is not declared in the config. Volumes are never deleted by config apply; delete it from the dashboard if that is intended."}],
  "changes":[
   {"address":"volume.openemr-sites","kind":"resource.create","severity":"safe",
    "path":"resources.volume.openemr-sites","summary":"Create volume openemr-sites"}]}}
JSON
expect_classify_says 0 "Railway marks all 1 change(s) safe" \
  "English narration about deletes does not override a plan Railway marks safe" \
  --plan "$tmp/rp-prose.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-prose.json"

# `changeSet.diagnostics[].severity` IS A DIFFERENT AXIS — lint severities, not change severities. A
# classifier that walked for any `.severity` would read `warning` here and refuse to judge a safe plan.
case "$(cat "$tmp/rp-prose.json")" in
  *'"severity":"warning"'*) pass "... and the fixture really does carry a diagnostics severity of its own" ;;
  *) fail "... and the fixture really does carry a diagnostics severity of its own" "fixture lost its diagnostic" ;;
esac

# RAILWAY'S OWN TOP-LEVEL FLAG IS READ AS A VALUE. `{"destructive": true}` was HARMLESS under round 2 —
# the invented `{"destroyed": true}` was caught and Railway's actual spelling was not.
cat > "$tmp/rp-flag.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":true,"claim":false,
 "diff":"~ Update api",
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"service.api","kind":"resource.update","field":"deploy","severity":"safe",
    "path":"resources.service.api.deploy","summary":"Update api deploy.startCommand"}]}}
JSON
expect_classify 10 "a plan Railway's own top-level destructive flag calls destructive is destructive" \
  --plan "$tmp/rp-flag.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-flag.json"

# ... and the flag disagreeing with the per-change severities means one of the two is being read wrongly.
cat > "$tmp/rp-contradict.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":false,"claim":false,
 "diff":"~ Update api",
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"volume.mysql-data","kind":"resource.update","field":"config","severity":"destructive",
    "path":"resources.volume.mysql-data.config","summary":"Update mysql-data config.region"}]}}
JSON
expect_classify 11 "a plan whose destructive flag contradicts its severities is not judged" \
  --plan "$tmp/rp-contradict.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-contradict.json"

# A SEVERITY OUTSIDE {safe, destructive} CANNOT BE ASSUMED HARMLESS — the allowlist is Railway's, and an
# addition to it is still news to this file.
cat > "$tmp/rp-newsev.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":false,"claim":false,
 "diff":"~ Update api",
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"service.api","kind":"resource.update","severity":"catastrophic","summary":"Update api"}]}}
JSON
expect_classify_says 11 "catastrophic" \
  "a severity Railway has added since is undecidable, and the verdict names it" \
  --plan "$tmp/rp-newsev.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-newsev.json"

cat > "$tmp/rp-nosev.json" <<'JSON'
{"kind":"railway.config.plan","version":1,"cliVersion":"5.57.2","destructive":false,"claim":false,
 "changeSet":{"version":1,"declared":{},"diagnostics":[],"changes":[
   {"address":"service.api","kind":"resource.update","summary":"Update api"}]}}
JSON
expect_classify 11 "a change carrying no severity at all is undecidable" \
  --plan "$tmp/rp-nosev.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-nosev.json"

# THE SCHEMA CHECK IS THE CLASSIFIER'S OWN GUARD. If Railway renames or drops either field the checks
# above would otherwise evaluate to "nothing destructive found" — permissive failure, silently.
echo '{"kind":"railway.config.plan","destructive":"no","changeSet":{"changes":[]}}' > "$tmp/rp-badflag.json"
expect_classify_says 11 "not a boolean" \
  "a plan whose .destructive stopped being a boolean is a schema change, not a safe plan" \
  --plan "$tmp/rp-badflag.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-badflag.json"
echo '{"kind":"railway.config.plan","destructive":false,"changeSet":{"changes":{"0":{"severity":"safe"}}}}' > "$tmp/rp-badchanges.json"
expect_classify_says 11 "not an array" \
  "a plan whose .changeSet.changes stopped being an array is a schema change, not a safe plan" \
  --plan "$tmp/rp-badchanges.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-badchanges.json"

# A DOCUMENT THAT DECLARES ITSELF A RAILWAY PLAN IS NEVER RE-READ AS PROSE. Falling back would give the
# round-2 answer — right for the captured plan, by regex, and wrong in both directions elsewhere.
expect_classify_says 11 "schema has changed" \
  "a malformed railway.config.plan stays undecidable instead of falling back to word-matching" \
  --plan "$tmp/rp-badflag.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-badflag.json"

echo "== the vocabulary fallback still covers documents that do not name themselves =="

# `kind` is Railway's action key, so a fragment lifted out of a plan carries it without the envelope.
echo '{"kind":"resource.update","field":"volumeAttachments","address":"service.openemr"}' > "$tmp/frag-detach.json"
expect_classify 10 "a plan FRAGMENT that re-points a volume is destructive on the fallback path" \
  --plan "$tmp/frag-detach.json" -- "$tmp/fake-railway" config apply --plan "$tmp/frag-detach.json"

# THE MUTANT THIS KILLS, and it was proposed as the one-word fix for the case above: adding `destructive`
# to `dre`. `vocab` matches object KEYS, and `destructive` is a top-level key on every `--out` document
# whatever its value — so that patch turns a plan whose own flag is `false` into DESTRUCTIVE on the
# strength of the key existing. No round-2 fixture carried the key at all, so nothing caught it.
echo '{"destructive":false,"actions":[{"type":"create","resource":"service"}]}' > "$tmp/frag-flag-false.json"
expect_classify 0 "a document whose own destructive flag is false is not destructive BECAUSE of the key" \
  --plan "$tmp/frag-flag-false.json" -- "$tmp/fake-railway" config apply --plan "$tmp/frag-flag-false.json"

echo "== the plan classified must be the plan applied =="

# `run` execs the guarded argv verbatim, --plan and all, and the documented wiring writes the filename
# twice on one line. Nothing compared them: `run --plan A -- railway config apply --plan B` classified A,
# found it harmless, took no snapshot, and applied B.
fixture
expect_run 2 no "a guarded command applying a DIFFERENT plan refuses" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-detach.json"
fixture
expect_run 2 no "... in the --plan=FILE spelling too" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --plan="$tmp/rp-detach.json"
fixture
expect_run 2 no "a guarded command that applies a plan the guard was never given refuses" \
  -- "$tmp/fake-railway" config apply --plan "$tmp/rp-detach.json"
# The same defect produces a wrong exit code in classify mode, where nothing is executed to notice it.
expect_classify 2 "classify refuses the mismatch too rather than answering about the wrong file" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-detach.json"

# Two spellings of one file are one file: the check must not become a brake on the documented wiring.
fixture
expect_run 0 yes "the documented wiring — the same plan named twice — still runs" \
  --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-create.json"
fixture
cp "$tmp/rp-create.json" "$tmp/same.json"
# THIS CASE USED TO RUN IN A SUBSHELL - `( cd "$tmp" && expect_run … )` - AND THAT MADE IT THE ONE CASE
# IN THIS FILE THAT COULD NOT REDDEN THE SUITE. `fail` records by incrementing `fails`, and a variable
# incremented inside `( … )` dies with the subshell, so a regression here printed a FAIL line and exited
# 0. The relative `--plan ./same.json` needs the cwd to be $tmp; save and restore it in THIS shell
# instead. Every other path in play ($GUARD, $FIX, $SENTINEL, the fake railway) is absolute, so the cd
# changes nothing else. Found by the assertion count added below, which read 100 while 101 `ok` lines
# printed - which is exactly the "no case ran" blindness the count exists to end. A separate change
__selftest_cwd="$PWD"; cd "$tmp"
expect_run 0 yes "the same file reached by a different spelling is the same file" \
  --plan "./same.json" -- "$tmp/fake-railway" config apply --plan "$tmp/same.json"
cd "$__selftest_cwd"

# AND THE FILE MUST NOT CHANGE UNDERNEATH THE VERDICT. A snapshot can take fifteen minutes; the transport
# stub rewrites the plan mid-snapshot, which is the window a shared runner really has.
fixture
cp "$tmp/rp-detach.json" "$tmp/mutating.json"
MUTATE_PLAN="$tmp/mutating.json" expect_run 2 no \
  "a plan rewritten between the verdict and the exec refuses rather than applying the new one" \
  --plan "$tmp/mutating.json" -- "$tmp/fake-railway" config apply --plan "$tmp/mutating.json"

# A PIN ONLY CLEARS AN APPLY THAT WILL ACTUALLY READ IT.
#
# `railway config apply` is the one command whose argv verdict a plan may LOWER, and the check above
# closes only half of that door: it refuses a DIFFERENT plan and refuses a plan the guard was never
# given, and returned 0 unchecked when the guarded command named NO plan. But a bare `config apply` does
# not apply nothing — the CLI's own help says `--plan <PLAN>  Apply this pinned plan WITHOUT
# re-evaluating the authoring file` (5.57.2), so dropping it re-evaluates `.railway/railway.ts` against
# production and applies whatever that computes. What it computes depends on the CLI, which nothing pins:
# measured on 2026-09-21, CLI 5.57.2 gives `resource.delete database.mysql` — the 1.3 GB OpenEMR database,
# reached through the guard, exiting 0 — plus the volumeAttachments re-point that sat beside it
# declared the volume each environment actually uses; CLI 5.59.0 gives `variable.delete mysql.MYSQL_URL`
# instead, until a separate change declared that variable. Both are destructive and one door closing does not close this one. The FIXTURES below stay on
# 5.57.2 deliberately, because they test the guard rather than the environment — it is the claim about
# PRODUCTION that is version-dependent. Latent until round 3 made HARMLESS reachable at all.
#
# The way in is mundane: the guard's likeliest real exercise is an operator's
# hand apply, which is exactly where the second `--plan` gets dropped.
# DEPLOYMENT.md §10
echo "== a pinned plan only clears an apply that will READ it =="

fixture
expect_run 2 no "a guarded config apply naming NO --plan refuses: it re-evaluates the authoring file"   --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --yes
fixture
expect_run 2 no "... and --file re-evaluates too, so it does not qualify either"   --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply --file .railway/railway.ts --yes
expect_classify 2 "classify refuses it too rather than answering about a document nobody will read"   --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply

# The other direction stays open: with no --plan at ALL the guard still judges, and UNDECIDABLE still
# snapshots. Refusing here instead would make the guard a brake on an apply nobody pinned.
fixture
expect_run 0 yes "a config apply with no plan on either side still snapshots and runs"   -- "$tmp/fake-railway" config apply --yes

# AMBIGUITY REFUSES. `--plan <PLAN>` is a single-value clap argument (`ArgAction::Set`, which overwrites),
# so the CLI reads the LAST one while a first-wins reader vouches for the first. Both named files here
# pass the identity check individually; the guard must not pick.
fixture
expect_run 2 no "two --plan arguments refuse rather than vouching for the one the CLI will not read"   --plan "$tmp/rp-create.json" -- "$tmp/fake-railway" config apply   --plan "$tmp/rp-create.json" --plan "$tmp/rp-detach.json"

# PATH RESOLUTION, NOT THE BASENAME. Every other identity fixture differs in basename or not at all, so
# `canonical_path` reduced to `basename` passes all of them. Two different documents, one filename.
mkdir -p "$tmp/d1" "$tmp/d2"
cp "$tmp/rp-create.json" "$tmp/d1/plan.json"
cp "$tmp/rp-detach.json" "$tmp/d2/plan.json"
fixture
expect_run 2 no "the same filename in two directories is two plans, not one"   --plan "$tmp/d1/plan.json" -- "$tmp/fake-railway" config apply --plan "$tmp/d2/plan.json"

# THE ESCALATION LATTICE HAS A SECOND CORNER. "A destructive plan forces a snapshot before an otherwise
# harmless command" was pinned; its UNDECIDABLE twin was not, so ranking UNDECIDABLE as 0 in `severity()`
# passed the whole suite while letting a plan whose schema this guard cannot read be waved through beside
# a read-only verb.
expect_classify 11 "an UNDECIDABLE plan is not cleared by a harmless command line"   --plan "$tmp/rp-badflag.json" -- "$tmp/fake-railway" status

# A CLASSIFIER THAT PRODUCES NO VERDICT HAS NOT PRODUCED A HARMLESS ONE. The `*)` fallback was carried
# over unexercised from round 2, so returning EX_OK there passed every case. This document reaches it for
# real: it declares itself a plan and carries a destructive change whose `address` is an object, which the
# jq program cannot concatenate into a name, so jq exits non-zero and no verdict comes back.
echo '{"kind":"railway.config.plan","destructive":true,"changeSet":{"changes":[{"kind":"resource.delete","address":{"service":"mysql"},"severity":"destructive"}]}}' > "$tmp/rp-noverdict.json"
expect_classify_says 11 "produced no verdict"   "a plan the classifier cannot produce a verdict for is UNDECIDABLE, not harmless"   --plan "$tmp/rp-noverdict.json" -- "$tmp/fake-railway" config apply --plan "$tmp/rp-noverdict.json"

echo "== permissions (the guard must not be a brake on everything) =="

fixture
expect_run 0 yes "a verified snapshot lets the destructive command through" \
  -- "$tmp/fake-railway" volume delete --yes
case "${LAST_OUT:-}" in
  *"backup_id=BK-NEW"*) pass "the new backup id is printed for the job log" ;;
  *) fail "the new backup id is printed for the job log" "not in output: ${LAST_OUT:-}" ;;
esac
case "${LAST_OUT:-}" in
  *"VI-mysql-b"*) fail "only the target environment is snapshotted" "the other environment's instance was touched" ;;
  *) pass "only the target environment is snapshotted" ;;
esac

# A harmless command must not call the API at all — proven by making every API call fail.
fixture
rm -f "$FIX/projectToken.json"
expect_run 0 yes "a read-only command runs without taking a snapshot" -- "$tmp/fake-railway" status

# A guarded argv can carry a secret; a job log is not a place to put one. `variables --set` is not a
# read, so this also proves the carve-out from READ_ONLY_RE end to end: it snapshots first, then runs.
fixture
expect_run 0 yes "variables --set is snapshotted rather than waved through" \
  -- "$tmp/fake-railway" variables --set "FOO=hunter2-the-real-value"
# Only the guard's own log line is under test; the fake CLI echoes its argv on purpose.
guard_line="$(printf '%s\n' "${LAST_OUT:-}" | grep '^GUARD running:' || true)"
case "$guard_line" in
  *"hunter2-the-real-value"*) fail "a KEY=VALUE argument is redacted from the job log" "the value reached the log: $guard_line" ;;
  *"FOO=***"*)                pass "a KEY=VALUE argument is redacted from the job log" ;;
  *)                          fail "a KEY=VALUE argument is redacted from the job log" "no redacted form in: $guard_line" ;;
esac

echo "== snapshot mode (no guarded command — it snapshots, or it refuses) =="

fixture
expect_snapshot 0 "snapshot mode takes and verifies a snapshot of the whole environment"
case "${LAST_OUT:-}" in
  *"backup_id=BK-NEW"*) pass "snapshot mode prints the verified backup id" ;;
  *) fail "snapshot mode prints the verified backup id" "not in output: ${LAST_OUT:-}" ;;
esac

fixture
expect_snapshot 0 "snapshot --volume resolving to exactly one instance is permitted" --volume mysql-data

# ASSERTS THE MESSAGE, for the same reason its SEVERAL-instances sibling below already does: an
# exit-code-only check here is backstopped by resolve_instances' OWN generic "no READY volume instances
# found" refusal once the name filter empties $rows, so it stayed green with the --volume name-resolution
# count check ([ "$count" = "1" ]) deleted outright. A separate change
fixture
expect_snapshot_says 2 "resolves to 0 volume instances" \
  "snapshot --volume resolving to NO instance refuses" --volume not-a-volume

fixture
volumes_json_duplicate_names > "$FIX/volumes.json"
# ASSERTS THE MESSAGE. With only an exit-code assertion this passed whether or not the resolution
# check existed: removing it let the run proceed and refuse at "a backup named ... already exists"
# instead, which is also exit 2 and looks identical. A separate change
expect_snapshot_says 2 "resolves to 2 volume instances" "snapshot --volume resolving to SEVERAL instances refuses rather than picking one" \
  --volume mysql-data

fixture
echo '{"data":{"volumeInstanceBackupList":[]}}' > "$FIX/backups-2.json"
expect_snapshot 2 "snapshot mode refuses when the workflow completes without producing a backup"

echo "== the data-refreshable declaration: what the guard does with no scheduled backups =="

# THE EXACT FIXTURE THE ISSUE ASKS ABOUT, RUN IN BOTH DIRECTIONS. A volume 4000MB into 5120MB with no
# existing backup is past Railway's 50% manual-backup cap, which is the state every volume in an
# environment reaches once the DAILY schedules that used to fund the first backup are gone. The pair
# below is the demonstration: the SAME plan, the SAME volume, the SAME command, and the only thing that
# differs is the declaration.
#
# POSITIVE: declared refreshable — the apply goes through, and the mutation is never sent.
fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE"
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 0 yes "declared data-refreshable" \
  "a destructive command against a volume PAST the 50% cap is permitted where the data is declared refreshable" \
  -- "$tmp/fake-railway" volume delete --yes
expect_request no 'volumeInstanceBackupCreate' "the permitted run took no backup at all — not merely a backup it then ignored"
expect_request no 'volumeInstanceBackupList'   "it did not even read the backup list, because it was never going to take one"
case "${LAST_OUT:-}" in
  *"synthetic demo data, re-seedable"*) pass "the declared REASON reaches the job log, so an operator can tell a decision from a broken guard" ;;
  *) fail "the declared REASON reaches the job log, so an operator can tell a decision from a broken guard" "not in output: ${LAST_OUT:-}" ;;
esac

# NEGATIVE, AND THIS IS THE HALF THAT PROVES THE POSITIVE MEANS ANYTHING. Same fixture, declaration says
# false: the cap still bites, the guard still refuses, and the destructive command still does not run.
fixture
declare_refreshable "$DECL_ENV_A_NOT"
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "the SAME volume past the SAME cap still refuses when the environment is declared NOT refreshable" \
  -- "$tmp/fake-railway" volume delete --yes

# ... and every other way the declaration can fail to say "yes" lands in that same negative direction.
fixture
declare_refreshable "$DECL_ENV_B_REFRESHABLE"
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "a declaration naming a DIFFERENT environment does not excuse this one" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
declare_refreshable '{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"stub","refreshable":"true","reason":"typed as a string"}]}'
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "refreshable: \"true\" as a STRING is not the boolean true" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
declare_refreshable 'not json at all'
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "an unparseable declaration is read as NOT refreshable" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
declare_refreshable '{"environments":[{"environmentId":"ENV-A","refreshable":true}]}'
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "a declaration that does not name its kind is read as NOT refreshable" \
  -- "$tmp/fake-railway" volume delete --yes

fixture
declare_refreshable '{"kind":"agentforge.railway.data-refreshable","environments":[{"environmentId":"ENV-A","name":"one","refreshable":true,"reason":"r1"},{"environmentId":"ENV-A","name":"two","refreshable":true,"reason":"r2"}]}'
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_run_says 2 no "50% of the volume size" \
  "an environment declared TWICE refuses rather than picking a declaration" \
  -- "$tmp/fake-railway" volume delete --yes

echo "== a refreshable environment gives up the BACKUP and nothing else =="

# THE WHOLE ARGUMENT FOR THIS SHAPE over deleting the snapshot half outright is that the guard's other
# checks are worth keeping. That is a claim, so it is a case: each of these is a refusal that has
# nothing to do with backups, asserted with the declaration saying "take none".
fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE"
echo '{"kind":"railway.config.plan","destructive":false,"changeSet":{"changes":[]}}' > "$tmp/r-plan-a.json"
echo '{"kind":"railway.config.plan","destructive":false,"changeSet":{"changes":[]}}' > "$tmp/r-plan-b.json"
expect_run_says 2 no "A verdict about one plan is not a verdict about another" \
  "plan identity still refuses in a refreshable environment" \
  --plan "$tmp/r-plan-a.json" -- "$tmp/fake-railway" config apply --plan "$tmp/r-plan-b.json"

fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE"
expect_run_says 2 no "names no --plan of its own" \
  "an apply that will RE-EVALUATE the authoring file still refuses in a refreshable environment" \
  --plan "$tmp/r-plan-a.json" -- "$tmp/fake-railway" config apply --yes

# THE FINGERPRINT, and it needs its own hook: the plan is normally rewritten mid-snapshot by the backup
# mutation, which is precisely the call this path never makes. MUTATE_PLAN_EARLY fires on the first
# call the guard makes instead, so the window between classification and exec is still exercised.
#
# THE PLAN HAS TO BE DESTRUCTIVE for this case to mean anything: a harmless one never reaches
# do_snapshot, so nothing would call the stub, nothing would rewrite the file, and the case would pass
# on a green exec without ever testing the fingerprint. A separate change
fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE"
echo '{"kind":"railway.config.plan","destructive":true,"changeSet":{"changes":[{"kind":"resource.delete","address":"database.mysql","severity":"destructive"}]}}' > "$tmp/r-plan-mut.json"
MUTATE_PLAN_EARLY="$tmp/r-plan-mut.json" \
  expect_run_says 2 no "changed between being classified and being applied" \
  "a plan rewritten after classification still refuses in a refreshable environment" \
  --plan "$tmp/r-plan-mut.json" -- "$tmp/fake-railway" config apply --plan "$tmp/r-plan-mut.json"
unset MUTATE_PLAN_EARLY

# And `snapshot` mode — the shape GitHub's apply job uses — honours the same declaration rather than
# reading an explicit request as an override. Since a separate change production is declared refreshable too, so
# that job takes no backup either; this asserts the behaviour, not the policy.
fixture
declare_refreshable "$DECL_ENV_A_REFRESHABLE"
volumes_json 5120 4000 > "$FIX/volumes.json"
expect_snapshot_says 0 "declared data-refreshable" "snapshot mode takes none in a declared-refreshable environment"
expect_request no 'volumeInstanceBackupCreate' "snapshot mode sent no backup mutation there either"

echo "== classification =="

expect_classify 10 "volume delete is destructive"                -- "$tmp/fake-railway" volume delete --yes
expect_classify 10 "volume detach is destructive"                -- "$tmp/fake-railway" volume detach
expect_classify 10 "environment delete is destructive"           -- "$tmp/fake-railway" environment delete
expect_classify 10 "a restore is destructive"                    -- "$tmp/fake-railway" volume restore
expect_classify  0 "config plan is harmless"                     -- "$tmp/fake-railway" config plan
expect_classify  0 "status is harmless"                          -- "$tmp/fake-railway" status
expect_classify 11 "config apply with no plan cannot be judged"  -- "$tmp/fake-railway" config apply
expect_classify 11 "naming a project-level volume cannot be judged" -- "$tmp/fake-railway" redeploy -v mysql-data
expect_classify 11 "an unknown verb is not assumed harmless"     -- "$tmp/fake-railway" wibble

# `domain` and `variables` read; `domain delete` and `variables --set` do not.
expect_classify  0 "variables on its own is a read"              -- "$tmp/fake-railway" variables
expect_classify  0 "domain on its own is a read"                 -- "$tmp/fake-railway" domain
expect_classify 11 "domain delete is not a read"                 -- "$tmp/fake-railway" domain delete api.example.com
expect_classify 11 "variables delete is not a read"              -- "$tmp/fake-railway" variables delete FOO
expect_classify 11 "variables --set is not a read"               -- "$tmp/fake-railway" variables --set FOO=bar

echo
# Raise this when you add a case. A mismatch is a red suite, deliberately: a suite that quietly stopped
# running a third of its cases is the failure this number exists to make visible.
EXPECTED_ASSERTIONS=120
if [ "$asserts" -ne "$EXPECTED_ASSERTIONS" ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  ran %s assertion(s), expected %s — a case stopped firing, or one was added without updating EXPECTED_ASSERTIONS.\n' \
    "$asserts" "$EXPECTED_ASSERTIONS" >&2
  exit 1
fi
if [ "$fails" -ne 0 ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  %s of %s assertion(s)\n' "$fails" "$asserts" >&2
  exit 1
fi
printf '\033[32mSELF-TEST PASSED\033[0m  %s of %s assertions — the snapshot guard refuses and permits as specified\n' \
  "$asserts" "$asserts"
