#!/usr/bin/env bash
# railway-destructive-guard-selftest.sh — prove the destructive-plan gate goes red for the reason
# it exists, and still goes green on a plan that is genuinely harmless.
#
# The gate's whole value is that it fails CLOSED. Every assertion below is therefore about a way it
# could fail OPEN instead: a plan fragment with no header, a renamed severity, a dropped key, an
# empty artifact, a crash log, a plan that contradicts itself, a reworded trailer. Each of those has
# to be red. The green assertions exist so the gate cannot be "fixed" by making it refuse
# everything, which would be just as useless and much more obvious - and two of them pin a plan the
# CLI itself calls clean, which an over-eager rule had reported as a plan that deletes production.
#
# The fixtures are NOT invented. The destructive text plan and the destructive JSON below are the
# real output of `railway config plan --verbose` / `--out` against this project's production
# environment on 2026-09-18 (trimmed, variable values already redacted by the CLI). The four-line
# fragment is the excerpt quoted. The mutants are that same capture with ONE thing
# changed - a reworded trailer, a deleted marker line, a zero count - which is what makes them
# evidence rather than illustration. reference: DEPLOYMENT.md §9
#
# Run it: bash scripts/railway-destructive-guard-selftest.sh
# Under the image CI actually uses:
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c \
#     'apk add -q bash jq && bash scripts/railway-destructive-guard-selftest.sh'

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/railway-destructive-guard.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

fails=0
# HOW MANY CASES RAN, because "no FAIL line" is the shape of both a pass and a no-op: a suite that
# died before its first case prints exactly what a green one prints. Reported below, and a run that
# reached no case at all is red: read a gate's POSITIVE statement.
ran=0

# expect <exit> <description> [guard args...]
expect() {
    want="$1"
    what="$2"
    ran=$((ran + 1))
    shift 2
    out=$(bash "$GATE" "$@" 2>&1)
    got=$?
    if [ "$got" -ne "$want" ]; then
        printf '\033[31mSELF-TEST FAILED\033[0m  %s — expected exit %s, got %s\n' "$what" "$want" "$got" >&2
        printf '%s\n' "$out" | sed 's/^/      /' >&2
        fails=$((fails + 1))
    fi
}

# --- fixtures ------------------------------------------------------------------------------------

# The excerpt a separate change was filed with: no header, no trailer, no envelope. A guard that only reads
# the CLI's summary lines sees nothing here and reports success.
cat >"$tmp/fragment.txt" <<'EOF'
- Delete database mysql
~ Update mysql-data config.region        ("sfo" -> "us-west2")
~ Update postgres-data config.region     ("sfo" -> "us-west2")
~ Update dataprotection-keys config.region ("sfo" -> "us-west2")
EOF

# The real thing, as the drift job's railway-drift.txt holds it.
cat >"$tmp/real-destructive.txt" <<'EOF'
Railway configuration
Using /builds/adammarquette/agent-forge/.railway/railway.ts
Project fearless-abundance
Environment production

Warning warning: resources.volume.openemr-volume-ceSx: Volume openemr-volume-ceSx exists on Railway but is not declared in the config.
Plan: 2 to add, 7 to change, 3 to destroy
  + Create service mysql
  + Update variable postgres.POSTGRES_DB
    └ postgres.POSTGRES_DB (preserve() → «hidden»)
  - Delete variable openemr.DEPLOY_NONCE
  ~ Update mysql-data config.region
    └ config.region ("sfo" → "us-west2")
  + Create volume openemr-sites
  ~ Update postgres-data config.region
    └ config.region ("sfo" → "us-west2")
  - Delete database mysql

! 7 destructive change(s) will remove Railway resources or variables.

Next
  • Run railway config apply to apply these changes.
EOF

# THE TRAILER, AND NOTHING ELSE. real-destructive.txt and clean-sentence-in-destructive-plan.txt are
# real captures and genuinely destructive by both signals at once, so neither proves the trailer
# check itself does anything: the header's own destroy count backstops it in both. This one
# zeroes the header count and drops every `- Delete` line, so the trailer regex is the only thing
# that can call it destructive.
cat >"$tmp/trailer-only.txt" <<'EOF'
Railway configuration
Project fearless-abundance
Environment production

Plan: 2 to add, 7 to change, 0 to destroy
  ~ Update mysql-data config.region
    └ config.region ("sfo" → "us-west2")

! 3 destructive change(s) will remove Railway resources or variables.

Next
  • Run railway config apply to apply these changes.
EOF

# The CLI's own words for "no drift".
cat >"$tmp/clean.txt" <<'EOF'
Railway configuration
Using /builds/adammarquette/agent-forge/.railway/railway.ts
Project fearless-abundance
Environment production

✓ Your Railway configuration is already up to date.
EOF

# Drift, but nothing is being removed.
cat >"$tmp/safe-changes.txt" <<'EOF'
Railway configuration
Project fearless-abundance
Environment production

Plan: 1 to add, 1 to change, 0 to destroy
  + Create volume scratch
  ~ Update agent-forge-api deploy.healthcheckPath
    └ deploy.healthcheckPath (null → "/health")

Next
  • Run railway config apply to apply these changes.
EOF

# The plan command died. This is the shape that must never be read as "clean".
cat >"$tmp/crash.txt" <<'EOF'
error: Unauthorized. Run `railway login`, set RAILWAY_API_TOKEN, or set RAILWAY_TOKEN.
EOF

: >"$tmp/empty.txt"

# A header whose counts Railway renamed. The "N to destroy" read finds nothing, which must not be
# mistaken for "nothing to destroy".
cat >"$tmp/header-changed.txt" <<'EOF'
Plan: 1 added, 2 changed, 3 removed
  ~ Update mysql-data config.region
EOF

# THE MUTANT THAT USED TO PASS 24/24. Production's real plan with the three `- Delete` lines taken
# out and the trailer reworded `change(s)` -> `changes`. Everything still destructive is a `~ Update`
# carrying no marker, and `N to destroy` counts deletes only, so the trailer is the ONLY signal left.
# Before the fix this classified as safe drift and the guard told the operator to apply it.
cat >"$tmp/trailer-reworded.txt" <<'EOF'
Railway configuration
Project fearless-abundance
Environment production

Plan: 2 to add, 7 to change, 0 to destroy
  + Create service mysql
  ~ Update openemr volumeAttachments.openemr-sites.mountPath and 3 more
    └ volumeAttachments.openemr-volume-ceSx.volume ("volume.openemr-volume-ceSx" → null)
  ~ Update mysql-data config.region
    └ config.region ("sfo" → "us-west2")
  ~ Update postgres-data config.region
    └ config.region ("sfo" → "us-west2")

! 4 destructive changes will remove Railway resources or variables.

Next
  • Run railway config apply to apply these changes.
EOF

# The same rewording with the word "destructive" gone altogether.
cat >"$tmp/trailer-no-keyword.txt" <<'EOF'
Plan: 2 to add, 7 to change, 0 to destroy
  ~ Update mysql-data config.region

! 4 changes will remove Railway resources or variables.
EOF

# A clean plan whose captured stderr carries a wrapped warning beginning `- word`. GitLab captures
# the plan with `2>&1`, so any stderr line lands in this same file. The `-` fragment rule used to
# run before the CLI's own "already up to date" statement and called this a plan that deletes
# production.
cat >"$tmp/clean-with-prose.txt" <<'EOF'
Railway configuration
Project fearless-abundance
Environment production

Warning warning: resources.volume.openemr-volume-ceSx: Volume exists on Railway but is
- not declared in the config.

✓ Your Railway configuration is already up to date.
EOF

# The same prose line in a plan with pending but safe changes: the header speaks, so the fragment
# rule must stay silent here too.
cat >"$tmp/changes-with-prose.txt" <<'EOF'
Warning warning: resources.volume.openemr-volume-ceSx: Volume exists on Railway but is
- not declared in the config.

Plan: 1 to add, 0 to change, 0 to destroy
  + Create volume scratch
EOF

# The other shape of "no drift": an all-zero header. The `already up to date` sentence was lifted
# from the CLI's format strings and has never been observed, so if the CLI prints this instead, a
# healthy environment must still go green rather than red-forever.
cat >"$tmp/zero-header.txt" <<'EOF'
Railway configuration
Project fearless-abundance
Environment production

Plan: 0 to add, 0 to change, 0 to destroy
EOF

# The header claims nothing is destroyed while the change list carries the CLI's own deletion
# marker. Two signals, in contradiction; neither is safe to prefer.
cat >"$tmp/header-contradicts.txt" <<'EOF'
Plan: 1 to add, 1 to change, 0 to destroy
  + Create volume scratch
  - Delete database mysql
EOF

# THE SAME CONTRADICTION UNDER AN ALL-ZERO HEADER, which is the OTHER claim that nothing is being
# destroyed and reaches the `clean` return directly. Reordering the two blocks in the header branch
# - they are adjacent, inside one `if`, and either order reads natural - passed the whole self-test
# while turning this document into a pass. The fixture above cannot see that: its header is
# `1 to add, 1 to change, 0 to destroy`, so it never touches the all-zero path.
cat >"$tmp/zero-header-contradicts.txt" <<'EOF'
Plan: 0 to add, 0 to change, 0 to destroy
  - Delete database mysql
EOF

# The STRONGER of the two clean claims, contradicted the same way. `already up to date` is checked
# before the header is even looked for, so a refusal that lives inside the header branch never runs
# on this document at all, and it was the only passing verdict this gate has.
cat >"$tmp/clean-claim-contradicts.txt" <<'EOF'
Railway configuration
Project fearless-abundance

Plan-ish output
  - Delete database mysql
  - Delete volume mysql-data

✓ Your Railway configuration is already up to date.
EOF

# A count category this script does not know, beside three it does. The three positional reads all
# succeed, all three are zero, and `2 to replace` is never read by anything - three integers
# outranking the body of the plan. `N to replace` / `N to recreate` are the two words a planner most
# plausibly grows next, and this is the mainline text input of the one job nobody watches. NO body
# line: the `% Replace...` line this fixture used to carry also matches the zero-header body_changes
# check a few lines further down, which backstopped this one silently once the unknown_counts check
# was deleted. Leaving the body empty means only the header word "replace" itself can trip
# anything.
cat >"$tmp/header-extra-count.txt" <<'EOF'
Railway configuration

Plan: 0 to add, 0 to change, 0 to destroy, 2 to replace
EOF

# The same family without a new word: an all-zero header printed above the change lines it is
# supposed to be counting. Whatever is true here, "nothing is happening" is not it.
cat >"$tmp/zero-header-with-changes.txt" <<'EOF'
Plan: 0 to add, 0 to change, 0 to destroy
  ~ Update mysql-data config.region
  + Create volume scratch
EOF

# The real production capture with ONE stray clean sentence appended - a second `railway` invocation
# in the same job, a cached line, a concatenated log. Hoisting the `already up to date` check to the
# top of classify_text ("check the cheap clean case first") is a tidy-looking refactor that passed
# the whole self-test and read THIS as a plan with nothing to do.
cat >"$tmp/clean-sentence-in-destructive-plan.txt" <<'EOF'
Plan: 2 to add, 7 to change, 3 to destroy
  - Delete database mysql
  ~ Update mysql-data config.region

! 7 destructive change(s) will remove Railway resources or variables.
✓ Your Railway configuration is already up to date.
EOF

# The green counterpart to the two refusals above, and the reason they are scoped rather than a bare
# document-wide grep: an all-zero header whose file also carries a wrapped stderr line beginning
# `- word`. Both hosts capture the plan with stderr merged in, so this shape is ordinary. Prose at
# the left margin is not a change line, and a healthy environment must stay green.
cat >"$tmp/zero-header-with-prose.txt" <<'EOF'
Railway configuration
Warning warning: resources.volume.openemr-volume-ceSx: Volume exists on Railway but is
- not declared in the config.

Plan: 0 to add, 0 to change, 0 to destroy
EOF

# A header shape whose counts are present by name but unreadable by position.
cat >"$tmp/header-unparseable.txt" <<'EOF'
Plan: to add: 1, to change: 2, to destroy: 3
  ~ Update mysql-data config.region
EOF

# THE SAME THREE CAPTURES WITH A LONG TAIL APPENDED, and nothing else changed: merged stderr, a
# verbose plan, a concatenated job log. A tail longer than the pipe buffer makes the race
# certain rather than rare - `printf "$text" | grep -q` under pipefail read a match as "no
# match" whenever grep exited before printf finished writing. Each of these three lost its
# verdict that way: the reworded trailer read as safe drift (exit 0), the fragment as undecidable
# rather than destructive, the clean log as undecidable. The tail line is deliberately inert to every
# rule in the guard: no keyword, no `Plan:`, no change marker.
long_tail() { yes '  Warning: padding line standing in for a long captured tail' | head -n 4000; }
{ cat "$tmp/trailer-reworded.txt"; long_tail; } >"$tmp/trailer-reworded-long-tail.txt"
{ cat "$tmp/fragment.txt"; long_tail; } >"$tmp/fragment-long-tail.txt"
{ cat "$tmp/clean.txt"; long_tail; } >"$tmp/clean-long-tail.txt"
# The fourth site, `claims_none`. This one is NOT red against the whole earlier guard - that lost
# both `already up to date` reads together and fell through to 11. It is red against a guard where
# ONLY the claims_none read races and the clean read below it does not: the contradiction refusal is
# skipped and the deletions pass as CLEAN. Verify it by reverting that one line, not against base.
{ cat "$tmp/clean-claim-contradicts.txt"; long_tail; } >"$tmp/clean-claim-contradicts-long-tail.txt"

# A grep that never answers. When bash cannot create the temp file a long here-string is spooled to,
# the redirection fails with status 1 and grep never starts - the same status as "no match". This
# shim stands in for that: every yes/no search the guard makes prints nothing and exits 1.
mkdir -p "$tmp/mute-grep"
real_grep=$(command -v grep)
cat >"$tmp/mute-grep/grep" <<EOF
#!/bin/sh
case "\$1" in -q* | -c*) exit 1 ;; esac
exec "$real_grep" "\$@"
EOF
chmod +x "$tmp/mute-grep/grep"

json() { printf '%s\n' "$1" >"$tmp/$2"; }

json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": true,
  "changeSet": {
    "version": 1,
    "changes": [
      { "address": "service.mysql", "kind": "resource.create", "severity": "safe", "summary": "Create service mysql" },
      { "address": "volume.mysql-data", "kind": "resource.update", "severity": "destructive", "summary": "Update mysql-data config.region" },
      { "address": "database.mysql", "kind": "resource.delete", "severity": "destructive", "summary": "Delete database mysql" }
    ]
  }
}' destructive.json

json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": false,
  "changeSet": { "version": 1, "changes": [] }
}' clean.json

json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": false,
  "changeSet": {
    "version": 1,
    "changes": [
      { "address": "volume.scratch", "kind": "resource.create", "severity": "safe", "summary": "Create volume scratch" }
    ]
  }
}' safe-changes.json

# the row that redeployed staging's openemr on every apply - the real change from a CLI 5.59.0
# staging plan on 2026-09-29, trimmed. Railway calls it safe; it builds nothing (openemr is an image)
# and only redeploys, so the guard refuses it. The path-only copy drops `field`, so the path half of
# the predicate is tested alone; the image bump is the row a real apply carries and must stay a pass.
json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": false,
  "changeSet": {
    "version": 1,
    "changes": [
      { "address": "service.openemr", "kind": "resource.update", "severity": "safe", "field": "build",
        "path": "resources.service.openemr.build", "deployEffect": "deploy",
        "before": { "buildEnvironment": "V3", "builder": "DOCKERFILE", "dockerfilePath": "docker/railway/Dockerfile" },
        "after": null, "summary": "Update openemr build.builder, build.dockerfilePath" },
      { "address": "service.agent-forge-api", "kind": "resource.update", "severity": "safe", "field": "source",
        "path": "resources.service.agent-forge-api.source", "summary": "Update agent-forge-api source.image" }
    ]
  }
}' build-row.json

json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": false,
  "changeSet": {
    "version": 1,
    "changes": [
      { "address": "service.openemr", "kind": "resource.update", "severity": "safe",
        "path": "resources.service.openemr.build", "summary": "Update openemr build.builder" }
    ]
  }
}' build-row-path-only.json

json '{
  "kind": "railway.config.plan",
  "version": 1,
  "destructive": false,
  "changeSet": {
    "version": 1,
    "changes": [
      { "address": "service.agent-forge-api", "kind": "resource.update", "severity": "safe", "field": "source",
        "path": "resources.service.agent-forge-api.source", "summary": "Update agent-forge-api source.image" }
    ]
  }
}' image-bump.json

# Railway renames the severity vocabulary. Every `select(.severity == "destructive")` now matches
# nothing, and without the vocabulary check the plan reads as entirely safe.
json '{
  "kind": "railway.config.plan",
  "destructive": true,
  "changeSet": { "changes": [ { "kind": "resource.delete", "severity": "dangerous", "summary": "Delete database mysql" } ] }
}' renamed-severity.json

# The top-level flag disappears.
json '{
  "kind": "railway.config.plan",
  "changeSet": { "changes": [ { "kind": "resource.create", "severity": "safe", "summary": "Create service mysql" } ] }
}' no-flag.json

# The change list moves somewhere else — the key is absent/renamed, not merely mistyped.
json '{
  "kind": "railway.config.plan",
  "destructive": false,
  "changeSet": { "operations": [] }
}' no-changes-key.json

# THE SAME CHECK, A DIFFERENT MALFORMATION: the key is present but not an array. A review mutant
# that consistently reads `.changeSet.changes` as `(.changeSet.changes // [])` everywhere in
# classify_json treats the absent key above as an empty list rather than undecidable — verified: it
# passes no-changes-key.json straight through to a clean verdict. `{}` is not
# rescued by that same `// []` fallback — `{} // []` is still `{}`, since `//` only substitutes on
# `null`/`false` — so this fixture isolates the array-type check on its own regardless of how the
# absent-key case is handled. Both fixtures are kept; each is malformed in exactly one way.
json '{
  "kind": "railway.config.plan",
  "destructive": false,
  "changeSet": { "changes": {} }
}' changes-not-array.json

# The two independent signals disagree.
json '{
  "kind": "railway.config.plan",
  "destructive": false,
  "changeSet": { "changes": [ { "kind": "resource.delete", "severity": "destructive", "summary": "Delete database mysql" } ] }
}' contradictory.json

# Valid JSON, but not a plan — e.g. an API error document downloaded into the artifact path.
json '{ "message": "404 Not Found" }' not-a-plan.json

# THE KIND CHECK, ISOLATED. not-a-plan.json above lacks .destructive too, so the very next check
# backstops it as well — deleting the kind check there still ends up undecidable, for a different
# reason. This one is shaped like a real plan everywhere except its kind, so only that check can call
# it undecidable. A separate change
json '{
  "kind": "not-a-railway-plan",
  "destructive": false,
  "changeSet": { "changes": [] }
}' wrong-kind.json

printf '{ "kind": "railway.config.plan", "destructive": fals\n' >"$tmp/truncated.json"

# --- red: the reason the gate exists -------------------------------------------------------------

expect 10 "the four-line fragment is DESTRUCTIVE" "$tmp/fragment.txt"
expect 10 "a real destructive plan log is DESTRUCTIVE" "$tmp/real-destructive.txt"
expect 10 "a real destructive pinned plan is DESTRUCTIVE" "$tmp/destructive.json"
expect 10 "one destructive input among clean ones still fails" "$tmp/clean.json" "$tmp/fragment.txt"
expect 10 "--require-clean does not soften a destructive verdict" --require-clean "$tmp/destructive.json"
# A stray clean sentence does not repeal the trailer that precedes it. The CLI's own destruction
# count is evidence; "already up to date" is only ever the absence of it.
expect 10 "a stray 'already up to date' line cannot clear a destructive trailer" --require-clean "$tmp/clean-sentence-in-destructive-plan.txt"
expect 10 "a destructive trailer is DESTRUCTIVE even with a zero-destroy header and no deletion lines" "$tmp/trailer-only.txt"
expect 10 "the fragment is DESTRUCTIVE however long the log after it" "$tmp/fragment-long-tail.txt"

# --- red: every way the gate could fail open -----------------------------------------------------

expect 11 "an empty plan is UNDECIDABLE, not clean" "$tmp/empty.txt"
expect 11 "a crash log is UNDECIDABLE, not clean" "$tmp/crash.txt"
expect 11 "truncated JSON is UNDECIDABLE" "$tmp/truncated.json"
expect 11 "valid JSON that is not a plan is UNDECIDABLE" "$tmp/not-a-plan.json"
expect 11 "a wrong .kind is UNDECIDABLE even when everything else is plan-shaped" "$tmp/wrong-kind.json"
expect 11 "an unknown severity vocabulary is UNDECIDABLE" "$tmp/renamed-severity.json"
expect 11 "a missing .destructive flag is UNDECIDABLE" "$tmp/no-flag.json"
expect 11 "a missing .changeSet.changes is UNDECIDABLE" "$tmp/no-changes-key.json"
expect 11 ".changeSet.changes present but not an array is UNDECIDABLE" "$tmp/changes-not-array.json"
expect 11 "a plan whose two signals disagree is UNDECIDABLE" "$tmp/contradictory.json"
expect 11 "a changed plan header is UNDECIDABLE, not zero-to-destroy" "$tmp/header-changed.txt"
expect 11 "an unparseable header count is UNDECIDABLE" "$tmp/header-unparseable.txt"
# Finding 1 of the !434 review: the trailer had no schema check behind it, and for a destructive
# UPDATE it is the only signal there is.
expect 11 "a reworded destructive trailer is UNDECIDABLE, never safe drift" "$tmp/trailer-reworded.txt"
expect 11 "--require-clean does not soften a reworded trailer either" --require-clean "$tmp/trailer-reworded.txt"
expect 11 "a removal claim with no 'destructive' keyword is UNDECIDABLE" "$tmp/trailer-no-keyword.txt"
expect 11 "a header contradicting its own deletion lines is UNDECIDABLE" "$tmp/header-contradicts.txt"
# Round 2 of the !434 review: the refusal above went into ONE of the two branches that claim nothing
# is being destroyed, and the count read validated three numbers and nothing else. Each of the four
# below was a pass at bd4a323, and each kills a mutant that passed all 34 assertions before them.
expect 11 "an all-zero header contradicting its own deletion lines is UNDECIDABLE" "$tmp/zero-header-contradicts.txt"
expect 11 "'already up to date' beside deletion lines is UNDECIDABLE" --require-clean "$tmp/clean-claim-contradicts.txt"
expect 11 "a header count category this script does not know is UNDECIDABLE" --require-clean "$tmp/header-extra-count.txt"
expect 11 "an all-zero header above change lines is UNDECIDABLE" --require-clean "$tmp/zero-header-with-changes.txt"
# a verdict must not depend on how much text follows the line that decided it.
expect 11 "a reworded destructive trailer is UNDECIDABLE however long the log after it" "$tmp/trailer-reworded-long-tail.txt"
expect 11 "'already up to date' beside deletion lines is UNDECIDABLE however long the log after it" --require-clean "$tmp/clean-claim-contradicts-long-tail.txt"
# A search that never ran is not a "no": the reworded trailer read as safe drift this way too.
PATH="$tmp/mute-grep:$PATH" expect 11 "a text search that returns no answer is UNDECIDABLE, not 'no match'" "$tmp/trailer-reworded.txt"
expect 11 "one undecidable input among clean ones still fails" "$tmp/clean.json" "$tmp/crash.txt"
expect 11 "every input absent is UNDECIDABLE, never a pass" "$tmp/nope.json"

# a build-config row on an image service is a redeploy that builds nothing - refused, with or
# without --require-clean, even beside a legitimate image bump.
expect 11 "a build-config row is refused though Railway calls it safe" "$tmp/build-row.json"
expect 11 "a build-config row is refused when only its path names it" "$tmp/build-row-path-only.json"
expect 11 "a build-config row outranks ordinary drift under --require-clean" --require-clean "$tmp/build-row.json"

# --- red: drift itself, under --require-clean ----------------------------------------------------

expect 12 "safe pending changes fail --require-clean (text)" --require-clean "$tmp/safe-changes.txt"
expect 12 "safe pending changes fail --require-clean (json)" --require-clean "$tmp/safe-changes.json"

# --- green: it must still be able to pass --------------------------------------------------------

expect 0 "an 'already up to date' log is clean" "$tmp/clean.txt"
expect 0 "an 'already up to date' log is clean however long the log after it" "$tmp/clean-long-tail.txt"
expect 0 "an empty pinned change set is clean" "$tmp/clean.json"
expect 0 "a clean plan passes --require-clean" --require-clean "$tmp/clean.json" "$tmp/clean.txt"
expect 0 "safe pending changes pass without --require-clean" "$tmp/safe-changes.txt"
expect 0 "an image bump - a source path, not build - passes without --require-clean" "$tmp/image-bump.json"
# The drift job passes both artifacts; --out may not write one when there is nothing to pin, and a
# job that reddens because a file it did not need is missing is a false alarm nobody will trust.
expect 0 "an absent input alongside a decided one does not fail" "$tmp/nope.json" "$tmp/clean.txt"
# Finding 2: the `-` fragment rule is the script's own opinion and must not outrank the CLI's.
expect 0 "a clean plan carrying a '- word' prose line is still clean" --require-clean "$tmp/clean-with-prose.txt"
# Finding 3: the all-zero header is the other shape of "no drift", and the acceptance test for the
# destructive-plan work is this job going green - which an unreachable clean path would deny.
expect 0 "an all-zero plan header passes --require-clean" --require-clean "$tmp/zero-header.txt"
# ... and still does when captured stderr puts a wrapped `- word` prose line in the same file. The
# contradiction rules are scoped to indented change lines below the header for exactly this reason.
expect 0 "an all-zero header with a '- word' prose line is still clean" --require-clean "$tmp/zero-header-with-prose.txt"
# The option loop used to stop at the first non-option, silently dropping a trailing flag.
expect 12 "--require-clean is honoured AFTER the file arguments" "$tmp/safe-changes.txt" --require-clean
expect 12 "safe pending changes with a '- word' prose line are still just changes" --require-clean "$tmp/changes-with-prose.txt"

# --- the fixtures still reach past the pipe buffer ----------------------------------------
# Without this, a tail that silently came out short (a missing `yes`, a smaller head) turns the four
# long-tail cases back into rare coin flips that pass against the broken guard.
for f in trailer-reworded-long-tail fragment-long-tail clean-long-tail clean-claim-contradicts-long-tail; do
    ran=$((ran + 1))
    size=$(wc -c <"$tmp/$f.txt")
    if [ "$size" -lt 262144 ]; then
        printf '\033[31mSELF-TEST FAILED\033[0m  %s.txt is %s bytes; it must exceed 256 KiB to outrun the pipe buffer\n' "$f" "$size" >&2
        fails=$((fails + 1))
    fi
done

# --- usage ---------------------------------------------------------------------------------------

expect 1 "no arguments is a usage error"
expect 1 "an unknown option after the files is a usage error, not a missing file" "$tmp/clean.txt" --nope

if [ "$fails" -gt 0 ]; then
    printf '\n\033[31m%d of %d self-test case(s) failed.\033[0m The destructive-plan gate is not behaving as documented.\n' "$fails" "$ran" >&2
    exit 1
fi
if [ "$ran" -eq 0 ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  no case ran at all - this suite proved nothing.\n' >&2
    exit 1
fi
printf '\033[32mok\033[0m  %d/%d cases: the destructive-plan gate still reddens on a destructive or unreadable plan, and still passes a clean one.\n' "$ran" "$ran"
