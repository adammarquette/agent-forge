#!/usr/bin/env bash
# railway-destructive-guard.sh — turn a Railway plan into a PASS/FAIL verdict, where the only
# passing verdict is one the script can positively prove.
#
#   scripts/railway-destructive-guard.sh [--require-clean] <plan-file> [<plan-file>...]
#
# A plan file is either the pinned JSON artifact (`railway config plan --out FILE`) or the plan's
# own text log. Both are accepted and auto-detected; pass both and each has to agree.
#
# WHY THIS EXISTS
#
# `railway config plan` against production has proposed destructive changes for some time, and the
# drift job reported them the same way it reports a harmless variable rename: one red job, one
# generic "the live environment no longer matches the file" message. The plan it printed included
#
#     - Delete database mysql
#     ~ Update mysql-data config.region        ("sfo" -> "us-west2")
#
# so "resolve by deciding which side is right — the FILE is right -> re-apply it", which is what
# the drift job told the reader to do, was an instruction to delete the production database.
# Destruction is not a grade of drift; it is a different question, and it gets its own verdict.
#
# THE FIRST OF THOSE TWO ROWS IS AN ARTEFACT OF THE CLI THAT PRINTED IT, not a standing fact about
# production. `- Delete database mysql` is what CLI 5.57.2 makes of a `service("mysql", ...)` declaration;
# re-measured against production on 2026-09-21, CLI 5.59.0 emits no mysql-service row at all and proposes
# `- Delete variable mysql.MYSQL_URL` instead (gone since a separate change declared it). Still destructive, still exit 10,
# different row. The example
# moved and the class did not, which is why the verdict is separate rather than tied to a row - and the
# CLI is installed UNPINNED in CI, so the rows can move again with no diff here.
# DEPLOYMENT.md §10
#
# THE STANDING FAILURE MODE THIS IS WRITTEN AGAINST. Everything in this repository's CI fails
# PERMISSIVE when it breaks: an unparseable artifact, a renamed key, a job that produced no output
# at all. A guard that matches nothing and reports "no destructive changes" is worse than no guard,
# because it is believed. So the pass is earned, never defaulted: this script exits 0 only when at
# least one input DECIDED, every input that decided said the same thing, and that thing was "no
# destructive change". Empty, truncated, unparseable, schema-drifted and contradictory inputs are
# all UNDECIDABLE, and UNDECIDABLE is red.
#
# WHAT COUNTS AS DESTRUCTIVE IS RAILWAY'S OPINION, NOT THIS SCRIPT'S. The pinned JSON carries
# `severity: "safe" | "destructive"` on every change and a top-level `destructive` boolean; the
# text log carries the CLI's own `! N destructive change(s)` trailer and the `Plan: … N to destroy`
# header. This script reads those and does not attempt to re-derive them — a second opinion about
# which changes are dangerous would drift out of step with the first. It only adds one rule the CLI
# has no reason to state: a change line whose marker is `-` is a deletion, which catches a plan
# FRAGMENT (a paste, an excerpt in an issue) that carries no header or trailer to read. That rule is
# scoped to exactly that case — it is consulted LAST, only once the CLI's own trailer, header and
# "already up to date" line have all been looked for and none was there — so the script's own
# opinion can never overrule the CLI's. Where the CLI DID speak and this script cannot read what it
# said, the verdict is UNDECIDABLE, never a pass.
#
# ONE RULE DOES OVERRULE A "SAFE": a pinned plan that changes a service's `build` config is refused
# (exit 11, its own message), because every service here is an image, so the row builds nothing and
# only redeploys - see classify_json. Read from the JSON only; the text log is not searched for it.
#
# EXIT CODES
#   0   proven non-destructive (and, under --require-clean, proven to have no pending changes)
#   1   usage or environment error — no arguments, an unknown option, or no jq
#  10   DESTRUCTIVE — at least one input proposes removing a Railway resource or variable
#  11   UNDECIDABLE — nothing could be proven, including the case where every named plan file was
#       missing; treated exactly as destructive by every caller. Also a pinned plan that changes a
#       service's `build` config (above): refused, though Railway calls it safe
#  12   pending changes, all of them safe — only ever returned under --require-clean
#
# PORTABILITY. Kept BusyBox-safe (the earlier CI jobs ran `alpine:3.21`), where `date -d` parses nothing and
# awk is not gawk. Nothing here needs either: the parsing is grep/sed/jq. Verify with
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c 'apk add -q bash jq && bash scripts/railway-destructive-guard-selftest.sh'
#
# reference: DEPLOYMENT.md §9

set -uo pipefail

EX_OK=0
EX_ENV=1
EX_DESTRUCTIVE=10
EX_UNDECIDABLE=11
EX_CHANGES=12

REQUIRE_CLEAN=0

usage() {
    cat >&2 <<'USAGE'
usage: railway-destructive-guard.sh [--require-clean] <plan-file> [<plan-file>...]

  <plan-file>       `railway config plan --out FILE` JSON, or the plan's text log.
  --require-clean   also fail when the plan has pending changes, even if all of them are safe.
                    The drift job uses this: "matches the file" is the only passing state there.
USAGE
}

say() { printf '%s\n' "railway-destructive-guard: $*" >&2; }

# Strip the CLI's colour codes and any CRLF before matching. A literal ESC in the sed pattern
# rather than \x1b: BusyBox sed does not read that escape.
ESC=$(printf '\033')
normalize() { tr -d '\r' <"$1" | sed "s/${ESC}\[[0-9;]*[a-zA-Z]//g"; }

# EVERY YES/NO QUESTION ABOUT THE TEXT GOES THROUGH HERE, NEVER `printf ... | grep -q`. Under
# `pipefail` that pipeline's status is the WRITER's as well as grep's: `grep -q` exits on its first
# match, and if printf still has lines to write it takes SIGPIPE and the pipeline reports 141 - a
# match read as "no match". bash line-buffers printf into a pipe, so after a match on line 14 of 17
# that lost about 1 run in 20000, and every run once the text outgrows the pipe buffer. That is how a
# reworded destructive trailer intermittently read as safe drift. A here-string has no writer
# process to kill. It asks for a COUNT, not an exit status, because "no match" and "grep never ran"
# share status 1: bash spools a long here-string to a temp file, and when that cannot be created the
# redirection fails with 1 and grep is never started. Only a printed count is an answer; anything
# else ends the classifier UNDECIDABLE. Callers run inside `$(classify_text ...)`, so the exit
# leaves that subshell and nothing else.
has() {
    n=$(grep -cE "$1" <<<"$text")
    case "$n" in
        '' | *[!0-9]*)
            say "$file: could not search the plan text for '$1' - no count came back, which is not a 'no'."
            echo undecidable
            exit 0
            ;;
    esac
    [ "$n" -gt 0 ]
}

# --- per-file classification --------------------------------------------------------------------
# Each classifier echoes exactly one word: destructive | redeploy | changes | clean | undecidable
# and may print its reasoning to stderr.

# A change to a service's build config: the CLI's `field`, or its path `resources.service.<name>.build`.
BUILD_ROW='.field == "build" or ((.path // "" | split(".")) as $p | $p[0] == "resources" and $p[1] == "service" and $p[3] == "build")'

classify_json() {
    file="$1"

    if ! jq -e . "$file" >/dev/null 2>&1; then
        say "$file: not valid JSON."
        echo undecidable
        return
    fi

    kind=$(jq -r '.kind // empty' "$file")
    if [ "$kind" != "railway.config.plan" ]; then
        say "$file: kind is '${kind:-<absent>}', not 'railway.config.plan' - this is not a pinned plan."
        echo undecidable
        return
    fi

    # The schema check is the guard's own guard. If Railway renames or drops any of these, the
    # checks below would silently evaluate to "nothing destructive found" - the permissive failure
    # this file exists to prevent. Demand the shape first, and go red when it is not there.
    if [ "$(jq -r 'if (.destructive | type) == "boolean" then "ok" else "no" end' "$file")" != "ok" ]; then
        say "$file: top-level .destructive is missing or not a boolean - plan schema changed."
        echo undecidable
        return
    fi
    if [ "$(jq -r 'if (.changeSet.changes | type) == "array" then "ok" else "no" end' "$file")" != "ok" ]; then
        say "$file: .changeSet.changes is missing or not an array - plan schema changed."
        echo undecidable
        return
    fi
    unknown=$(jq -r '[.changeSet.changes[] | select((.severity | type) != "string" or ((.severity != "safe") and (.severity != "destructive")))] | length' "$file")
    if [ "$unknown" != "0" ]; then
        say "$file: $unknown change(s) carry a severity that is neither 'safe' nor 'destructive'."
        say "  A severity this script does not understand cannot be assumed harmless."
        echo undecidable
        return
    fi

    flag=$(jq -r '.destructive' "$file")
    count=$(jq -r '[.changeSet.changes[] | select(.severity == "destructive")] | length' "$file")
    total=$(jq -r '.changeSet.changes | length' "$file")

    # The two signals are independent, so disagreement means one of them is being read wrongly.
    # Neither reading is safe to prefer, so refuse rather than pick.
    if [ "$flag" = "false" ] && [ "$count" != "0" ]; then
        say "$file: .destructive is false but $count change(s) are marked destructive - contradictory plan."
        echo undecidable
        return
    fi

    if [ "$flag" = "true" ] || [ "$count" != "0" ]; then
        say "$file: $count destructive change(s):"
        jq -r '.changeSet.changes[] | select(.severity == "destructive") | "    " + .kind + "  " + .summary' "$file" >&2
        echo destructive
        return
    fi

    # A `build` ROW IS A REDEPLOY THAT BUILDS NOTHING, SO IT IS REFUSED THOUGH RAILWAY CALLS IT SAFE -
    # the one place this script overrules the CLI. Every service railway.ts declares is image-sourced
    # (sites-volume-selftest.mjs pins that premise), and Railway ignores build config for an image
    # source, but applying the row still redeploys the service onto a fresh host. openemr's re-proposed
    # itself on every staging apply and one redeploy landed 10 ms from mysql, making FHIR 10x slower.
    # Declare the value the environment stores instead (railway.ts, OPENEMR_STORED_BUILD_BY_ENV).
    # DEPLOYMENT.md §9
    builds=$(jq -r "[.changeSet.changes[] | select($BUILD_ROW)] | length" "$file")
    case "$builds" in
        '' | *[!0-9]*)
            say "$file: could not count build changes in the plan."
            echo undecidable
            return
            ;;
    esac
    if [ "$builds" != "0" ]; then
        say "$file: $builds change(s) to a service's build config, on a project whose services are all images:"
        jq -r ".changeSet.changes[] | select($BUILD_ROW) | \"    \" + .summary" "$file" >&2
        echo redeploy
        return
    fi

    if [ "$total" = "0" ]; then
        echo clean
    else
        say "$file: $total pending change(s), all marked safe."
        echo changes
    fi
}

classify_text() {
    file="$1"
    text=$(normalize "$file")

    if [ -z "$(printf '%s' "$text" | tr -d '[:space:]')" ]; then
        say "$file: empty. A plan that produced no output proves nothing."
        echo undecidable
        return
    fi

    # Destructive evidence first, in the CLI's own words: the trailer, then the header's destroy count.
    trailer=$(printf '%s\n' "$text" | sed -n 's/.*[^0-9]\([0-9][0-9]*\) destructive change(s).*/\1/p' | head -1)
    if [ -n "$trailer" ] && [ "$trailer" != "0" ]; then
        say "$file: the CLI reports $trailer destructive change(s)."
        echo destructive
        return
    fi
    destroy=$(printf '%s\n' "$text" | sed -n 's/^[[:space:]]*Plan:.*[^0-9]\([0-9][0-9]*\) to destroy.*/\1/p' | head -1)
    if [ -n "$destroy" ] && [ "$destroy" != "0" ]; then
        say "$file: the plan header reports $destroy resource(s) to destroy."
        echo destructive
        return
    fi

    # THE TRAILER GETS THE SAME TREATMENT AS THE HEADER AND THE JSON SCHEMA: if the text says
    # something about destruction that this script could not turn into a count, REFUSE. It is the
    # only signal for a whole class of destruction - `Plan: N to destroy` counts DELETES, not
    # destructive UPDATES. Production's plan USED TO read `3 to destroy` against `! 6 destructive
    # change(s)`, the three region rewrites appearing in neither the destroy count nor the `-` markers.
    # NO CURRENT COUNT IS QUOTED HERE, deliberately: a plan's contents depend on the RAILWAY CLI
    # VERSION that produced it, which nothing in this repo pins - 5.57.2 and 5.59.0 disagree about
    # whether a resource is being deleted, on the same commit and environment, under either
    # credential. The class does not depend on the example: a trailer reworded to `3 destructive
    # changes` would still fall through every branch below and be reported as safe drift, which is
    # what this check exists to stop.
    if [ -z "$trailer" ] && has 'destructive|will remove Railway resources'; then
        say "$file: the plan states something about destructive changes, but no count could be read"
        say "  out of it. The expected wording is '! N destructive change(s)'. The line found was:"
        printf '%s\n' "$text" | grep -E 'destructive|will remove Railway resources' | head -3 | sed 's/^/    /' >&2
        say "  A destruction claim this script cannot count is not a claim that there is none."
        echo undecidable
        return
    fi

    # THE CONTRADICTION REFUSAL SITS ABOVE BOTH CLEAN-CLAIMING BRANCHES, NOT INSIDE ONE. Two
    # different lines can claim nothing is being destroyed - the CLI's `already up to date`
    # sentence, and an all-zero `Plan:` header - and a document carrying the CLI's own deletion
    # marker contradicts either of them. The first version of this check lived inside the header
    # branch only, so `- Delete database mysql` under an `already up to date` line - the STRONGER
    # of the two claims - walked straight past it into the only passing verdict. Placed here it
    # covers both, and cannot be defeated by reordering the branches below either. It is the same
    # refusal classify_json makes when .destructive contradicts .severity: two signals disagree,
    # and neither reading is safe to prefer.
    deletions=$(printf '%s\n' "$text" | grep -E '^[[:space:]]*-[[:space:]]+(Delete|Destroy|Remove)[[:space:]]')

    # The change lines as the CLI prints them: INDENTED under the header, one marker, one verb.
    # Scoped to the header and below, and to indented lines, on purpose - CI captures the
    # plan with stderr merged into it, and an unindented wrapped warning beginning `- word` is
    # prose, not a change line.
    body_changes=$(printf '%s\n' "$text" | sed -n '/^[[:space:]]*Plan:/,$p' |
        grep -E '^[[:space:]][[:space:]]*[-+~%][[:space:]][[:space:]]*[A-Za-z]')

    claims_none=0
    has 'Railway configuration is already up to date' && claims_none=1
    [ "${destroy:-}" = "0" ] && claims_none=1
    if [ "$claims_none" -eq 1 ] && [ -n "$deletions" ]; then
        say "$file: the plan claims nothing is being destroyed and lists deletion line(s) anyway:"
        printf '%s\n' "$deletions" | sed 's/^/    /' >&2
        say "  One of the two is being read wrongly, and neither is safe to prefer."
        echo undecidable
        return
    fi

    # "Already up to date" is the CLI's own words for a clean plan, and the only text that earns a
    # pass here. Anything else that merely lacks destructive markers has not been proven clean.
    if has 'Railway configuration is already up to date'; then
        echo clean
        return
    fi

    header=$(printf '%s\n' "$text" | grep -E '^[[:space:]]*Plan:' | head -1)
    if [ -n "$header" ]; then
        # A COUNT CATEGORY THIS SCRIPT DOES NOT KNOW IS A FORMAT CHANGE, and it gets the same
        # answer a renamed severity gets in classify_json. The three positional reads below parse
        # `Plan: 0 to add, 0 to change, 0 to destroy, 2 to replace` perfectly happily and return
        # `clean`, with `2 to replace` sitting unread on the very same line - three integers
        # outranking the body of the plan, which is the failure this file is written against. Read
        # the header's WHOLE count vocabulary, not the three words this script happens to ask
        # about, and refuse on a fourth.
        unknown_counts=$(printf '%s' "$header" |
            grep -oE '[0-9][0-9]*[[:space:]][[:space:]]*to[[:space:]][[:space:]]*[A-Za-z][A-Za-z]*' |
            sed 's/^[0-9][0-9]*[[:space:]]*to[[:space:]]*//' | grep -vxE 'add|change|destroy')
        if [ -n "$unknown_counts" ]; then
            say "$file: the plan header names count(s) this script does not know:" \
                "$(printf '%s' "$unknown_counts" | tr '\n' ' ')"
            say "  Header: $(printf '%s' "$header" | sed 's/^[[:space:]]*//')"
            say "  A count category that goes unread is exactly as blind as a missing one."
            echo undecidable
            return
        fi

        # A header that no longer names all three counts - or names them in a shape the reads above
        # cannot parse - is a format change, and the "N to destroy" read would have quietly found
        # nothing. Refuse instead. All three counts are parsed, not just probed for, because an
        # unparseable count is exactly as blind as an absent one.
        add=$(printf '%s' "$header" | sed -n 's/.*[^0-9]\([0-9][0-9]*\) to add.*/\1/p')
        change=$(printf '%s' "$header" | sed -n 's/.*[^0-9]\([0-9][0-9]*\) to change.*/\1/p')
        if [ -n "$add" ] && [ -n "$change" ] && [ -n "$destroy" ]; then
            # An all-zero header is the OTHER shape of "no drift". `✓ Your Railway configuration is
            # already up to date.` was taken from the CLI's format strings and has never been
            # observed here, because production has been drifted throughout; if the CLI prints the
            # header instead, treating it as pending changes would leave railway-drift red forever
            # on a healthy environment - and its own message would contradict the counts it just
            # printed. Read the counts rather than depend on one unverified sentence.
            if [ "$add" = "0" ] && [ "$change" = "0" ] && [ "$destroy" = "0" ]; then
                # ... and only where the rest of the document agrees with it. A header that
                # counts nothing, printed above the change lines it is supposed to be counting,
                # contradicts itself exactly as `0 to destroy` beside a `- Delete` line does - and
                # the same answer is owed to both.
                if [ -n "$body_changes" ]; then
                    say "$file: the header counts nothing, and the plan lists change line(s) anyway:"
                    printf '%s\n' "$body_changes" | sed 's/^/    /' >&2
                    say "  One of the two is being read wrongly, and neither is safe to prefer."
                    echo undecidable
                    return
                fi
                echo clean
                return
            fi
            say "$file:${header#*Plan:} pending, none destructive."
            echo changes
            return
        fi
        say "$file: plan header is '$(printf '%s' "$header" | sed 's/^[[:space:]]*//')' - not the expected"
        say "  'Plan: N to add, N to change, N to destroy'. The counts could not be read."
        echo undecidable
        return
    fi

    # THE FRAGMENT RULE, AND IT IS LAST ON PURPOSE. This is the one rule that is the script's own
    # opinion rather than the CLI's, so it only speaks where the CLI has not: control reaches here
    # only when there is no trailer, no `Plan:` header and no "already up to date" line, i.e. the
    # excerpt-in-an-issue case the header comment describes. Run earlier and unscoped, it outranked
    # the CLI's own "already up to date" statement, so one wrapped stderr warning beginning `- word`
    # - and the earlier CI captured stderr into this same file - reported a plan the CLI called clean as one
    # that deletes production.
    if has '^[[:space:]]*-[[:space:]]+[A-Za-z]'; then
        say "$file: no plan envelope, and the fragment contains deletion line(s):"
        printf '%s\n' "$text" | grep -E '^[[:space:]]*-[[:space:]]+[A-Za-z]' | sed 's/^/    /' >&2
        echo destructive
        return
    fi

    say "$file: no plan header, no 'already up to date' line and no destructive markers."
    say "  This is not a plan this script can read - it may be a crash log or a truncated job log."
    echo undecidable
}

# --- main ----------------------------------------------------------------------------------------

# Options are accepted ANYWHERE among the file arguments, and an unrecognised `-*` is a hard error
# wherever it appears. The loop used to stop at the first non-option, so `guard plan.txt
# --require-clean` ran WITHOUT the flag and reported the flag as a missing file - "--require-clean:
# does not exist - skipping", which reads benign - and then exited 0 on drift. A gate that quietly
# drops half its instruction is the permissive failure this file is written against.
files=()
end_of_options=0
while [ $# -gt 0 ]; do
    if [ "$end_of_options" -eq 0 ]; then
        case "$1" in
            --require-clean) REQUIRE_CLEAN=1; shift; continue ;;
            -h | --help) usage; exit "$EX_OK" ;;
            --) end_of_options=1; shift; continue ;;
            -*) say "unknown option: $1"; usage; exit "$EX_ENV" ;;
        esac
    fi
    files+=("$1")
    shift
done

[ "${#files[@]}" -gt 0 ] || { usage; exit "$EX_ENV"; }

command -v jq >/dev/null 2>&1 || {
    say "jq is not installed. A JSON plan cannot be read without it, and guessing is not an option."
    exit "$EX_ENV"
}
# shellcheck source=scripts/lib/jq-crlf.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib/jq-crlf.sh" || {
    say "scripts/lib/jq-crlf.sh refused to load."
    exit "$EX_ENV"
}

decided=0
destructive=0
undecidable=0
redeploy=0
changes=0

for file in "${files[@]}"; do
    if [ ! -f "$file" ]; then
        # Absent is not the same as clean, and not the same as broken either: the drift job passes
        # both the JSON and the text log, and only the text log is guaranteed to exist if the CLI
        # died early. A missing input is reported and skipped; if NOTHING decided, the run is red.
        say "$file: does not exist - skipping. It proves nothing either way."
        continue
    fi

    first=$(sed -e 's/^[[:space:]]*//' "$file" | grep -v '^$' | head -1)
    case "$first" in
        '{'*) verdict=$(classify_json "$file") ;;
        *) verdict=$(classify_text "$file") ;;
    esac

    decided=$((decided + 1))
    case "$verdict" in
        destructive) destructive=$((destructive + 1)) ;;
        undecidable) undecidable=$((undecidable + 1)) ;;
        redeploy) redeploy=$((redeploy + 1)) ;;
        changes) changes=$((changes + 1)) ;;
        clean) ;;
        *) say "internal error: classifier returned '$verdict'"; exit "$EX_UNDECIDABLE" ;;
    esac
done

if [ "$destructive" -gt 0 ]; then
    echo ""
    echo "ERROR: this Railway plan is DESTRUCTIVE. It removes resources or variables."
    echo ""
    echo "  Do NOT resolve this by applying the file. 'the FILE is right -> re-apply it' is the"
    echo "  right instruction for ordinary drift and the wrong one here: applying a destructive"
    echo "  plan deletes live data, and Railway volume backups are not automatic."
    echo ""
    echo "  Read the plan above and decide, per destructive line, which side is wrong:"
    echo "    - the LIVE resource should not exist -> say so explicitly, snapshot first, and apply"
    echo "      by hand with a human watching."
    echo "    - the FILE is wrong about it -> fix .railway/railway.ts so the plan stops proposing"
    echo "      the deletion, and merge that. This job going green is the acceptance test."
    echo ""
    echo "  reference: DEPLOYMENT.md section 9,"
    exit "$EX_DESTRUCTIVE"
fi

if [ "$undecidable" -gt 0 ] || [ "$decided" -eq 0 ]; then
    echo ""
    echo "ERROR: this Railway plan could not be read, so nothing about it has been proven."
    echo ""
    echo "  That is a FAILURE on purpose. A plan checker that cannot parse its input and passes"
    echo "  anyway reports 'no destructive changes' about a file it never understood, and that"
    echo "  report is believed. See the reasoning at the top of scripts/railway-destructive-guard.sh."
    echo ""
    echo "  Usual causes: the plan command died before printing anything (check the log above for"
    echo "  an auth or network error), the artifact expired, or Railway changed the plan format -"
    echo "  in which case this script is what needs updating, in its own pull request."
    exit "$EX_UNDECIDABLE"
fi

if [ "$redeploy" -gt 0 ]; then
    echo ""
    echo "ERROR: this Railway plan changes a service's build config, and every service here is an image."
    echo ""
    echo "  Railway builds nothing for an image service, so the row changes nothing - but applying it"
    echo "  still redeploys the service, onto whatever host it draws. openemr's did that on every staging"
    echo "  apply, and one draw put it 10 ms from mysql and made FHIR ten times slower."
    echo ""
    echo "  Do not apply it to make it go away: an apply writing null did not persist, so it came back."
    echo "  Declare the value the environment stores in .railway/railway.ts, so the plan converges"
    echo "  without an apply (OPENEMR_STORED_BUILD_BY_ENV is the example)."
    echo ""
    echo "  reference: DEPLOYMENT.md section 9,"
    exit "$EX_UNDECIDABLE"
fi

if [ "$REQUIRE_CLEAN" -eq 1 ] && [ "$changes" -gt 0 ]; then
    echo ""
    echo "ERROR: the live environment no longer matches .railway/railway.ts."
    echo "Someone changed it outside this repository, or a merged change was never applied."
    echo "None of the pending changes is destructive."
    echo ""
    echo "Resolve by deciding which side is right:"
    echo "  - the FILE is right  -> re-apply it. NOTHING IN THIS PIPELINE APPLIES ON A SCHEDULE."
    echo "    Every push to DEVELOP applies to the STAGING environment, via the staging apply,"
    echo "    which deploys the images that push published. A merge to the staging"
    echo "    BRANCH applies nothing - it only gates main."
    echo "    Production applies only when a pull request merges into main (the production apply"
    echo "    workflow, once its Railway token exists and a reviewer approves). To re-apply it by hand from main's head:"
    echo "      npm ci && railway link && railway config plan --out railway-plan.json 2>&1 | tee railway-plan.txt"
    echo "    then THIS script over that plan - STOP unless it exits 0; production is data-refreshable"
    echo ", so nothing after it refuses a destructive plan:"
    echo "      bash scripts/railway-destructive-guard.sh railway-plan.json railway-plan.txt"
    echo "    and then THE GUARDED ENTRY POINT, never the bare apply:"
    echo "      scripts/railway-snapshot-guard.sh run --plan railway-plan.json \\"
    echo "        -- railway config apply --plan railway-plan.json"
    echo "  - the LIVE change is right -> promote it into .railway/railway.ts in a pull request"
    echo ""
    echo "Known exception: the reverse-proxy's GENERATED domain is outside the planned graph and"
    echo "cannot drift-report. See DEPLOYMENT.md section 9."
    exit "$EX_CHANGES"
fi

if [ "$changes" -gt 0 ]; then
    echo "railway-destructive-guard: pending changes, none destructive."
else
    echo "railway-destructive-guard: no destructive changes, and no pending changes."
fi
exit "$EX_OK"
