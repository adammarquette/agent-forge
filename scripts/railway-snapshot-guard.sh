#!/usr/bin/env bash
# railway-snapshot-guard.sh — refuse a destructive Railway operation that is not preceded by a
# VERIFIED volume snapshot.
#
#   scripts/railway-snapshot-guard.sh classify [--plan FILE] -- <railway argv...>
#   scripts/railway-snapshot-guard.sh snapshot [--label TEXT] [--volume NAME]
#   scripts/railway-snapshot-guard.sh run [--plan FILE] [--label TEXT] -- <railway argv...>
#
# WHY THIS EXISTS
#
# Production's database was emptied on 2026-09-18 and `volumeInstanceBackupList` returned an empty array
# for `mysql-data`: no scheduled backups, no manual ones, nothing to restore. Recovery was possible only
# because this project mandates synthetic demo data and the cohort could be re-seeded. Real state would
# have been gone. A separate change
#
# WHAT RAILWAY ACTUALLY OFFERS, established by introspecting the live API rather than assumed:
#
#   mutation volumeInstanceBackupCreate(name: String, volumeInstanceId: String!): WorkflowId!
#   query    volumeInstanceBackupList(volumeInstanceId: String!): [VolumeInstanceBackup!]
#   query    workflowStatus(workflowId: String!): WorkflowResult   # Complete | Error | NotFound | Running
#
# The create side EXISTS. Three properties of it shape everything below:
#
#   1. IT IS ASYNCHRONOUS AND RETURNS A WORKFLOW ID, NOT A BACKUP ID. A script that fires the mutation,
#      logs what came back and proceeds has logged a receipt for work that may still be running or may
#      have failed. So this guard WAITS for the work and then PROVES it, and those are two separate
#      jobs: it polls `workflowStatus` to Complete in order to WAIT, and then requires a backup carrying
#      THIS RUN'S LABEL that was not in the list beforehand in order to PROVE. Only the second is the
#      proof — "some new id appeared" would accept a scheduled backup that happened to land mid-run.
#      A credential may create a backup and still not be allowed to read `workflowStatus`. A TEAM token
#      does exactly that (2026-09-19, fearless-abundance/staging, where this guard refused a snapshot it
#      had in fact taken) - AND SO DOES A PROJECT TOKEN, measured 2026-09-22 on the same environment,
#      which means the fallback below is the ONLY path CI ever takes. A separate change
#      Waiting therefore falls back to polling the backup list for the label, on the
#      RESTARTED deadline - see the note at the fallback loop for why it restarts and what it costs.
#      THE PROOF DOES NOT MOVE, and `Not Authorized` is the only message that triggers the
#      fallback — every other workflow failure still refuses. A separate change
#   2. IT KEYS OFF A VOLUME **INSTANCE**, NOT A VOLUME. A `Volume` is project-level (id, name, projectId).
#      A `VolumeInstance` is the per-environment materialisation (environmentId, serviceId, mountPath) and
#      is what carries data and backups. `railway volume list --json` prints VOLUME ids. Passing one to
#      `volumeInstanceBackupList` answers `Not Authorized` — a message that names neither the real fault
#      nor the right object. That project-level/environment-level confusion is the shape of the incident
#      this guard exists for. A separate change
#   3. THE CLI CANNOT DO IT. Railway CLI 5.57.2's `railway volume` has list/add/delete/update/detach/
#      attach/files/browse and no backup or snapshot verb at all. The guard therefore talks to the
#      GraphQL API directly; `railway` stays the thing it guards, not the thing it calls.
#
# FAIL CLOSED, IN EVERY DIRECTION. The standing failure mode of this repository's guards is that they
# fail PERMISSIVE when they break. This one refuses on: no credential, an unreadable project graph, a
# volume name that resolves to more or less than one instance in the target environment, a token whose
# environment disagrees with the requested one, a mutation error, a workflow that does not reach Complete
# inside the timeout, a workflow that reaches Complete without producing a new backup, and a plan it
# cannot positively prove harmless. In `run` mode a refusal means the guarded command is never executed.
#
# ONE THING IT NO LONGER REFUSES FOR, AND IT IS DECLARED RATHER THAN INFERRED: an environment listed as
# data-refreshable in `scripts/railway-data-refreshable.json` is not snapshotted at all, because its data
# is synthetic and re-seedable and a backup nobody would restore is a cost with no matching risk
# (maintainer ruling). Every OTHER refusal above is unchanged, including for that environment.
# An environment that is not listed — or listed with anything other than the boolean `true` — is
# snapshotted exactly as before: the declaration can only be read as "not refreshable" when it is
# missing, broken or ambiguous. A separate change
#
# Every API result below is captured into a variable and its status checked before it is parsed. Nothing
# is read out of a pipeline, because a pipeline reports only its last command and would report jq's
# opinion of an error document as success — the shape of permissive failure this file is written against.
#
# RAILWAY SUPPLIES THE ALLOWLIST; READ IT RATHER THAN INVENT ONE. `railway config plan --out` writes a
# document that names itself — `kind: "railway.config.plan"` — and labels EVERY entry in
# `changeSet.changes[]` with a `severity` drawn from {safe, destructive}. That is the closed vocabulary,
# published by the thing that owns the schema. `classify_plan` reads it structurally, demands the shape
# first, and refuses on anything outside that vocabulary. A separate change
#
# It was previously MODELLED instead — an action key drawn from a guessed list carrying a value from a
# guessed safe set — and against a real plan that classifier could not return HARMLESS at all: Railway's
# action key is `kind` (`resource.create`, `variable.delete`), which was not on the list, and the `type`
# key that IS present carries data-type discriminators (`literal`, `image`, `service`, `volume`), which
# were not in the safe set. Every genuine plan came back UNDECIDABLE, and the captured one came back
# DESTRUCTIVE only because a regex matched the ENGLISH PROSE in `.diff` and `.summary`. Safety resting on
# Railway continuing to narrate its JSON in English is not safety.
#
# The string matching survives as a FALLBACK for a document that does not name itself: an excerpt pasted
# into an issue, a fragment, a plan from some other tool. There, an action value this file does not know —
# terraform's `-`, `teardown`, `DROP` — is UNDECIDABLE, not harmless, and `run` snapshots on UNDECIDABLE
# exactly as it does on DESTRUCTIVE. That is the difference between an allowlist and a blocklist.
#
# THE TWO CLASSIFIERS ESCALATE; THEY DO NOT OVERRIDE. argv and plan are two readings of one operation, and
# the effective verdict is the MORE SEVERE of them (DESTRUCTIVE > UNDECIDABLE > HARMLESS). A plan of pure
# creates cannot make `volume delete` — or any command line this file does not recognise — safe. The one
# case where a plan is allowed to LOWER the verdict is `railway config apply`, whose argv verdict is
# literally "I cannot judge this without the plan"; that, and nothing else, is what `--plan` clears — and
# only when the guarded command NAMES that same plan, because an apply that names none re-evaluates the
# authoring file and so was never classified by the pin at all. A separate change

set -euo pipefail

# --- exit codes -------------------------------------------------------------------------------------
#   0   ok / classified harmless
#   1   usage or environment error (missing jq, missing credential)
#   2   snapshot refused or failed — in `run` mode the guarded command was NOT executed
#  10   classified DESTRUCTIVE
#  11   classified UNDECIDABLE (treated as destructive by `run`)
EX_OK=0; EX_ENV=1; EX_REFUSED=2; EX_DESTRUCTIVE=10; EX_UNDECIDABLE=11
# Internal only, never returned to a caller: "UNDECIDABLE, and specifically because the plan is what would
# decide it". `decide` maps it to EX_UNDECIDABLE. It exists so that the permission to let a plan LOWER the
# verdict is carried by the branch that earned it, rather than re-derived from the argv in a second place
# where `config apply --volume mysql-data` would quietly qualify. `decide` lowers it only for an apply that
# names the plan; see there.
EX_NEEDS_PLAN=12

# Severity ordering for combining the two classifiers: DESTRUCTIVE > UNDECIDABLE > everything else.
severity() { # severity <exit-code>
  case "$1" in
    "$EX_DESTRUCTIVE") echo 2 ;;
    "$EX_UNDECIDABLE") echo 1 ;;
    *)                 echo 0 ;;
  esac
}

ENDPOINT="${RAILWAY_SNAPSHOT_GUARD_ENDPOINT:-https://backboard.railway.com/graphql/v2}"
POLL_SECONDS="${RAILWAY_SNAPSHOT_GUARD_POLL:-5}"
TIMEOUT_SECONDS="${RAILWAY_SNAPSHOT_GUARD_TIMEOUT:-900}"

ERRFILE="$(mktemp)"
# ERRKIND IS SEPARATE FROM ERRFILE ON PURPOSE. ERRFILE holds a COMPOSED, human-facing sentence - the
# call site's `what`, the generic explanation, sometimes a hint - so substring-matching it for a
# decision matches text this guard wrote about the error rather than the error. ERRKIND holds one
# machine token set only on an EXACT match, and callers switch on it. A separate change
ERRKIND="$(mktemp)"
trap 'rm -f "$ERRFILE" "$ERRKIND"' EXIT

red()  { printf '\033[31m%s\033[0m %s\n' "$1" "$2" >&2; }
note() { printf '%s %s\n' "$1" "$2" >&2; }

# For the job log: keep the shape of the command, drop anything that looks like a value.
redact_argv() {
  local a out=""
  for a in "$@"; do
    case "$a" in *=*) a="${a%%=*}=***" ;; esac
    out="$out $a"
  done
  printf '%s' "${out# }"
}

die_env()     { red "GUARD CANNOT RUN" "$1"; exit "$EX_ENV"; }
die_refused() {
  red "SNAPSHOT REFUSED" "$1"
  red "REFUSING" "the destructive operation was NOT run."
  exit "$EX_REFUSED"
}
# Every call site that can fail funnels through this, so a failure deep in a command substitution still
# stops the whole script rather than returning an empty string to a caller that carries on.
bail() { die_refused "$(cat "$ERRFILE" 2>/dev/null)"; }

command -v jq >/dev/null 2>&1 \
  || die_env "jq is required and is not on PATH. The .railway CI job installs it (apk add jq); a workstation must too."
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" \
  || die_env "scripts/lib/jq-crlf.sh refused to load."

# --- the data-refreshable declaration ---------------------------------------------------------------
# WHAT THIS GUARD DOES WHEN NOBODY IS TAKING SCHEDULED BACKUPS ANY MORE. The DAILY schedules that sat
# under it were retired on 2026-09-22: the data in both environments is synthetic and
# re-seedable, so a backup nobody would restore is a cost with no matching risk. That decision would
# have broken this guard quietly if nothing else changed — Railway caps a MANUAL backup at 50% of the
# volume and the cap bites the FIRST one, so a volume past half full with no scheduled backup makes
# every guarded apply refuse. Fail-closed and loud, and still a deploy that stops for nothing.
#
# So the judgement is DECLARED, per environment, in scripts/railway-data-refreshable.json, and this
# guard reads its `refreshable` field — AND NO OTHER FIELD IN THAT DOCUMENT. The `schedules` field beside
# it answers a different question, for railway-backup-schedules.sh, and production answered the two the
# other way round - not refreshable, and no schedules - until a separate change declared it refreshable too.
#
# An environment declared refreshable takes no snapshot; EVERYTHING ELSE THIS GUARD DOES IS UNCHANGED —
# the classification, the plan-identity check and the plan fingerprint all still run, and all still
# refuse. What is given up is the backup, and only where someone wrote down that there is nothing worth
# backing up. A separate change, DEPLOYMENT.md §10
[ -f "$(dirname "${BASH_SOURCE[0]}")/railway-data-refreshable.sh" ] \
  || die_env "scripts/railway-data-refreshable.sh is missing; this guard cannot read the per-environment data-refreshable declaration without it."
# shellcheck source=scripts/railway-data-refreshable.sh
. "$(dirname "${BASH_SOURCE[0]}")/railway-data-refreshable.sh"

# --- transport --------------------------------------------------------------------------------------
# One seam, so the self-test can drive every branch below without a network or a Railway account.
# RAILWAY_SNAPSHOT_GUARD_TRANSPORT is a command that reads the GraphQL request body on stdin and writes
# the GraphQL response on stdout. Unset, it is curl against $ENDPOINT.
#
# AUTH IS NOT ONE HEADER. A Railway PROJECT token — which is what CI holds, as RAILWAY_TOKEN_PROD, and
# what `railway` itself reads from RAILWAY_TOKEN — authenticates with `Project-Access-Token`. A personal
# or team token uses `Authorization: Bearer`. Sending a project token as a Bearer credential answers
# `Not Authorized`, which reads like a permissions problem and is a header problem.
gql() { # gql <query> <variables-json>
  local body
  body="$(jq -n --arg q "$1" --argjson v "$2" '{query: $q, variables: $v}')"
  if [ -n "${RAILWAY_SNAPSHOT_GUARD_TRANSPORT:-}" ]; then
    printf '%s' "$body" | $RAILWAY_SNAPSHOT_GUARD_TRANSPORT
    return
  fi
  local auth_header
  if [ -n "${RAILWAY_TOKEN:-}" ]; then
    auth_header="Project-Access-Token: ${RAILWAY_TOKEN}"
  elif [ -n "${RAILWAY_API_TOKEN:-}" ]; then
    auth_header="Authorization: Bearer ${RAILWAY_API_TOKEN}"
  else
    printf 'no credential: set RAILWAY_TOKEN (project token) or RAILWAY_API_TOKEN (personal/team token).' > "$ERRFILE"
    return 1
  fi
  curl -sS --fail-with-body -X POST "$ENDPOINT" \
    -H "$auth_header" -H 'Content-Type: application/json' --data-binary "$body"
}

# Returns 0 and prints the response, or returns 1 with the reason in $ERRFILE. A GraphQL error is an
# HTTP 200 carrying an `errors` array, so status alone proves nothing.
# The optional fourth argument replaces the generic `Not Authorized` explanation at a call site that
# knows more than the generic one can. Railway answers `Not Authorized` both for a credential that may
# not do this and for an id of the wrong kind — but a call site that has already had THIS id accepted
# can rule the second one out, and saying so is the difference between a five-minute diagnosis and an
# hour spent inspecting an id the guard already validated.
gql_checked() { # gql_checked <what> <query> <variables-json> [not-authorized-explanation]
  local what="$1" hint="${4:-}" out
  # CLEARED PER CALL, and this is a correctness fix rather than hygiene. ERRFILE is one mktemp for the
  # whole run and nothing truncated it, so after ONE genuine failure its text persisted for every later
  # call - and do_snapshot loops over every volume instance in the environment. A caller asking "was
  # that Not Authorized?" would read instance 1's answer while handling instance 5's unrelated HTTP 500.
  : > "$ERRFILE"
  : > "$ERRKIND"
  if ! out="$(gql "$2" "$3" 2>&1)"; then
    [ -s "$ERRFILE" ] || printf '%s: the API call failed — %s' "$what" "$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-300)" > "$ERRFILE"
    return 1
  fi
  if ! printf '%s' "$out" | jq -e . >/dev/null 2>&1; then
    printf '%s: the API returned something that is not JSON — %s' "$what" "$(printf '%s' "$out" | tr '\n' ' ' | cut -c1-200)" > "$ERRFILE"
    return 1
  fi
  if printf '%s' "$out" | jq -e '(.errors // []) | length > 0' >/dev/null 2>&1; then
    local msg; msg="$(printf '%s' "$out" | jq -r '[.errors[].message] | join("; ")')"
    # `=` and not a substring: GraphQL may return SEVERAL errors, and line ~187 joins them with "; ".
    # A response carrying `workflow WF-1 failed: backup aborted` AND `Not Authorized` must not be read
    # as a permissions answer - that is an explicit abort with a permissions error stapled to it.
    [ "$msg" = "Not Authorized" ] && printf 'not-authorized' > "$ERRKIND"
    if [ "$msg" = "Not Authorized" ] && [ -n "$hint" ]; then
      printf '%s: Not Authorized. %s' "$what" "$hint" > "$ERRFILE"
    elif [ "$msg" = "Not Authorized" ]; then
      printf '%s: Not Authorized. Railway returns this both for a credential that may not do this AND for an id of the wrong kind — a volume id where a volume INSTANCE id belongs. Check the id before rotating the token.' "$what" > "$ERRFILE"
    else
      printf '%s: %s' "$what" "$msg" > "$ERRFILE"
    fi
    return 1
  fi
  printf '%s' "$out"
}

# --- target resolution ------------------------------------------------------------------------------
# WHY THE CREDENTIAL, NOT A NAME, PICKS THE ENVIRONMENT. A Railway project token is scoped to exactly one
# environment, so it can say which one it is. A name cannot: a volume is PROJECT-scoped, so another
# environment's volume name resolves perfectly well and names the wrong disk. `staging` also carried three
# volumes at OpenEMR's sites path and two at `/var/lib/mysql` while its duplicates were live, and resolving
# by name would pick one and be confident about it. The duplicates are being deleted and made the
# authoring file name the volume each environment actually uses, so the SECOND half of that example is now
# history - the project-scoping in the first half is not, and is the reason this resolves by credential.
resolve_scope() {
  if [ -n "${RAILWAY_TOKEN:-}" ]; then
    local out pid eid
    out="$(gql_checked "reading the project token's scope" 'query { projectToken { projectId environmentId } }' '{}')" || bail
    pid="$(printf '%s' "$out" | jq -r '.data.projectToken.projectId // empty')"
    eid="$(printf '%s' "$out" | jq -r '.data.projectToken.environmentId // empty')"
    [ -n "$pid" ] && [ -n "$eid" ] || die_refused "the project token did not report a project and environment."
    # An environment-scoped credential plus a differently-scoped request is the incident's exact shape.
    if [ -n "${RAILWAY_ENVIRONMENT_ID:-}" ] && [ "$RAILWAY_ENVIRONMENT_ID" != "$eid" ]; then
      die_refused "RAILWAY_ENVIRONMENT_ID=$RAILWAY_ENVIRONMENT_ID but the token is scoped to $eid. Refusing rather than guessing which one you meant."
    fi
    PROJECT_ID="$pid"; ENVIRONMENT_ID="$eid"
  else
    PROJECT_ID="${RAILWAY_PROJECT_ID:-}"; ENVIRONMENT_ID="${RAILWAY_ENVIRONMENT_ID:-}"
    [ -n "$PROJECT_ID" ] && [ -n "$ENVIRONMENT_ID" ] \
      || die_env "with a personal token, set RAILWAY_PROJECT_ID and RAILWAY_ENVIRONMENT_ID. Ids, not names — see the note above."
  fi
}

VOLUME_GRAPH_QUERY='query($pid: String!) { project(id: $pid) { name volumes { edges { node { id name volumeInstances { edges { node { id environmentId serviceId mountPath state sizeMB currentSizeMB } } } } } } } }'

# Prints one TSV line per volume instance in the target environment:
#   instanceId <TAB> volumeName <TAB> mountPath <TAB> sizeMB <TAB> currentSizeMB
resolve_instances() { # resolve_instances [volume-name]
  local want="${1:-}" out rows count
  out="$(gql_checked "reading the project's volume graph" "$VOLUME_GRAPH_QUERY" "$(jq -n --arg pid "$PROJECT_ID" '{pid: $pid}')")" || bail
  rows="$(printf '%s' "$out" | jq -r --arg env "$ENVIRONMENT_ID" '
    .data.project.volumes.edges[].node as $v
    | $v.volumeInstances.edges[].node
    | select(.environmentId == $env)
    | select(.state == "READY")
    | [.id, $v.name, .mountPath, (.sizeMB // 0), (.currentSizeMB // 0)]
    | @tsv')"
  rows="$(printf '%s\n' "$rows" | grep . || true)"

  if [ -n "$want" ]; then
    rows="$(printf '%s\n' "$rows" | awk -F'\t' -v n="$want" '$2 == n' || true)"
    count="$(printf '%s\n' "$rows" | grep -c . || true)"
    [ "$count" = "1" ] || die_refused "volume name '$want' resolves to $count volume instances in environment $ENVIRONMENT_ID. A name is not an identifier here — snapshot the whole environment, or fix the duplicate names."
  fi

  count="$(printf '%s\n' "$rows" | grep -c . || true)"
  [ "$count" != "0" ] || die_refused "no READY volume instances found in environment $ENVIRONMENT_ID. Refusing: a destructive operation against an environment whose volumes cannot be read is precisely the case this guard exists for."
  printf '%s\n' "$rows"
}

BACKUP_LIST_QUERY='query($id: String!) { volumeInstanceBackupList(volumeInstanceId: $id) { id name createdAt } }'

# IDENTITY, NOT EXISTENCE — which is why the name comes back too.
#
# Diffing the list of IDS before and after only proves that *a* backup appeared. A previous guard run that
# timed out and completed late, a second agent guarding the same environment concurrently, or a scheduled
# backup landing between the two list calls all satisfy such a diff while THIS run produced nothing at
# all. The third of those is rarer since a separate change retired the schedules — and is exactly why the reasoning
# is written from the shape rather than from the schedules: anyone can set one in the Backups tab, and
# the other two never depended on a schedule at all. The label is already generated, already passed as
# `name:` to the mutation and already selected here; using it is the whole fix.
list_backups() { # list_backups <instance-id>; prints "id<TAB>name<TAB>createdAt" per backup
  local out
  out="$(gql_checked "listing backups for volume instance $1" "$BACKUP_LIST_QUERY" \
    "$(jq -n --arg id "$1" '{id: $id}')")" || return 1
  printf '%s' "$out" | jq -r '.data.volumeInstanceBackupList[]? | [.id, (.name // ""), (.createdAt // "")] | @tsv'
}

# The id of the newest backup in a TSV listing whose name is exactly this run's label, or nothing.
backup_id_for_label() { # backup_id_for_label <label> <<< tsv
  awk -F'\t' -v l="$1" 'NF && $2 == l { print $3 "\t" $1 }' | sort | tail -n1 | cut -f2
}

# Is <id> exactly one line of <list>? A HERE-STRING AND A COUNT, never `printf | grep -q`: under
# pipefail grep -q exits at its first match, printf can die of SIGPIPE writing the rest, and the 141
# reads as "not listed" - which accepts a backup that predates this run as this run's snapshot. No
# count at all (the here-string's temp file could not be made, or grep did not answer) refuses.
id_listed() { # id_listed <id> <newline-separated list>
  local n
  n="$(grep -cxF -- "$1" <<<"$2")" || true
  case "$n" in
    ''|*[!0-9]*) die_refused "could not search the pre-run backup list for '$1' - grep returned no count, which is not a 'no'. Nothing is known about whether this snapshot is new." ;;
  esac
  [ "$n" -gt 0 ]
}

# --- snapshotting -----------------------------------------------------------------------------------
snapshot_instance() { # <instance-id> <volume-name> <mount> <sizeMB> <currentMB> <label>
  local vi="$1" name="$2" mount="$3" size="$4" used="$5" label="$6"
  local before before_ids after new out wf status err waited waited_on_list=0

  before="$(list_backups "$vi")" || bail
  before_ids="$(printf '%s\n' "$before" | awk -F'\t' 'NF { print $1 }')"

  # A backup already carrying this run's label would make the identity check below pass on somebody
  # else's work. Read from the listing already in hand rather than asking Railway a second time.
  if [ -n "$(printf '%s\n' "$before" | backup_id_for_label "$label")" ]; then
    die_refused "$name: a backup named '$label' already exists on this volume instance. The label is how this guard proves the snapshot it verifies is its own — pass a --label that is not already taken."
  fi

  # Railway caps a MANUAL backup at 50% of the volume's size. Backups are incremental, so the cap bites
  # the FIRST one — the full copy — and taking a first one is this guard's whole job. Checked here so the
  # operator reads "grow the volume" rather than an API error raised halfway through a deploy.
  #
  # NOTHING FUNDS THIS ANY MORE, AND THAT IS THE POINT OF THE DECLARATION ABOVE. A scheduled backup used
  # to remove the cap for every manual one after it, so an environment that
  # is NOT declared data-refreshable reaches this branch on a first backup for ever, and refuses once its
  # volume passes half full. That refusal is correct — it is the environment saying it holds data worth
  # protecting and cannot protect it — and the fix is to grow the volume, take one backup by hand, or
  # declare the data refreshable in scripts/railway-data-refreshable.json with a reason.
  if [ -z "$before_ids" ] && [ "${size:-0}" -gt 0 ] 2>/dev/null; then
    if awk -v u="$used" -v s="$size" 'BEGIN { exit !(u > s / 2) }'; then
      die_refused "$name ($mount) holds ${used}MB of ${size}MB and has no existing backup. Railway limits a MANUAL backup to 50% of the volume size, so the first one cannot be taken. Grow the volume, or have Railway raise the limit, before running anything destructive."
    fi
  fi

  note "SNAPSHOT" "$name ($mount) — volume instance $vi"
  out="$(gql_checked "creating a backup of $name" \
    'mutation($id: String!, $name: String) { volumeInstanceBackupCreate(volumeInstanceId: $id, name: $name) { workflowId } }' \
    "$(jq -n --arg id "$vi" --arg n "$label" '{id: $id, name: $n}')" \
    "volumeInstanceBackupList accepted this same volume instance id moments ago, so this is NOT an id-of-the-wrong-kind problem — this credential may list backups and may not create them. Rotate to a token that can, or take the snapshot by hand in the service's Backups tab. Do not go looking at the id.")" || bail
  # `WorkflowId` is an OBJECT carrying a `workflowId` string, not a scalar — so this is the only path to
  # the id, and there is no second one to fall back to. A `// .data.volumeInstanceBackupCreate` fallback
  # would turn a null id into the enclosing object's JSON text: a non-empty string, and a guard that
  # thinks it has a workflow to wait on when the mutation returned nothing.
  wf="$(printf '%s' "$out" | jq -r '.data.volumeInstanceBackupCreate.workflowId // empty')"
  [ -n "$wf" ] || die_refused "$name: the backup mutation returned no workflow id."

  # A WorkflowId is a receipt for work that has not happened yet. Waiting for it is not optional.
  #
  # WAITING IS NOT THE SAME AS PROVING, and separating the two is what lets this loop degrade safely.
  # `workflowStatus` is how this guard WAITS; the labelled-backup check below is how it PROVES. A team
  # token may create a backup and still be refused `workflowStatus` — observed on 2026-09-19 against
  # fearless-abundance/staging, where the mutation succeeded, the poll answered `Not Authorized`, and the
  # guard refused a snapshot it had in fact taken. Refusing there is not caution, it is a false negative:
  # the proof was available and never reached, and the operator's only remaining move is to bypass the
  # one guard in this repository that fails closed. So an unreadable workflow falls back to waiting on
  # the evidence itself. NOTHING IS WEAKENED — the identity check below is unchanged and still the only
  # thing that can call a snapshot taken. Every other failure of this query is still fatal, because only
  # `Not Authorized` says "this credential cannot answer"; the rest say the workflow is in a state this
  # guard must not read as success. A separate change
  waited=0
  while :; do
    if ! out="$(gql_checked "polling workflow $wf" \
      'query($id: String!) { workflowStatus(workflowId: $id) { status error } }' \
      "$(jq -n --arg id "$wf" '{id: $id}')")"; then
      [ "$(cat "$ERRKIND" 2>/dev/null)" = "not-authorized" ] || bail
      waited_on_list=1
      note "SNAPSHOT" "workflowStatus is not readable with this credential — waiting on the labelled backup instead."
      break
    fi
    err="$(printf '%s' "$out" | jq -r '.data.workflowStatus.error // empty')"
    status="$(printf '%s' "$out" | jq -r '.data.workflowStatus.status // empty')"
    case "$status" in
      Complete) break ;;
      Error)    die_refused "$name: the backup workflow failed — ${err:-no detail given}." ;;
      NotFound) die_refused "$name: workflow $wf is not known to Railway. Nothing was snapshotted." ;;
      Running)  : ;;
      *)        die_refused "$name: unexpected workflow status '${status:-<empty>}'. Refusing rather than reading an unknown state as success." ;;
    esac
    waited=$((waited + POLL_SECONDS))
    [ "$waited" -lt "$TIMEOUT_SECONDS" ] || die_refused "$name: the backup workflow did not complete within ${TIMEOUT_SECONDS}s. Nothing is known to have been snapshotted."
    sleep "$POLL_SECONDS"
  done

  # The fallback wait. Same label, same identity rule — only the thing being polled differs, and the
  # deadline RESTARTS rather than continuing. That is deliberate: the workflow poll may have burned most
  # of the budget before the credential was refused, and a fallback with no time left would refuse a
  # snapshot that is merely slow. The honest cost is that a worst case is 2 x RAILWAY_SNAPSHOT_GUARD_
  # TIMEOUT, not one, and the operator should read the default 900s as "up to 30 minutes" on this path —
  # PER VOLUME INSTANCE. do_snapshot calls this in series, one instance at a time, so an apply touching
  # several volumes multiplies this figure rather than paying it once; it is not the apply's ceiling.
  # It deliberately does NOT accept "some id is new": a scheduled backup landing mid-run would
  # satisfy that, which is the exact confusion the label exists to end.
  #
  # THE ASSUMPTION THIS RESTS ON, STATED SO IT CAN BE CHECKED: that Railway lists a backup in
  # volumeInstanceBackupList only once it is USABLE, not while it is still being written.
  # BACKUP_LIST_QUERY selects `id name createdAt` and no status field, so this loop breaks on a row
  # EXISTING. The workflowStatus path does not need the assumption - `Complete` is the API saying so -
  # which is why that path is still preferred and this one runs only when the credential cannot read it.
  # Not verified against Railway: the API exposes no backup status to check it with, and inventing one
  # would be guessing. If it turns out to be false the failure is a guard that permits slightly early.
  # THIS IS NOW THE ONLY PATH IN CI, NOT THE EXCEPTIONAL ONE. This comment used to advise preferring a
  # PROJECT token, on the reasoning that one could read workflowStatus. Measured 2026-09-22 against
  # fearless-abundance/staging: a project token CAN create a backup and CANNOT read workflowStatus -
  # `Not Authorized`, the same as a team token. There is no credential in this project that reaches the
  # stronger path, so every guarded apply lands here and inherits the restarted deadline and the
  # assumption above. A separate change, DEPLOYMENT.md §10
  if [ "$waited_on_list" -eq 1 ]; then
    waited=0
    while :; do
      after="$(list_backups "$vi")" || bail
      new="$(printf '%s\n' "$after" | backup_id_for_label "$label")"
      if [ -n "$new" ] && ! id_listed "$new" "$before_ids"; then
        break
      fi
      waited=$((waited + POLL_SECONDS))
      [ "$waited" -lt "$TIMEOUT_SECONDS" ] || die_refused "$name: no backup named '$label' appeared within ${TIMEOUT_SECONDS}s, and this credential cannot read workflow status to say why. Nothing is known to have been snapshotted."
      sleep "$POLL_SECONDS"
    done
  fi

  # AND THIS RUN'S backup must actually be there. "Workflow Complete" is the API's word for it; a backup
  # carrying this run's label is the evidence. A guard that stops at the former is the permissive failure
  # this file is written against; one that stops at "some id is new" accepts a scheduled backup that
  # happened to land mid-run as proof of work it did not do.
  after="$(list_backups "$vi")" || bail
  new="$(printf '%s\n' "$after" | backup_id_for_label "$label")"
  if [ -z "$new" ]; then
    if [ "$waited_on_list" -eq 1 ]; then
      die_refused "$name: no backup named '$label' appeared in volumeInstanceBackupList. Treating that as no snapshot — whatever else is in that list was not taken by this run."
    fi
    die_refused "$name: the workflow reported Complete but no backup named '$label' appeared in volumeInstanceBackupList. Treating that as no snapshot — whatever else is in that list was not taken by this run."
  fi
  if id_listed "$new" "$before_ids"; then
    die_refused "$name: the only backup named '$label' was already present before this run started. Treating that as no snapshot."
  fi

  printf 'BACKUP label=%s volume=%s instance=%s mount=%s backup_id=%s\n' "$label" "$name" "$vi" "$mount" "$new"
}

do_snapshot() { # do_snapshot <label> [volume-name]
  local label="$1" want="${2:-}" rows count=0 vi name mount size used
  resolve_scope
  note "GUARD" "project=$PROJECT_ID environment=$ENVIRONMENT_ID"

  # THE DECLARATION IS READ AFTER THE SCOPE AND BEFORE ANYTHING ELSE, because the environment id is
  # what it is keyed on and the credential is what supplies that. Reading it earlier would mean
  # trusting a name; reading it later would mean resolving volumes nobody is going to snapshot.
  #
  # NOT SILENT. An operator reading a job log has to be able to tell "this guard took no backup
  # because someone decided none was needed" from "this guard is broken" — those look identical at
  # exit 0, and only one of them is fine. So the environment, the file and the recorded reason all go
  # to the log every time. A separate change
  data_refreshable_resolve "$ENVIRONMENT_ID"
  if [ "$DATA_REFRESHABLE" = "yes" ]; then
    note "NO SNAPSHOT" "environment $ENVIRONMENT_ID is declared data-refreshable: $DATA_REFRESHABLE_WHY"
    note "NO SNAPSHOT" "declared in $(data_refreshable_file). The classification, the plan-identity check and the plan fingerprint still apply; only the backup is skipped."
    return 0
  fi

  rows="$(resolve_instances "$want")"
  while IFS=$'\t' read -r vi name mount size used; do
    [ -n "${vi:-}" ] || continue
    snapshot_instance "$vi" "$name" "$mount" "$size" "$used" "$label"
    count=$((count + 1))
  done <<EOF
$rows
EOF
  [ "$count" -gt 0 ] || die_refused "nothing was snapshotted."
  note "GUARD" "$count volume instance(s) snapshotted and verified."
}

# --- classification ---------------------------------------------------------------------------------
# WHAT COUNTS AS DESTRUCTIVE IS ENUMERATED, NOT JUDGED. Three shapes. A separate change
#
#   (a) a `railway config apply` whose plan proposes a delete — the plan decides, see classify_plan;
#   (b) a verb that removes or re-points storage: volume delete/detach/update, service or environment
#       delete, `down`, and a backup or PITR restore, which swaps the mount out from under the service;
#   (c) an environment-scoped invocation that names a PROJECT-level object — `-v <name>` and friends.
#       `railway volume delete -v mysql-data` binds a project-level volume by name; that is how the
#       previous outage happened. Any argv naming a volume is destructive-or-undecidable whatever the
#       verb. A separate change
#
# Anything not positively recognised as read-only is UNDECIDABLE, which `run` treats as destructive.
#
# `domain` and `variables` are NOT on this list. Both are read-only as bare verbs and neither is as
# `domain delete api.example.com` or `variables --set KEY=VALUE`, and this regex matches a verb wherever
# it appears in the command line — so listing them made both of those HARMLESS. They are recognised
# below, where the mutating forms can be carved out first.
READ_ONLY_RE='^(status|whoami|list|ls|logs|docs|completion|help|link|login|version)$'
# Verbs that only read when nothing after them says otherwise.
CONDITIONALLY_READ_ONLY_RE='^(domain|domains|variables)$'

classify_argv() { # prints the verdict, returns the exit code
  local -a argv=("$@") words=()
  local a

  # Match on the SUBCOMMAND WORDS, never on the joined command line. `case " $* "` with two quoted
  # segments cannot match adjacent words — " volume " already eats the space " delete " needs — so a
  # pattern like *" volume "*" delete "* silently never fires, and a guard that never fires is worse
  # than no guard. Flags are dropped so `volume delete --yes` and `--json volume delete` read alike.
  for a in "${argv[@]:1}"; do
    case "$a" in -*) ;; *) words+=("$a") ;; esac
  done
  local verb="${words[0]:-}" sub="${words[1]:-}"
  has() { local w; for w in "${words[@]:-}"; do [ "$w" = "$1" ] && return 0; done; return 1; }

  if has volume && { has delete || has remove || has rm; }; then
    echo "DESTRUCTIVE  deletes a volume"; return "$EX_DESTRUCTIVE"
  fi
  if has volume && has detach; then
    echo "DESTRUCTIVE  detaches a volume from its service"; return "$EX_DESTRUCTIVE"
  fi
  if has volume && { has update || has edit; }; then
    echo "DESTRUCTIVE  changes a volume in place (a resize can truncate)"; return "$EX_DESTRUCTIVE"
  fi
  # `rm` and `remove` are the aliases the volume branch above already carries; the enumeration is only as
  # good as its spellings, and `environment rm staging` destroys exactly as much as `environment delete`.
  if { has service || has environment; } && { has delete || has remove || has rm; }; then
    echo "DESTRUCTIVE  removes a service or environment"; return "$EX_DESTRUCTIVE"
  fi
  if has down; then
    echo "DESTRUCTIVE  tears the environment's deployments down"; return "$EX_DESTRUCTIVE"
  fi
  if has restore; then
    echo "DESTRUCTIVE  a restore replaces the live mount"; return "$EX_DESTRUCTIVE"
  fi

  for a in "${argv[@]}"; do
    case "$a" in
      -v|--volume)
        echo "UNDECIDABLE  names a volume — a PROJECT-level object — from an environment-scoped command"
        return "$EX_UNDECIDABLE" ;;
    esac
  done

  if [ "$verb" = "config" ] && [ "$sub" = "apply" ]; then
    echo "UNDECIDABLE  a config apply is only as safe as its plan — pass --plan FILE"
    return "$EX_NEEDS_PLAN"
  fi
  if [ "$verb" = "config" ] && [ "$sub" = "plan" ]; then
    echo "HARMLESS     config plan never mutates"; return "$EX_OK"
  fi

  # `domain` / `variables`: read-only bare, a configuration change with anything that writes. The flag
  # forms are checked against the RAW argv because the word list above has already dropped flags.
  if [[ "$verb" =~ $CONDITIONALLY_READ_ONLY_RE ]] || [[ "$sub" =~ $CONDITIONALLY_READ_ONLY_RE ]]; then
    if has delete || has remove || has rm || has unset || has add || has set; then
      echo "UNDECIDABLE  '$verb $sub' removes or rewrites configuration rather than reading it"
      return "$EX_UNDECIDABLE"
    fi
    for a in "${argv[@]}"; do
      case "$a" in
        --set|--unset|--remove|--set=*)
          echo "UNDECIDABLE  '$verb' with $a rewrites configuration rather than reading it"
          return "$EX_UNDECIDABLE" ;;
      esac
    done
    echo "HARMLESS     read-only verb"; return "$EX_OK"
  fi

  if [[ "$verb" =~ $READ_ONLY_RE ]] || [[ "$sub" =~ $READ_ONLY_RE ]]; then
    echo "HARMLESS     read-only verb"; return "$EX_OK"
  fi

  echo "UNDECIDABLE  '$verb ${sub}' is not on the read-only list"
  return "$EX_UNDECIDABLE"
}

# PROVE HARMLESS, THEN PERMIT — and prefer Railway's own word over this file's opinion of English.
#
# TWO PATHS, AND THE STRUCTURAL ONE WINS WHERE IT APPLIES. A document declaring
# `kind: "railway.config.plan"` is classified from `changeSet.changes[].severity` and nothing else; one
# that does not declare it falls back to the vocabulary matching below.
#
# `changeSet.diagnostics[].severity` IS A DIFFERENT AXIS AND MUST NOT BE CONFLATED WITH IT. Diagnostics
# carry lint severities — the captured plan's single diagnostic is a `warning` whose text is "Volumes are
# never deleted by config apply", sitting on a plan that carried seven destructive changes when it was
# captured (six; the count is incidental, the axis confusion is not). Reading
# diagnostics as change severities would classify a plan from a sentence about what apply does NOT do.
# Only `.changeSet.changes[]` is read.
#
# DEMAND THE SHAPE BEFORE TRUSTING THE ANSWER. If Railway renames or drops `.destructive` or
# `.changeSet.changes`, the checks below would otherwise evaluate to "nothing destructive found" — the
# permissive failure this file is written against. A missing or mistyped field is UNDECIDABLE, which
# costs a snapshot rather than a refusal.
#
# AND THE TWO SIGNALS MUST AGREE. The top-level `.destructive` boolean and the per-change severities are
# independent readings of one plan. `destructive: false` beside a change marked `destructive` means one of
# them is being read wrongly; neither is safe to prefer, so the guard refuses to pick.
RAILWAY_PLAN_JQ='
  def chg: .changeSet.changes;
  def bad: [chg[] | select((.severity | type) != "string"
                           or ((.severity != "safe") and (.severity != "destructive")))];
  def dest: [chg[] | select(.severity == "destructive")];
  # `field` is what makes a `resource.update` legible: the volumeAttachments re-point and a
  # startCommand edit are the same kind against the same address otherwise.
  def nameof: ((.kind // "?") + " " + (.address // .path // "?")
               + (if (.field | type) == "string" then "." + .field else "" end));

  if (type != "object") or ((.kind? // "") != "railway.config.plan") then "FALLBACK"
  elif ((.destructive | type) != "boolean")
  then "UNDECIDABLE  " + $f + " declares kind railway.config.plan but its top-level .destructive is "
       + (.destructive | type) + ", not a boolean — the plan schema has changed"
  elif ((chg | type) != "array")
  then "UNDECIDABLE  " + $f + " declares kind railway.config.plan but .changeSet.changes is "
       + (chg | type) + ", not an array — the plan schema has changed"
  elif ((bad | length) > 0)
  then "UNDECIDABLE  " + $f + " carries " + (bad | length | tostring)
       + " change(s) whose severity is neither safe nor destructive: "
       + ([bad[] | (.severity | tostring)] | unique | join(", "))
  elif (.destructive == false) and ((dest | length) > 0)
  then "UNDECIDABLE  " + $f + " says destructive=false while " + (dest | length | tostring)
       + " change(s) are marked destructive — the plan contradicts itself"
  elif ((dest | length) > 0)
  then "DESTRUCTIVE  Railway marks " + ((dest | length) | tostring) + " of "
       + ((chg | length) | tostring) + " change(s) destructive: " + ([dest[] | nameof] | join("; "))
  elif (.destructive == true)
  then "DESTRUCTIVE  the top-level destructive flag is set on this plan, though all "
       + ((chg | length) | tostring) + " change(s) are marked safe"
  else "HARMLESS     Railway marks all " + ((chg | length) | tostring) + " change(s) safe"
  end'

# THE FALLBACK, for a document that does not declare itself a Railway plan: an excerpt pasted into an
# issue, a fragment, a plan from some other tool. It walks for objects carrying a recognised ACTION KEY
# and requires every action value to come from a CLOSED SAFE SET.
#
# AND THE TWO PATHS AGREE ON WHAT IS DESTRUCTIVE. `classify_argv` calls `volume update` destructive
# because a resize can truncate, and `volume detach` destructive because it re-points storage. A plan
# fragment saying the same thing has to reach the same verdict, or the plan path is a way around the
# argv path.
#
# `kind` IS IN `akeys` because that is Railway's real action key, and a fragment lifted out of a plan
# carries it without the envelope. Railway's own action values sit in the sets below beside the generic
# spellings.
#
# DO NOT ADD `destructive` TO `dre`. `vocab` matches object KEYS as well as string values, and
# `destructive` is a top-level key on every `--out` document whatever its value — so a plan whose own
# `"destructive"` is `false` would flip from UNDECIDABLE to DESTRUCTIVE on the strength of the key
# existing. The value is read as a value, above, and never matched as vocabulary.
PLAN_JQ='
  def dre: "delete|destroy|remove|wipe|recreate|replace|detach|teardown|prune|purge|truncate|drop|restore";
  # Keys whose value names an action. Container keys ("changes", "actions", "steps") are not here: they
  # hold the entries, they are not the entry.
  def akeys: ["kind","type","op","operation","action","verb","change","changetype","change_type","effect"];
  def dactions: ["-","-/+","+/-","delete","destroy","remove","rm","down","teardown","drop","prune",
                 "purge","wipe","detach","replace","recreate","restore","truncate",
                 "resource.delete","variable.delete","volume.delete"];
  def mactions: ["update","modify","patch","edit","change","resize","~","resource.update"];
  def sactions: ["+","~","=","create","add","new","insert","update","modify","patch","edit","change",
                 "set","unchanged","no-change","nochange","no_change","noop","none","keep","skip","same",
                 "resource.create","variable.set"];

  def objs: [.. | objects];
  # Every string value AND every object key, because a key is vocabulary too: {"destroyed": true} and
  # {"delete": [...]} both say what they propose without any string saying it.
  def vocab: ([.. | strings] + [objs[] | keys_unsorted[]] | map(ascii_downcase));
  # One record per action-bearing entry: the action value, and the strings of the object it sits in, so
  # an `update` can be told apart by WHAT it updates.
  def entries:
    [ objs[]
      | . as $o
      | ($o | [.. | strings] | map(ascii_downcase) | join(" ")) as $ctx
      | $o
      | to_entries[]
      | select((.key | ascii_downcase) as $k | (akeys | index($k)) != null)
      | select(.value | type | . == "string" or . == "number" or . == "boolean")
      | { a: (.value | tostring | ascii_downcase), ctx: $ctx } ];

  if (vocab | any(test(dre)))
  then "DESTRUCTIVE  the plan proposes a delete, replace or detach"
  elif (entries | any(. as $e | (dactions | index($e.a)) != null))
  then "DESTRUCTIVE  the plan marks a resource for destruction"
  elif (entries | any(. as $e | (mactions | index($e.a)) != null and ($e.ctx | test("volume"))))
  then "DESTRUCTIVE  the plan changes a volume in place (a resize can truncate)"
  elif (entries | length) == 0
  then "UNDECIDABLE  " + $f + " carries no recognisable action key — Railway may have changed the plan schema"
  elif (entries | any(. as $e | (sactions | index($e.a)) == null))
  then "UNDECIDABLE  " + $f + " carries an action this guard does not recognise: "
       + ([entries[] | select(. as $e | (sactions | index($e.a)) == null) | .a] | unique | join(", "))
  else "HARMLESS     the plan proposes only creates and in-place changes to non-volume resources"
  end'

classify_plan() { # classify_plan <file>
  local f="$1" out=""
  [ -f "$f" ] || { echo "UNDECIDABLE  no plan file at $f"; return "$EX_UNDECIDABLE"; }
  jq -e . "$f" >/dev/null 2>&1 || { echo "UNDECIDABLE  $f is not readable JSON"; return "$EX_UNDECIDABLE"; }
  out="$(jq -r --arg f "$f" "$RAILWAY_PLAN_JQ" "$f" 2>/dev/null)" || out=""
  # Only a document that did NOT name itself a Railway plan reaches the vocabulary fallback. A real plan
  # whose schema has drifted stays UNDECIDABLE on the structural path rather than being re-read as prose,
  # which is how the previous version reached a right answer for a wrong reason.
  if [ "$out" = "FALLBACK" ]; then
    out="$(jq -r --arg f "$f" "$PLAN_JQ" "$f" 2>/dev/null)" || out=""
  fi
  # A classifier that cannot produce a verdict has not produced a harmless one.
  case "$out" in
    DESTRUCTIVE*) echo "$out"; return "$EX_DESTRUCTIVE" ;;
    HARMLESS*)    echo "$out"; return "$EX_OK" ;;
    UNDECIDABLE*) echo "$out"; return "$EX_UNDECIDABLE" ;;
    *)            echo "UNDECIDABLE  the plan classifier produced no verdict for $f"
                  return "$EX_UNDECIDABLE" ;;
  esac
}

# THE TWO SIGNALS ESCALATE. The effective verdict is the more severe of (argv, plan), never the plan
# alone. The previous shape of this function — "the plan governs unless the argv is exactly DESTRUCTIVE" —
# let a plan of pure creates clear every UNDECIDABLE argv there is, including `service rm`, `environment rm`,
# `volume attach -v mysql-data` and any verb the enumeration does not carry. Since the documented wiring
# always passes `--plan`, that made the argv classifier decorative in the only invocation the docs show.
#
# ONE EXCEPTION, and it is the reason `--plan` exists: a bare `railway config apply` returns
# EX_NEEDS_PLAN, which says "the plan is what decides this". There, and only there, the plan governs
# outright and may lower the verdict. `config apply --volume NAME` does NOT qualify: the volume-naming
# branch fires first and returns plain EX_UNDECIDABLE.
#
# No `set +e` here. Toggling errexit inside a function turns it back on for the CALLER too, so the
# `return` below would abort the script at its own call site instead of handing back a verdict — a
# classifier that exits rather than classifies. Both failures are captured with `|| code=$?`, which
# errexit exempts.
decide() { # decide <plan-or-empty> <argv...>; sets VERDICT and returns the code
  local plan="$1"; shift
  local code=0 pcode=0 pverdict="" defers=0
  VERDICT="$(classify_argv "$@")" || code=$?
  if [ "$code" -eq "$EX_NEEDS_PLAN" ]; then
    # EX_NEEDS_PLAN MAY ONLY BE LOWERED BY A PLAN THE GUARDED COMMAND WILL ACTUALLY READ. The identity
    # check closes one direction — the command naming a DIFFERENT plan. This closes the other, where it
    # names NONE, which is not "applies nothing": `railway config apply` with no `--plan` RE-EVALUATES the
    # authoring file against production and applies whatever that computes. The CLI says so itself —
    # `--plan <PLAN>  Apply this pinned plan without re-evaluating the authoring file` (5.57.2) — so
    # re-evaluation is precisely what `--plan` buys, and without it the document this guard classified is
    # not the change set about to run. Letting a HARMLESS verdict about the pin clear it would take no
    # snapshot before that fresh evaluation - and WHAT THE EVALUATION PROPOSES DEPENDS ON THE CLI RUNNING
    # IT, which nothing in this repo pins. Measured against production on 2026-09-21: CLI 5.57.2 proposes
    # `resource.delete database.mysql` - the 1.3 GB OpenEMR database, the own incident, reached through
    # the guard - while CLI 5.59.0 proposed `variable.delete mysql.MYSQL_URL` instead, until a separate change declared
    # that variable. Both are destructive,
    # both must be snapshotted, and WHICH ONE an operator's CLI emits is not a safety argument: that is
    # exactly why the disagreement is refused rather than reasoned about. The `volumeAttachments` re-point
    # was beside the 5.57.2 row; one of the two going away does not make this safe. `--file`
    # re-evaluates
    # too and names no `--plan`, so it lands here as well. The likeliest way in is an operator's hand apply
    # with the second `--plan` dropped. A separate change, DEPLOYMENT.md §10
    if [ -n "$plan" ] && [ -z "$(argv_plans "$@")" ]; then
      die_refused "the guard was given --plan '$plan', but the guarded 'config apply' names no --plan of its own, so it will RE-EVALUATE the authoring file rather than apply that pin. A verdict about the pin is not a verdict about a fresh evaluation. Add '--plan $plan' to the guarded command; if you mean to re-evaluate, drop --plan from the guard and it will snapshot first."
    fi
    defers=1; code="$EX_UNDECIDABLE"
  fi
  if [ -n "$plan" ]; then
    pverdict="$(classify_plan "$plan")" || pcode=$?
    if [ "$defers" -eq 1 ] || [ "$(severity "$pcode")" -gt "$(severity "$code")" ]; then
      VERDICT="$pverdict"; code="$pcode"
    fi
  fi
  return "$code"
}

# --- the plan the guarded command will actually read --------------------------------------------------
# `run` execs the guarded argv VERBATIM, `--plan` and all, and the documented wiring writes the filename
# twice on one line. Nothing compared the two, so
#   run --plan A -- railway config apply --plan B
# classified A, found it harmless, took no snapshot and applied B. A verdict about one document is not a
# verdict about another. A separate change
#
# EVERY occurrence is printed, not the first. `--plan <PLAN>` is a single-value clap argument
# (`ArgAction::Set`), which OVERWRITES rather than accumulates, so the CLI reads the LAST `--plan` on the
# line while a first-wins reader here would vouch for the first. Silently picking either is the permissive
# direction, and this file's own doctrine is that the enumeration is only as good as its spellings: more
# than one `--plan` is an ambiguity to refuse, not a thing to choose from. A separate change
argv_plans() { # argv_plans <argv...>; prints every --plan the guarded command names, one per line
  local a take=0
  for a in "$@"; do
    if [ "$take" -eq 1 ]; then printf '%s
' "$a"; take=0; continue; fi
    case "$a" in
      --plan)   take=1 ;;
      --plan=*) printf '%s
' "${a#--plan=}" ;;
    esac
  done
  return 0
}

# Two spellings of one file must compare equal, so compare resolved paths rather than the text the
# operator typed. A path whose directory does not exist cannot be resolved and is compared as written —
# it is about to fail anyway, and guessing would be the permissive direction.
canonical_path() { # canonical_path <path>
  local p="$1" d b r
  d="$(dirname -- "$p")"; b="$(basename -- "$p")"
  if r="$(cd -- "$d" 2>/dev/null && pwd -P)"; then printf '%s/%s' "$r" "$b"; else printf '%s' "$p"; fi
}

# A content fingerprint, so a plan REWRITTEN between the classification and the exec is caught too. The
# GitLab job runs BusyBox and GitHub's runs GNU; both carry sha256sum, and cksum is the last resort.
plan_fingerprint() { # plan_fingerprint <file>
  if command -v sha256sum >/dev/null 2>&1; then sha256sum < "$1" | cut -d' ' -f1
  elif command -v shasum  >/dev/null 2>&1; then shasum -a 256 < "$1" | cut -d' ' -f1
  else cksum < "$1" | tr -s ' ' | cut -d' ' -f1,2
  fi
}

# Refuses unless the plan the guard classified is the plan the guarded command names. Applied to
# `classify` as well as `run`: an exit code that answers a question nobody asked is the same defect
# whether or not this process goes on to exec something.
require_plan_identity() { # require_plan_identity <guard-plan> <argv...>
  local guard_plan="$1"; shift
  local -a names=(); local line
  while IFS= read -r line; do [ -n "$line" ] && names+=("$line"); done < <(argv_plans "$@")
  if [ "${#names[@]}" -gt 1 ]; then
    die_refused "the guarded command names ${#names[@]} --plan arguments (${names[*]}). The CLI keeps the LAST one and this guard can vouch for exactly one document. Name the plan once."
  fi
  [ "${#names[@]}" -eq 1 ] || return 0
  local named="${names[0]}"
  if [ -z "$guard_plan" ]; then
    die_refused "the guarded command applies '$named' but no --plan was given to this guard, so the verdict below is about something else. Pass --plan $named."
  fi
  if [ "$(canonical_path "$guard_plan")" != "$(canonical_path "$named")" ]; then
    die_refused "the guard was given --plan '$guard_plan' but the guarded command applies '$named'. A verdict about one plan is not a verdict about another."
  fi
}

# --- entry point ------------------------------------------------------------------------------------
usage() { sed -n '2,8p' "$0" >&2; exit "$EX_ENV"; }

MODE="${1:-}"; shift || usage
PLAN=""; LABEL=""; WANT_VOLUME=""
while [ $# -gt 0 ]; do
  case "$1" in
    --plan)   PLAN="${2:-}"; shift 2 ;;
    --label)  LABEL="${2:-}"; shift 2 ;;
    --volume) WANT_VOLUME="${2:-}"; shift 2 ;;
    --)       shift; break ;;
    *)        break ;;
  esac
done
[ -n "$LABEL" ] || LABEL="guard-$(date -u +%Y%m%dT%H%M%SZ)"

case "$MODE" in
  classify)
    require_plan_identity "$PLAN" "$@"
    code=0; decide "$PLAN" "$@" || code=$?
    echo "$VERDICT"
    exit "$code"
    ;;
  snapshot)
    do_snapshot "$LABEL" "$WANT_VOLUME"
    ;;
  run)
    [ $# -gt 0 ] || die_env "run needs a command after --"
    require_plan_identity "$PLAN" "$@"
    # Taken BEFORE the classification and compared after it, below.
    PLAN_FINGERPRINT=""
    [ -n "$PLAN" ] && [ -f "$PLAN" ] && PLAN_FINGERPRINT="$(plan_fingerprint "$PLAN")"
    code=0; decide "$PLAN" "$@" || code=$?
    note "CLASSIFY" "$VERDICT"
    if [ "$code" -ne "$EX_OK" ]; then
      do_snapshot "$LABEL" "$WANT_VOLUME"
    else
      note "GUARD" "classified harmless; no snapshot taken."
    fi
    # The snapshot can take fifteen minutes; a plan file is a mutable path on a shared runner. Re-read it
    # rather than trust that the document classified above is still the document about to be applied.
    if [ -n "$PLAN_FINGERPRINT" ]; then
      [ -f "$PLAN" ] || die_refused "the plan '$PLAN' disappeared after it was classified."
      [ "$(plan_fingerprint "$PLAN")" = "$PLAN_FINGERPRINT" ] \
        || die_refused "the plan '$PLAN' changed between being classified and being applied. The verdict above describes the old contents."
    fi
    # `railway variables --set KEY=VALUE` is a guarded command whose argv carries a secret, and a job log
    # is not a place to put one. Anything shaped KEY=VALUE keeps its key and loses its value.
    note "GUARD" "running: $(redact_argv "$@")"
    # `exec` replaces this process, so the EXIT trap never fires and the mktemp file leaks on every
    # permitted run. Clean up and disarm before handing over.
    rm -f "$ERRFILE" "$ERRKIND"; trap - EXIT
    exec "$@"
    ;;
  *) usage ;;
esac
