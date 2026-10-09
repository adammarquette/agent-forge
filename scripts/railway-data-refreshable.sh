#!/usr/bin/env bash
# railway-data-refreshable.sh — read the per-environment declaration of what this repository owes each
# Railway environment's data.
#
# Sourced, not run. Two scripts need answers out of the same document and must never disagree about it:
#   * railway-snapshot-guard.sh   — whether to take a verified backup before a destructive operation;
#   * railway-backup-schedules.sh — which direction `check` expects, and whether `apply` / `remove`
#                                   is the operation this environment is allowed to have done to it.
# One reader, so a change of mind is one edit to one JSON file rather than two scripts drifting.
#
# IT IS TWO QUESTIONS, AND THEY ARE INDEPENDENT. That is the correction the review forced, and it is
# the whole shape of this file:
#
#   `refreshable` — MAY THE SNAPSHOT GUARD SKIP ITS PRE-DESTRUCTION BACKUP HERE?
#   `schedules`   — SHOULD THIS ENVIRONMENT CARRY SCHEDULED (DAILY) VOLUME BACKUPS?
#
# The first version of this file answered both from `refreshable` alone, and production was exactly the
# environment where the two answers differed until a separate change declared it refreshable (2026-09-23): NO, the
# guard may not skip — its volume fill had never been measured and the guard was the only control in
# front of `railway-apply-production` — and NO, it should
# carry no schedules, because the maintainer's ruling said neither environment does. One boolean made the
# second answer YES by implication, so `check` demanded a DAILY schedule on every production volume and
# told the operator to run the `apply` that re-creates what the ruling retired. Nobody wrote that answer;
# it rode in on the other question's. Keep them apart.
#
# WHY A DECLARATION EXISTS AT ALL. The maintainer ruled on 2026-09-22 that neither environment gets
# scheduled volume backups, because the data is not critical and can be refreshed. That settles data
# PROTECTION and does not, by itself, settle the snapshot guard — the schedules were doing a second job.
# Railway caps a MANUAL backup at 50% of the volume's size and the cap bites the FIRST one, so a
# scheduled backup was also what kept the guard's manual snapshot takeable at all. Removing the
# schedules and changing nothing else leaves the guard refusing every apply against a volume past half
# full: fail-closed and loud, but a deploy that stops for a backup nobody would ever restore.
#
# So the judgement is made ONCE, per environment, per question, in writing, where a reviewer can see it —
# rather than arrived at per-deploy by a guard that cannot know.
# DEPLOYMENT.md §10
#
# IT IS PER ENVIRONMENT AND KEYED BY ENVIRONMENT ID, not by name, for the reason the rest of this
# directory is: a Railway Volume is PROJECT-scoped, a name resolves in the wrong environment perfectly
# well, and the credential is the only thing that says which environment is actually in hand. Keying on
# the id means a declaration written for staging cannot be read as one for production.
#
# EVERY FAILURE IS THE STRICT ANSWER, and the two questions' strict answers are different words for the
# same instinct: a missing file, an unparseable one, a document that does not name itself, a duplicated
# entry, an environment nobody listed, a `refreshable` that is the STRING "true" rather than the boolean,
# a `schedules` that is neither "none" nor "daily" — all of them read as NOT refreshable (keep requiring
# the snapshot) and as DAILY-expected (keep the schedules this environment was expected to carry, which
# is the earlier behaviour for an environment nobody has ruled on). Nothing here can make an
# environment refreshable by breaking, and nothing here can authorise `remove` by breaking.

# The file lives beside this one so a checkout carries it; RAILWAY_DATA_REFRESHABLE_FILE overrides the
# path, which is how the self-tests drive every branch below. It is the same kind of seam as the GraphQL
# transport in the two callers, and is not a security boundary: anyone who can set it can edit the file.
# CRLF-safe by construction: both callers source lib/jq-crlf.sh before sourcing this file, which
# overrides `jq` for the rest of their process. Sourced here too, harmless if repeated, so a direct
# source of this file for testing is not quietly exposed to the same bug — and it REFUSES unless
# pipefail is already on, which a direct source without it needs to see rather than have swallowed.
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/jq-crlf.sh" || {
    printf 'railway-data-refreshable.sh: scripts/lib/jq-crlf.sh refused to load.\n' >&2
    return 1 2>/dev/null || exit 1
}

DATA_REFRESHABLE_DEFAULT_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/railway-data-refreshable.json"

data_refreshable_file() {
  printf '%s' "${RAILWAY_DATA_REFRESHABLE_FILE:-$DATA_REFRESHABLE_DEFAULT_FILE}"
}

# data_decl_match <environment-id>
#
# The half both questions share: find this environment's ONE entry, or say why there is not one. Prints
# "ok<TAB><entry-json>" or "err<TAB><why>" and always returns 0.
#
# FACTORED RATHER THAN DUPLICATED ON PURPOSE. The file's headline promise is that the two callers cannot
# disagree about which environment is in hand; two copies of the shape checks is exactly how that promise
# would rot, one hostile-document case at a time. What each caller does with an `err` is its own — the
# strict answers differ — but WHICH entry is theirs is decided once, here.
#
# AN EMPTY ENVIRONMENT ID IS AN `err`, AND THAT LINE IS LOAD-BEARING. `.environmentId? // "" == $env`
# matches when BOTH sides are empty, so without it an empty argument against an entry that has no
# `environmentId` key would match, and the answer would come from an entry written for nothing. It is
# unreachable from either caller today — both die on an empty scope — and closed here anyway, because
# the promise above is unconditional.
data_decl_match() {
  local env="$1" f out
  if [ -z "$env" ]; then
    printf 'err\tno environment id was supplied, so no entry can be matched\n'
    return 0
  fi
  f="$(data_refreshable_file)"
  if [ ! -f "$f" ]; then
    printf 'err\tthere is no declaration file at %s\n' "$f"
    return 0
  fi
  if ! jq -e . "$f" >/dev/null 2>&1; then
    printf 'err\t%s is not readable JSON\n' "$f"
    return 0
  fi
  # DEMAND THE SHAPE BEFORE TRUSTING THE ANSWER, exactly as railway-snapshot-guard.sh does with a
  # Railway plan. A document whose `kind` is missing is some other JSON file that happens to sit at
  # this path, and reading an answer out of it would be reading an answer out of nothing.
  #
  # `tojson` rather than the entry's own fields: a tab or a newline smuggled into a `reason` would
  # otherwise split this line and let the second field be read as the first. JSON escaping forbids it.
  out="$(jq -r --arg env "$env" '
    if (type != "object") or ((.kind? // "") != "agentforge.railway.data-refreshable")
    then "err\tthe declaration does not name itself kind agentforge.railway.data-refreshable"
    elif ((.environments | type) != "array")
    then "err\tits .environments is " + (.environments | type) + ", not an array"
    else ([.environments[]? | select((.environmentId? // "") == $env)]) as $m
      | if   ($m | length) == 0 then "err\tenvironment " + $env + " is not declared in the file"
        elif ($m | length) > 1  then "err\tenvironment " + $env + " is declared "
                                     + ($m | length | tostring) + " times — refusing to pick one"
        else "ok\t" + ($m[0] | tojson)
        end
    end' "$f" 2>/dev/null)" || out=""
  # A reader that produced no verdict has not produced a permissive one.
  case "$out" in
    ok*|err*) printf '%s\n' "$out" ;;
    *)        printf 'err\tthe declaration in %s could not be read\n' "$f" ;;
  esac
  return 0
}

# data_refreshable_decl <environment-id>  —  QUESTION 1: may the snapshot guard skip its backup here?
#
# Prints "yes<TAB><reason>" or "no<TAB><why not>" and always returns 0, so a caller running under
# `set -e` can read it in a command substitution without the shell deciding the answer for it. The
# caller switches on the first field; the second is for the job log, and is the whole point — an
# operator reading "no snapshot taken" needs to see WHICH declaration said so and WHY.
#
# `.refreshable == true` IS AN EXACT BOOLEAN COMPARISON, and that is deliberate: in jq the string "true"
# is not equal to the boolean true, so a declaration written `"refreshable": "true"` — the likeliest typo
# in a hand-edited JSON file — is NOT refreshable rather than silently refreshable.
data_refreshable_decl() {
  local line kind entry out
  line="$(data_decl_match "$1")"
  kind="${line%%$'\t'*}"; entry="${line#*$'\t'}"
  if [ "$kind" != "ok" ]; then
    printf 'no\t%s — so it is treated as NOT refreshable\n' "$entry"
    return 0
  fi
  out="$(printf '%s' "$entry" | jq -r '
    if   (.refreshable == true)
    then "yes\t" + (.name // "?") + " — " + (.reason // "no reason recorded")
    elif (.refreshable == false)
    then "no\t" + (.name // "?") + " is declared NOT refreshable — " + (.reason // "no reason recorded")
    else "no\t" + (.name // "?") + " has refreshable of type " + (.refreshable | type)
         + ", not a boolean"
    end' 2>/dev/null)" || out=""
  case "$out" in
    yes*|no*) printf '%s\n' "$out" ;;
    *)        printf 'no\tthe entry for this environment could not be read\n' ;;
  esac
  return 0
}

# data_schedules_decl <environment-id>  —  QUESTION 2: should this environment carry DAILY schedules?
#
# Prints "none<TAB><reason>" or "daily<TAB><why>" and always returns 0, same contract as above, and it
# reads `schedulesReason` rather than `reason` so each answer carries the argument written FOR it. Two
# questions sharing one justification is how the first version got read as answering both.
#
# THE STRICT ANSWER HERE IS "daily", NOT "none", AND THE DIRECTION IS THE POINT. The mutation that costs
# something irreversibly is `remove`: it takes the backups away and, where the guard still snapshots,
# unfunds its first manual backup. So `remove` is gated on a declaration that positively says "none",
# and every way of failing to say it leaves this environment expecting the DAILY schedule it was expected
# to carry before any of this existed — the earlier behaviour. `apply`, which only ever ADDS a
# schedule, is the direction a broken document is allowed to leave open, and `check` goes red, not quiet.
#
# EXACT STRING COMPARISON, for the same reason `refreshable` demands the boolean: "None", "NONE", true,
# null and an absent key are all NOT "none". A declaration has to say the word to authorise a removal.
data_schedules_decl() {
  local line kind entry out
  line="$(data_decl_match "$1")"
  kind="${line%%$'\t'*}"; entry="${line#*$'\t'}"
  if [ "$kind" != "ok" ]; then
    printf 'daily\t%s — so it is treated as expecting a DAILY schedule\n' "$entry"
    return 0
  fi
  out="$(printf '%s' "$entry" | jq -r '
    if   (.schedules == "none")
    then "none\t" + (.name // "?") + " — " + (.schedulesReason // "no reason recorded")
    elif (.schedules == "daily")
    then "daily\t" + (.name // "?") + " is declared to carry DAILY schedules — "
         + (.schedulesReason // "no reason recorded")
    else "daily\t" + (.name // "?") + " declares schedules as " + (.schedules | tojson)
         + ", which is neither \"none\" nor \"daily\""
    end' 2>/dev/null)" || out=""
  case "$out" in
    none*|daily*) printf '%s\n' "$out" ;;
    *)            printf 'daily\tthe entry for this environment could not be read\n' ;;
  esac
  return 0
}

# Convenience for the callers. Each sets one (state, why) pair; a caller asks only the question it has.
data_refreshable_resolve() { # data_refreshable_resolve <environment-id>
  local line
  line="$(data_refreshable_decl "$1")"
  DATA_REFRESHABLE="${line%%$'\t'*}"
  DATA_REFRESHABLE_WHY="${line#*$'\t'}"
}

data_schedules_resolve() { # data_schedules_resolve <environment-id>
  local line
  line="$(data_schedules_decl "$1")"
  DATA_SCHEDULES="${line%%$'\t'*}"
  DATA_SCHEDULES_WHY="${line#*$'\t'}"
}
