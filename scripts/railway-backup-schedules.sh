#!/usr/bin/env bash
# railway-backup-schedules.sh — hold each environment's volume backup schedules at the state that
# environment has DECLARED, and PROVE the change afterwards.
#
#   scripts/railway-backup-schedules.sh check  [--volume NAME]...
#   scripts/railway-backup-schedules.sh apply  [--volume NAME]...
#   scripts/railway-backup-schedules.sh remove [--volume NAME]...
#
# THE DECLARATION DECIDES WHICH OF THESE IS THE RIGHT ONE, and it is not a matter of taste.
# `scripts/railway-data-refreshable.json` says, per environment, in a field of its own — `schedules` —
# whether that environment should carry scheduled volume backups. Where it says `"none"`, `remove` is the
# operation the environment is allowed to have done to it, `check` expects no schedule and reports one it
# finds as DRIFT, and `apply` REFUSES rather than quietly contradicting a written decision. Where it says
# `"daily"`, every particular is the other way round.
#
# THIS FILE READS `schedules`, AND `refreshable` ONLY TO WARN. That separation is the whole correction
# the review forced. `refreshable` answers a different question — may `railway-snapshot-guard.sh`
# skip its pre-destruction backup — and production answered the two differently: no
# schedules (the ruling covered both environments) but still snapshot me (its fill had never been
# measured, and the guard was the only control in front of `railway-apply-production`). Since a separate change
# (2026-09-23) it is declared refreshable as well, but the questions stay independent. Driving this file from `refreshable` made
# `check` demand a DAILY schedule on every production volume and point the operator at the `apply` that
# re-creates exactly what the ruling retired — an answer nobody wrote, inherited from the other
# question's.
#
# THE COUPLING THAT MAKES `remove` MORE THAN BOOKKEEPING, and it is why `refreshable` is still read here:
# a schedule is also what keeps the snapshot guard's MANUAL backup takeable, because Railway caps a
# manual backup at 50% of the volume and the cap bites the FIRST one (see below). So in an environment
# that is NOT declared refreshable, `remove` says out loud that it is unfunding that first manual backup.
# It does not refuse — the declaration is where that trade was decided, and refusing would contradict it
# — but an operator should meet the consequence in the log rather than discover it later as a deploy
# that will not run.
#
# WHY THIS EXISTS, AND WHY IT IS A SCRIPT RATHER THAN A RUNBOOK LINE
#
# Production's database was emptied on 2026-09-18 and `volumeInstanceBackupList` returned an empty
# array: no scheduled backups, no manual ones, nothing to restore. The remedy was written down as
# operator setup — "Railway service -> Backups tab -> Daily" — and operator setup with no
# representation in source is the category of configuration this repository has repeatedly found
# missing months later, because its absence fails nothing. A schedule nobody set looks exactly like a
# schedule nobody needed. A separate change, DEPLOYMENT.md §10
#
# IT IS ALSO A PRECONDITION FOR THE SNAPSHOT GUARD, WHICH IS THE NON-OBVIOUS PART. Railway caps a
# MANUAL backup at 50% of the volume's size, and because backups are incremental the cap bites the
# FIRST one — the full copy. `railway-snapshot-guard.sh` refuses rather than proceeding when it hits
# that, so a volume more than half full with no backup at all makes every guarded apply refuse. One
# scheduled backup removes the cap for every manual one after it. The schedule is therefore not
# merely a floor under the guard, it is what keeps the guard from being dead on arrival.
# reference: DEPLOYMENT.md §10
#
# AND THAT IS WHY RETIRING THE SCHEDULES WAS NOT A ONE-LINE DECISION. The maintainer ruled on 2026-09-22
# that neither environment gets scheduled backups, because the data is not critical and can be
# refreshed — a statement about data PROTECTION, which is only one of the two jobs above. The other was
# still load-bearing and would have become unfunded. `scripts/railway-data-refreshable.json` is where
# both halves are settled, in two fields rather than one: staging needs neither the schedule NOR the
# guard's manual snapshot, production needed no schedule and still got the snapshot
# declared it refreshable too, and each half carries the argument written for it.
#
# THE GUARD COVERS DELIBERATE OPERATIONS; A SCHEDULE COVERED EVERYTHING ELSE. Neither substitutes for
# the other: state lost to something that was never a Railway operation at all is invisible to a wrapper
# around `railway config apply`. NOTHING COVERS THAT NOW, in either environment, and that is the shape of
# the ruling rather than an oversight — the answer to "what if the data is lost by some other route" is
# "re-seed it". Read that as the standing position, not as a gap to be filled quietly.
#
# WHAT RAILWAY OFFERS, established by introspecting the live API on 2026-09-22 rather than assumed:
#
#   query    volumeInstanceBackupScheduleList(volumeInstanceId: String!): [VolumeInstanceBackupSchedule!]
#   mutation volumeInstanceBackupScheduleUpdate(volumeInstanceId: String!,
#                                               kinds: [VolumeInstanceBackupScheduleKind!]!): Boolean!
#
# Two properties shape the whole file:
#
#   1. `kinds` IS THE WHOLE SET, NOT AN EDIT TO IT. The mutation REPLACES the schedules on that volume
#      instance with exactly the list it is given, so passing [DAILY] to a volume that also carries
#      WEEKLY silently removes the weekly one — and passing [] to that volume removes BOTH. Every
#      mutating verb here therefore reads the existing kinds first and sends arithmetic on them:
#      `apply` sends their UNION with DAILY, `remove` sends them MINUS DAILY. Each touches exactly the
#      one kind it is named for and neither disturbs the rest.
#   2. IT IS SYNCHRONOUS AND RETURNS A BOOLEAN, not a WorkflowId — unlike `volumeInstanceBackupCreate`.
#      So there is nothing to poll. There is still something to PROVE: this file re-reads
#      `volumeInstanceBackupScheduleList` afterwards and fails if it does not show what was asked for —
#      DAILY present after `apply`, DAILY gone after `remove`. `true` is the API's word for it; the
#      list is the evidence, and the distinction between the two is what the snapshot guard was
#      written around.
#
# A PROJECT TOKEN CAN DO BOTH, measured 2026-09-22 against fearless-abundance/staging under
# RAILWAY_TOKEN_STAGING. That mattered enough to check first: the same credential cannot read
# `workflowStatus` at all.
#
# FAILS CLOSED, and `check` fails closed in the direction that costs nothing: a volume whose schedule
# list it cannot read is reported WRONG, never assumed to be in the intended state. That holds in BOTH
# directions now — "I could not read it" is not "as declared" any more than it was "present".
#
# `check` CAN STILL FAIL, WHICH IS THE WHOLE REASON IT IS WORTH RUNNING. Retiring the schedules turned
# absence into the intended state in both environments, and a check whose expected state is "nothing" and
# whose every other answer is a shrug is a check that can never go red. So the expectation flips with the
# declaration rather than going away: where `schedules` is `"none"`, a DAILY that someone set in the
# dashboard is DRIFT and reported as such.

set -euo pipefail

# --- exit codes -------------------------------------------------------------------------------------
#   0  every named volume is in the state its environment's declaration calls for
#   1  usage or environment error (missing jq, missing credential)
#   2  the API refused, an apply/remove did not take effect, or the operation contradicts the declaration
#   3  `check` only: the API answered and the schedules do NOT match the declaration — a DAILY missing
#      where one is expected, a DAILY present where none should be, or a list that could not be read
EX_OK=0; EX_ENV=1; EX_FAILED=2; EX_MISMATCH=3

ENDPOINT="${RAILWAY_BACKUP_SCHEDULES_ENDPOINT:-https://backboard.railway.com/graphql/v2}"

red()  { printf '\033[31m%s\033[0m %s\n' "$1" "$2" >&2; }
note() { printf '%s %s\n' "$1" "$2" >&2; }

die_env()    { red "CANNOT RUN" "$1"; exit "$EX_ENV"; }
die_failed() { red "REFUSED" "$1"; exit "$EX_FAILED"; }

# Does the kinds list carry DAILY? A HERE-STRING AND A COUNT, never `printf | grep -q`: under pipefail
# grep -q exits at its first match, printf can die of SIGPIPE writing the rest, and the 141 reads as
# "no DAILY" - so `remove` reported a schedule gone that was still there. No count at all (the
# here-string's temp file could not be made, or grep did not answer) is a failure, not an answer.
has_daily() { # has_daily <kinds, one per line>
  local n
  n="$(grep -cx 'DAILY' <<<"$1")" || true
  case "$n" in
    ''|*[!0-9]*) die_failed "could not search the schedule kinds for DAILY - grep returned no count, which is not a 'no'." ;;
  esac
  [ "$n" -gt 0 ]
}

command -v jq >/dev/null 2>&1 \
  || die_env "jq is required and is not on PATH. The .railway CI job installs it (apk add jq); a workstation must too."
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" \
  || die_env "scripts/lib/jq-crlf.sh refused to load."

# The same reader `railway-snapshot-guard.sh` uses, so the two can never disagree about which
# environment holds throwaway data.
[ -f "$(dirname "${BASH_SOURCE[0]}")/railway-data-refreshable.sh" ] \
  || die_env "scripts/railway-data-refreshable.sh is missing; this script cannot read the per-environment data-refreshable declaration without it."
# shellcheck source=scripts/railway-data-refreshable.sh
. "$(dirname "${BASH_SOURCE[0]}")/railway-data-refreshable.sh"

# --- transport --------------------------------------------------------------------------------------
# One seam, so the self-test can drive every branch without a network or a Railway account. Same
# contract as railway-snapshot-guard.sh: a command that reads the request body on stdin and writes the
# response on stdout.
#
# AUTH IS NOT ONE HEADER, and this is the same trap the snapshot guard documents: a Railway PROJECT
# token authenticates with `Project-Access-Token`, a personal or team token with `Authorization:
# Bearer`. Sending a project token as a Bearer credential answers `Not Authorized`, which reads like a
# permissions problem and is a header problem.
gql() { # gql <query> <variables-json>
  local body
  body="$(jq -n --arg q "$1" --argjson v "$2" '{query: $q, variables: $v}')"
  if [ -n "${RAILWAY_BACKUP_SCHEDULES_TRANSPORT:-}" ]; then
    printf '%s' "$body" | $RAILWAY_BACKUP_SCHEDULES_TRANSPORT
    return
  fi
  local auth_header
  if [ -n "${RAILWAY_TOKEN:-}" ]; then
    auth_header="Project-Access-Token: ${RAILWAY_TOKEN}"
  elif [ -n "${RAILWAY_API_TOKEN:-}" ]; then
    auth_header="Authorization: Bearer ${RAILWAY_API_TOKEN}"
  else
    printf 'no credential: set RAILWAY_TOKEN (project token) or RAILWAY_API_TOKEN (personal/team token).\n' >&2
    return 1
  fi
  curl -sS --fail-with-body -X POST "$ENDPOINT" \
    -H "$auth_header" -H 'Content-Type: application/json' --data-binary "$body"
}

# Prints the response, or returns 1 with the reason on stderr. A GraphQL error is an HTTP 200 carrying
# an `errors` array, so status alone proves nothing — and a pipeline would report jq's opinion of an
# error document as success, which is why every result below is captured before it is parsed.
gql_checked() { # gql_checked <what> <query> <variables-json>
  local what="$1" out
  if ! out="$(gql "$2" "$3" 2>&1)"; then
    printf '%s: the API call failed - %s\n' "$what" "$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-300)" >&2
    return 1
  fi
  if ! printf '%s' "$out" | jq -e . >/dev/null 2>&1; then
    printf '%s: the API returned something that is not JSON - %s\n' "$what" "$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-200)" >&2
    return 1
  fi
  if printf '%s' "$out" | jq -e '(.errors // []) | length > 0' >/dev/null 2>&1; then
    printf '%s: %s\n' "$what" "$(printf '%s' "$out" | jq -r '[.errors[].message] | join("; ")')" >&2
    return 1
  fi
  printf '%s' "$out"
}

# --- target resolution ------------------------------------------------------------------------------
# THE CREDENTIAL PICKS THE ENVIRONMENT, NOT A NAME — identical reasoning to the snapshot guard, and for
# the same reason: a Volume is PROJECT-scoped, so another environment's volume of the same name
# resolves perfectly well and names the wrong disk.
resolve_scope() {
  if [ -n "${RAILWAY_TOKEN:-}" ]; then
    local out pid eid
    out="$(gql_checked "reading the project token's scope" 'query { projectToken { projectId environmentId } }' '{}')" \
      || die_failed "could not read the project token's scope."
    pid="$(printf '%s' "$out" | jq -r '.data.projectToken.projectId // empty')"
    eid="$(printf '%s' "$out" | jq -r '.data.projectToken.environmentId // empty')"
    [ -n "$pid" ] && [ -n "$eid" ] || die_failed "the project token did not report a project and environment."
    if [ -n "${RAILWAY_ENVIRONMENT_ID:-}" ] && [ "$RAILWAY_ENVIRONMENT_ID" != "$eid" ]; then
      die_failed "RAILWAY_ENVIRONMENT_ID=$RAILWAY_ENVIRONMENT_ID but the token is scoped to $eid. Refusing rather than guessing which one you meant."
    fi
    PROJECT_ID="$pid"; ENVIRONMENT_ID="$eid"
  else
    PROJECT_ID="${RAILWAY_PROJECT_ID:-}"; ENVIRONMENT_ID="${RAILWAY_ENVIRONMENT_ID:-}"
    [ -n "$PROJECT_ID" ] && [ -n "$ENVIRONMENT_ID" ] \
      || die_env "with a personal token, set RAILWAY_PROJECT_ID and RAILWAY_ENVIRONMENT_ID. Ids, not names."
  fi
}

VOLUME_GRAPH_QUERY='query($pid: String!) { project(id: $pid) { name volumes { edges { node { id name volumeInstances { edges { node { id environmentId mountPath state } } } } } } } }'

# Prints "instanceId <TAB> volumeName <TAB> mountPath" per READY volume instance in the environment.
resolve_instances() {
  local out rows
  out="$(gql_checked "reading the project's volume graph" "$VOLUME_GRAPH_QUERY" \
    "$(jq -n --arg pid "$PROJECT_ID" '{pid: $pid}')")" || die_failed "could not read the project's volume graph."
  rows="$(printf '%s' "$out" | jq -r --arg env "$ENVIRONMENT_ID" '
    .data.project.volumes.edges[].node as $v
    | $v.volumeInstances.edges[].node
    | select(.environmentId == $env)
    | select(.state == "READY")
    | [.id, $v.name, .mountPath]
    | @tsv')"
  rows="$(printf '%s\n' "$rows" | grep . || true)"
  [ -n "$rows" ] || die_failed "no READY volume instances found in environment $ENVIRONMENT_ID."
  printf '%s\n' "$rows"
}

SCHEDULE_LIST_QUERY='query($id: String!) { volumeInstanceBackupScheduleList(volumeInstanceId: $id) { id kind cron } }'
SCHEDULE_UPDATE_MUTATION='mutation($id: String!, $kinds: [VolumeInstanceBackupScheduleKind!]!) { volumeInstanceBackupScheduleUpdate(volumeInstanceId: $id, kinds: $kinds) }'

# The schedule kinds on one instance, one per line. Returns 1 if the list could not be read — the
# caller reports that as MISSING rather than as present, which is the direction that costs nothing.
schedule_kinds() { # schedule_kinds <instance-id>
  local out
  out="$(gql_checked "listing backup schedules for volume instance $1" "$SCHEDULE_LIST_QUERY" \
    "$(jq -n --arg id "$1" '{id: $id}')")" || return 1
  printf '%s' "$out" | jq -r '.data.volumeInstanceBackupScheduleList[]?.kind // empty'
}

# --- the two modes ------------------------------------------------------------------------------------
# WANT_VOLUMES empty means "every READY volume instance in this environment". Naming volumes narrows it,
# and a name that matches nothing is an ERROR rather than a silent no-op: `--volume mysql_data` for
# `mysql-data` would otherwise report success having checked nothing.
selected_rows() {
  local rows want hit=0 vi name mount out=""
  rows="$(resolve_instances)"
  if [ "${#WANT_VOLUMES[@]}" -eq 0 ]; then printf '%s\n' "$rows"; return 0; fi
  for want in "${WANT_VOLUMES[@]}"; do
    hit=0
    while IFS=$'\t' read -r vi name mount; do
      [ -n "${vi:-}" ] || continue
      if [ "$name" = "$want" ]; then out="$out$vi	$name	$mount"$'\n'; hit=$((hit + 1)); fi
    done <<EOF
$rows
EOF
    [ "$hit" -gt 0 ] || die_failed "no READY volume instance named '$want' in environment $ENVIRONMENT_ID. Check the name against \`railway volume list\`, and remember a volume is project-scoped while an instance is not."
  done
  printf '%s' "$out"
}

# THE EXPECTED STATE COMES FROM THE DECLARATION, NOT FROM THIS FILE'S OPINION. Both directions are
# failures, and both print the same exit 3 — what changes is which one is wrong and what fixes it.
# An unreadable list is a failure under either expectation: "I could not see it" has never been
# evidence of anything, and reading it as "as declared" would be the permissive direction that the
# 2026-09-18 incident already paid for once.
do_check() {
  local rows vi name mount kinds wrong=0 total=0 want_daily=1
  # THE `schedules` FIELD, NOT `refreshable`. Reading the latter here is the defect caught: it
  # made `check` demand DAILY on every production volume, where the ruling says none belongs.
  data_schedules_resolve "$ENVIRONMENT_ID"
  if [ "$DATA_SCHEDULES" = "none" ]; then
    want_daily=0
    note "DECLARED" "no scheduled backups: $DATA_SCHEDULES_WHY"
    note "DECLARED" "expecting NO backup schedule on any volume instance."
  else
    note "DECLARED" "scheduled backups expected: $DATA_SCHEDULES_WHY"
    note "DECLARED" "expecting a DAILY schedule on every volume instance."
  fi
  rows="$(selected_rows)"
  while IFS=$'\t' read -r vi name mount; do
    [ -n "${vi:-}" ] || continue
    total=$((total + 1))
    if ! kinds="$(schedule_kinds "$vi")"; then
      note "UNREADABLE" "$name ($mount) — its schedule list could not be read, which is never reported as the intended state."
      wrong=$((wrong + 1)); continue
    fi
    if [ "$want_daily" -eq 1 ]; then
      if has_daily "$kinds"; then
        note "OK     " "$name ($mount) — kinds: $(printf '%s\n' "$kinds" | grep . | tr '\n' ' ')"
      else
        note "MISSING" "$name ($mount) — no DAILY schedule. Kinds present: ${kinds:-<none>}"
        wrong=$((wrong + 1))
      fi
    else
      # DRIFT. A schedule where the declaration says none belongs was set by hand in the dashboard, or
      # by an `apply` that predates the declaration; either way the live state and the written decision
      # disagree, and that is worth a red exit whichever of the two is right. It is the only POSITIVE
      # finding this branch can make — an unreadable list reddens above, under either expectation.
      if [ -n "$(printf '%s\n' "$kinds" | grep . || true)" ]; then
        note "DRIFT  " "$name ($mount) — carries a schedule the declaration says it should not: $(printf '%s\n' "$kinds" | grep . | tr '\n' ' ')"
        wrong=$((wrong + 1))
      else
        note "OK     " "$name ($mount) — no schedule, as declared."
      fi
    fi
  done <<EOF
$rows
EOF
  [ "$total" -gt 0 ] || die_failed "nothing was checked."
  if [ "$wrong" -gt 0 ]; then
    if [ "$want_daily" -eq 1 ]; then
      red "BACKUP SCHEDULES" "$wrong of $total volume instance(s) in environment $ENVIRONMENT_ID do not carry the expected DAILY schedule."
      # THE DECLARATION FIRST, apply SECOND — mirroring the DRIFT branch below. Both environments are
      # declared schedules: none as, so this branch firing at all means either the declaration
      # changed or the document could not be read; pointing at apply before the file risks re-creating
      # exactly what the maintainer's ruling retired.
      red "BACKUP SCHEDULES" "Check $(data_refreshable_file) first — if it still says \"schedules\": \"none\" for this environment, this MISSING finding means the document is stale or unreadable, not that a schedule is owed. Only if it says \"daily\", run: scripts/railway-backup-schedules.sh apply   (or set them in each service's Backups tab)"
    else
      red "BACKUP SCHEDULES" "$wrong of $total volume instance(s) in environment $ENVIRONMENT_ID carry a schedule the declaration says they should not."
      red "BACKUP SCHEDULES" "Run: scripts/railway-backup-schedules.sh remove   (or set \"schedules\": \"daily\" in $(data_refreshable_file) if the decision has moved)"
    fi
    exit "$EX_MISMATCH"
  fi
  if [ "$want_daily" -eq 1 ]; then
    printf 'BACKUP SCHEDULES OK - %s of %s volume instance(s) carry a DAILY schedule.\n' "$total" "$total"
  else
    printf 'BACKUP SCHEDULES OK - %s of %s volume instance(s) carry no schedule, as declared.\n' "$total" "$total"
  fi
}

do_apply() {
  local rows vi name mount kinds newkinds out ok changed=0 total=0
  # SETTING A SCHEDULE WHERE A WRITTEN DECISION SAYS NONE BELONGS CONTRADICTS IT, so it refuses rather
  # than doing it quietly. The fix is one field, and saying which one is the difference between a guard
  # and an obstacle.
  data_schedules_resolve "$ENVIRONMENT_ID"
  if [ "$DATA_SCHEDULES" = "none" ]; then
    die_failed "environment $ENVIRONMENT_ID is declared to carry NO scheduled backups ($DATA_SCHEDULES_WHY), so they were deliberately retired for it. Refusing to set one. If that decision has changed, set \"schedules\": \"daily\" for it in $(data_refreshable_file) — with a schedulesReason — and run this again. Do NOT change \"refreshable\": that answers whether railway-snapshot-guard.sh may skip its pre-destruction backup, which is a different question."
  fi
  rows="$(selected_rows)"
  while IFS=$'\t' read -r vi name mount; do
    [ -n "${vi:-}" ] || continue
    total=$((total + 1))
    kinds="$(schedule_kinds "$vi")" || die_failed "$name: could not read the existing schedules, and this mutation REPLACES the whole set. Refusing rather than sending [DAILY] over schedules it cannot see."
    if has_daily "$kinds"; then
      note "OK     " "$name ($mount) — already DAILY; nothing sent."
      continue
    fi
    # THE UNION, NOT [DAILY]. `kinds` replaces the whole set on that instance.
    newkinds="$(printf '%s\nDAILY\n' "$kinds" | grep . | sort -u | jq -R . | jq -s -c .)"
    note "SET    " "$name ($mount) — $newkinds"
    out="$(gql_checked "setting backup schedules on $name" "$SCHEDULE_UPDATE_MUTATION" \
      "$(jq -n --arg id "$vi" --argjson k "$newkinds" '{id: $id, kinds: $k}')")" \
      || die_failed "$name: the schedule mutation failed. Nothing is known to have been set."
    ok="$(printf '%s' "$out" | jq -r '.data.volumeInstanceBackupScheduleUpdate')"
    [ "$ok" = "true" ] || die_failed "$name: the schedule mutation returned '$ok' rather than true."
    # `true` is the API's word for it; the list is the evidence. Same separation the snapshot guard
    # draws between `workflowStatus` saying Complete and a labelled backup actually existing.
    kinds="$(schedule_kinds "$vi")" || die_failed "$name: the mutation reported success and the schedule list is unreadable, so nothing is proven."
    has_daily "$kinds" \
      || die_failed "$name: the mutation returned true but volumeInstanceBackupScheduleList still shows no DAILY schedule. Treating that as not set."
    changed=$((changed + 1))
  done <<EOF
$rows
EOF
  [ "$total" -gt 0 ] || die_failed "nothing was changed."
  printf 'BACKUP SCHEDULES APPLIED - %s of %s volume instance(s) changed; all %s now carry a verified DAILY schedule.\n' \
    "$changed" "$total" "$total"
}

# THE INVERSE OF `apply`, AND DELIBERATELY NOT MORE THAN THAT. It takes DAILY away and leaves every
# other kind alone, exactly as `apply` adds DAILY and leaves every other kind alone — same union
# arithmetic, same `kinds` REPLACES-the-set hazard, run the other way. Removing every kind would be a
# bigger operation than the one anybody asked for, and `check` reports a surviving WEEKLY as drift
# anyway, so nothing is hidden by the narrower verb.
#
# IT EXISTS BECAUSE A HAND CLICK IS NOT A RECORDED OPERATION. Retiring the schedules was a decision; a
# decision carried out in the Railway dashboard leaves no trace anyone can re-run, diff or disprove —
# which is the same argument that made `apply` a script rather than a runbook line.
do_remove() {
  local rows vi name mount kinds newkinds out ok changed=0 total=0
  # TAKING THE SCHEDULES AWAY WITHOUT A DECLARATION SAYING TO IS THE TRAP THIS WHOLE CHANGE IS ABOUT. It
  # removes the protection AND, where the guard still snapshots, unfunds its manual backup in one step,
  # and both failures surface later, somewhere else, as a deploy that refuses or a volume with nothing
  # to restore. So it refuses unless the document says the word.
  data_schedules_resolve "$ENVIRONMENT_ID"
  if [ "$DATA_SCHEDULES" != "none" ]; then
    die_failed "environment $ENVIRONMENT_ID is NOT declared to carry no scheduled backups ($DATA_SCHEDULES_WHY). Removing its schedules would take away the backups AND unfund railway-snapshot-guard.sh's manual snapshot, which Railway caps at 50% of the volume for a first backup. Set \"schedules\": \"none\" for it in $(data_refreshable_file) — with a schedulesReason — if that is what you mean."
  fi
  # THE SECOND QUESTION, READ ONLY TO SAY WHAT THIS COSTS. Where the guard still takes a pre-destruction
  # snapshot, removing the schedules is what makes its FIRST manual backup hit Railway's 50% cap. The
  # declaration already decided that trade — this refuses nothing, it just makes the operator meet the
  # consequence in the log rather than in a deploy that stops weeks later.
  data_refreshable_resolve "$ENVIRONMENT_ID"
  if [ "$DATA_REFRESHABLE" != "yes" ]; then
    note "UNFUNDING" "$ENVIRONMENT_ID is NOT declared data-refreshable ($DATA_REFRESHABLE_WHY), so railway-snapshot-guard.sh still takes a manual snapshot here — and after this removal its FIRST one is subject to Railway's 50%-of-volume cap. That is the trade the declaration records, not an accident."
  fi
  # DELIBERATELY BEFORE `selected_rows`, so it fires on every invocation of `remove` against this
  # environment — including one that sends nothing because every named volume already lacks DAILY. The
  # warning is about the ENVIRONMENT'S posture going forward (its first manual snapshot will meet the
  # cap), not about what this particular run mutated, so gating it on "did anything change" would hide it
  # on exactly the reruns an operator is most likely to make.
  rows="$(selected_rows)"
  while IFS=$'\t' read -r vi name mount; do
    [ -n "${vi:-}" ] || continue
    total=$((total + 1))
    kinds="$(schedule_kinds "$vi")" || die_failed "$name: could not read the existing schedules, and this mutation REPLACES the whole set. Refusing rather than sending a set it cannot see."
    if ! has_daily "$kinds"; then
      note "OK     " "$name ($mount) — no DAILY schedule; nothing sent."
      continue
    fi
    # THE SET MINUS DAILY, NOT []. Same reasoning as the union in `apply`: `kinds` replaces the whole
    # set, so sending [] over a volume that also carried WEEKLY would delete the weekly one too — and
    # the mutation answers `true` either way, so only the request body could ever show it.
    # `|| true` on the filter, not decoration: the usual case is a volume carrying DAILY and nothing
    # else, where `grep -vx` matches nothing and exits 1 — and under `set -o pipefail` that would abort
    # the script on the one input this verb exists for.
    newkinds="$(printf '%s\n' "$kinds" | grep . | { grep -vx 'DAILY' || true; } | sort -u | jq -R . | jq -s -c .)"
    note "UNSET  " "$name ($mount) — $newkinds"
    out="$(gql_checked "removing the DAILY backup schedule on $name" "$SCHEDULE_UPDATE_MUTATION" \
      "$(jq -n --arg id "$vi" --argjson k "$newkinds" '{id: $id, kinds: $k}')")" \
      || die_failed "$name: the schedule mutation failed. Nothing is known to have been removed."
    ok="$(printf '%s' "$out" | jq -r '.data.volumeInstanceBackupScheduleUpdate')"
    [ "$ok" = "true" ] || die_failed "$name: the schedule mutation returned '$ok' rather than true."
    # `true` is the API's word for it; the list is the evidence — the same separation `apply` draws,
    # and it matters at least as much here: a removal that did not happen leaves a schedule running and
    # a log saying it is gone.
    kinds="$(schedule_kinds "$vi")" || die_failed "$name: the mutation reported success and the schedule list is unreadable, so nothing is proven."
    # An `if`, not `has_daily … && die_failed`, for legibility rather than for safety: the AND-list would
    # in fact be safe, because `set -e` exempts every command in an `&&` list but the last, so its
    # returning 1 on the SUCCESS path — DAILY absent — would not abort the loop. Stated here because the
    # comment that stood at this line claimed the opposite, and a false *why* is what the next author
    # reasons from. The sibling `|| true` above is NOT decoration and the distinction is real.
    if has_daily "$kinds"; then
      die_failed "$name: the mutation returned true but volumeInstanceBackupScheduleList still shows a DAILY schedule. Treating that as not removed."
    fi
    changed=$((changed + 1))
  done <<EOF
$rows
EOF
  [ "$total" -gt 0 ] || die_failed "nothing was changed."
  printf 'BACKUP SCHEDULES REMOVED - %s of %s volume instance(s) changed; none of the %s now carries a DAILY schedule.\n' \
    "$changed" "$total" "$total"
}

# --- entry point --------------------------------------------------------------------------------------
# THE LINE NUMBERS ARE THE USAGE BLOCK AT THE TOP OF THIS FILE. Adding a verb there without widening
# this range prints a usage message missing the verb the reader is looking for — which is how a help
# text starts lying. Keep them in step.
usage() { sed -n '5,7p' "$0" >&2; exit "$EX_ENV"; }

MODE="${1:-}"; shift || usage
WANT_VOLUMES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --volume) [ $# -ge 2 ] || usage; WANT_VOLUMES+=("$2"); shift 2 ;;
    --)       shift ;;
    *)        usage ;;
  esac
done

case "$MODE" in
  check)  resolve_scope; note "SCOPE" "project=$PROJECT_ID environment=$ENVIRONMENT_ID"; do_check ;;
  apply)  resolve_scope; note "SCOPE" "project=$PROJECT_ID environment=$ENVIRONMENT_ID"; do_apply ;;
  remove) resolve_scope; note "SCOPE" "project=$PROJECT_ID environment=$ENVIRONMENT_ID"; do_remove ;;
  *)      usage ;;
esac
exit "$EX_OK"
