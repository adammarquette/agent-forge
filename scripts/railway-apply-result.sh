#!/usr/bin/env bash
# railway-apply-result.sh — run `railway config apply ... --json` and fail unless Railway says the
# change set was APPLIED. The CLI's own exit code and message cannot be trusted for this.
#
#   scripts/railway-apply-result.sh run --out FILE -- <railway config apply ... --json>
#   scripts/railway-apply-result.sh check FILE
#
# WHY THIS EXISTS. `railway config apply --plan FILE` prints "Applied pinned Railway
# configuration." and exits 0 for EVERY result except `noop` - including `failed`,
# `partially_applied`, and an `applied` result in which individual changes report `failed`.
# railwayapp/cli src/commands/config/runner.rs, `apply_pinned_plan`, in 5.57.12, 5.59.0 and 5.63.4: it
# checks the status for `noop` and nothing else. The live path (no --plan) routes the same result
# through `record_apply_outcome` in src/iac/engine.rs, which does turn those statuses into an error.
# railwayapp/config@v1 runs the same `railway config apply --plan`, so it inherits the blind spot.
#
# What that cost: a staging apply in CI applied 15 creates to a fresh environment, the
# CLI printed success, and Railway had committed only the project-level half - nine service records,
# no instances, no volumes, no deployments. Railway's reason was thrown away with the result.
# railway-deploy-identity.sh went red (UNDECIDABLE), which is what surfaced it; but it could not say
# why, and in a populated environment one moved deployment id would have made it pass.
#
# WHAT IT DOES. `run` executes its argv with stdout captured to FILE (stderr passes through), then
# judges FILE exactly as `check` does. The argv must carry `--json`: without it the CLI prints prose
# that cannot be judged, so the wiring is refused rather than passed. The document is taken from the
# first line that is exactly `{` to the end, so a wrapper's stdout above it (railway-snapshot-guard.sh
# prints BACKUP lines there when it snapshots) does not hide it.
#
# THE VERDICT, fail closed - only two shapes pass:
#   status "applied", and no change reports status "failed"       -> APPLIED
#   status "noop",    and the change list is empty                 -> NOOP (an empty pinned plan)
# Anything else is red: `failed`, `partially_applied` (whatever its changes say), `staged` (nothing was
# deployed), `applying`, any status this script has not met, an `applied` with a failed change (exit
# 12); a missing or non-string status, no `changes` array, or unreadable output (exit 11).
# scripts/railway-apply-result-selftest.sh drives `check` over each of these shapes. Each change's kind, path,
# summary and status are printed, and Railway's diagnostics with them - never the raw document, whose
# `outputs` are Railway's and are not ours to echo into a public log.
#
# EXIT CODES
#   0   APPLIED or NOOP
#   1   usage or environment error (bad arguments, no jq, argv without --json)
#  11   UNDECIDABLE - the output is not a result document this script can read
#  12   NOT APPLIED - Railway answered, and the answer was not a complete apply
#   *   the wrapped command's own non-zero exit code, handed back unchanged
#
# Under railway-deploy-identity.sh `run`, a non-zero code from this script is handed back unchanged
# and no deploy assertion follows, so the job reports the apply's failure rather than a later
# "nothing moved".
#
# DEPLOYMENT.md §9

set -uo pipefail

EX_OK=0
EX_ENV=1
EX_UNDECIDABLE=11
EX_NOT_APPLIED=12

say() { printf '%s\n' "railway-apply-result: $*" >&2; }
red() { printf '\033[31m%s\033[0m %s\n' "$1" "$2" >&2; }

usage() {
    cat >&2 <<'USAGE'
usage: railway-apply-result.sh run --out FILE -- <railway config apply ... --json>
       railway-apply-result.sh check FILE
USAGE
}

command -v jq >/dev/null 2>&1 \
    || { red "CANNOT RUN" "jq is required and is not on PATH."; exit "$EX_ENV"; }
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" \
    || { red "CANNOT RUN" "scripts/lib/jq-crlf.sh refused to load."; exit "$EX_ENV"; }

# show_unreadable <file>: what an unjudgeable output looked like, WITHOUT echoing a result document,
# whose `outputs` are Railway's and are not ours to print. Its size, and only the lines above the first
# line that opens a JSON object (a wrapper's own messages, or the CLI's prose when --json was lost).
show_unreadable() {
    local file="$1" bytes
    bytes="$(wc -c <"$file" | tr -d ' ')"
    printf '    (%s byte(s) of output; anything from the first line that opens a JSON object is withheld)\n' "$bytes" >&2
    sed -n '/^[[:space:]]*{/q; 1,20p' "$file" | sed 's/^/    | /' >&2
}

# judge <file>: print the result's rows and decide.
judge() {
    local file="$1" doc status failed nchanges
    [ -s "$file" ] || { red "UNDECIDABLE" "the apply wrote no result to $file."; return "$EX_UNDECIDABLE"; }
    doc="$(sed -n '/^{[[:space:]]*$/,$p' "$file")"
    [ -n "$doc" ] || doc="$(cat "$file")"
    if ! printf '%s' "$doc" | jq -e 'type == "object"' >/dev/null 2>&1; then
        red "UNDECIDABLE" "the apply's output is not a JSON result document (was --json dropped?)."
        show_unreadable "$file"
        return "$EX_UNDECIDABLE"
    fi
    # The fields this verdict rests on must have the CLI's shape. A document without a `changes` array
    # (a renamed field, a schema change) is not an empty change list: it is a document this script does
    # not know how to read, so it cannot vouch for it.
    if ! printf '%s' "$doc" | jq -e '(.changes | type) == "array" and (.status | type) == "string"' >/dev/null 2>&1; then
        red "UNDECIDABLE" "the result has no string 'status' or no 'changes' array - not the shape railway-apply-result.sh was written against (CLI 5.63.4). Refusing to judge it."
        return "$EX_UNDECIDABLE"
    fi
    status="$(printf '%s' "$doc" | jq -r '.status')"
    failed="$(printf '%s' "$doc" | jq '[.changes[] | select(.status == "failed")] | length')"
    nchanges="$(printf '%s' "$doc" | jq '.changes | length')"

    say "Railway reports status '$status', $nchanges change(s), $failed failed."
    printf '%s' "$doc" | jq -r '.changes[]? | "    [\(.status // "?")] \(.kind // "?") \(.path // "") - \(.summary // "")"' >&2
    printf '%s' "$doc" | jq -r '.diagnostics[]? | "    diagnostic: \(if type == "object" then ((.severity // "") + " " + (.code // "") + " " + (.path // "") + " " + (.message // "")) else tostring end)"' >&2

    if [ "$status" = "applied" ] && [ "$failed" = "0" ]; then
        printf '\033[32mAPPLIED\033[0m %s\n' "every change Railway reported was applied." >&2
        return "$EX_OK"
    fi
    if [ "$status" = "noop" ] && [ "$nchanges" = "0" ]; then
        printf '\033[32mNOOP\033[0m %s\n' "the pinned plan was empty; nothing to apply." >&2
        return "$EX_OK"
    fi
    red "NOT APPLIED" "Railway answered '$status' with $failed failed change(s). The CLI's 'Applied' message is not a result: the rows above are Railway's own, and the environment holds only what they say was applied."
    return "$EX_NOT_APPLIED"
}

mode="${1:-}"
[ "$#" -gt 0 ] && shift
case "$mode" in
run)
    out=""
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --out) [ "$#" -ge 2 ] || { usage; exit "$EX_ENV"; }; out="$2"; shift 2 ;;
            --) shift; break ;;
            *) say "unknown argument '$1'."; usage; exit "$EX_ENV" ;;
        esac
    done
    [ -n "$out" ] || { say "run needs --out FILE."; usage; exit "$EX_ENV"; }
    [ "$#" -gt 0 ] || { say "run needs a command after --."; usage; exit "$EX_ENV"; }
    json=no
    for a in "$@"; do [ "$a" = "--json" ] && json=yes; done
    [ "$json" = yes ] || { red "REFUSED" "the wrapped command has no --json, so its result cannot be judged: $*"; exit "$EX_ENV"; }
    say "running: $*"
    "$@" >"$out"
    rc=$?
    if [ "$rc" != "0" ]; then
        say "the apply exited $rc."
        show_unreadable "$out"
        exit "$rc"
    fi
    judge "$out"
    exit $?
    ;;
check)
    [ "$#" -eq 1 ] || { usage; exit "$EX_ENV"; }
    judge "$1"
    exit $?
    ;;
-h|--help|help)
    usage; exit "$EX_OK"
    ;;
*)
    say "unknown mode '$mode'."
    usage; exit "$EX_ENV"
    ;;
esac
