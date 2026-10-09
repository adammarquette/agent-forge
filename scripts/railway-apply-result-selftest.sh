#!/usr/bin/env bash
# railway-apply-result-selftest.sh — drive scripts/railway-apply-result.sh over a table of result
# documents, so the verdict is pinned shape by shape and not only through the deploy steps that call it.
#
# WHY A TABLE OF ITS OWN. A test of the deploy steps proves they CALL the checker and fail on a failed
# result. It cannot pin the checker's exact acceptance set: a checker relaxed to accept
# `partially_applied`, or "anything but failed", would pass it while every fixture also carried a failed
# change. Each row here differs from a passing document in exactly one way.
#
# Run it:  bash scripts/railway-apply-result-selftest.sh
# Under the image CI uses:
#   docker run --rm -v "$PWD:/w" -w /w alpine:3.21 sh -c 'apk add -q bash jq && bash scripts/railway-apply-result-selftest.sh'
#
# DEPLOYMENT.md §9

set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/railway-apply-result.sh"
EXPECTED_ASSERTIONS=24

asserts=0
fails=0
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

chk() { # chk <expected> <got> <description>
    asserts=$((asserts + 1))
    if [ "$1" = "$2" ]; then
        printf '  ok    %s\n' "$3"
    else
        printf '\033[31m  FAIL\033[0m  expected=%s got=%s  %s\n' "$1" "$2" "$3" >&2
        fails=$((fails + 1))
    fi
}

command -v jq >/dev/null 2>&1 || { printf 'SELF-TEST FAILED  jq is required.\n' >&2; exit 1; }
bash -n "$SCRIPT" || { printf 'SELF-TEST FAILED  %s does not parse.\n' "$SCRIPT" >&2; exit 1; }

A='{ "kind": "resource.create", "path": "resources.volume.v", "summary": "Create volume v", "status": "applied" }'
F='{ "kind": "resource.create", "path": "resources.volume.v", "summary": "Create volume v", "status": "failed" }'
P='{ "kind": "resource.create", "path": "resources.service.s", "summary": "Create service s", "status": "pending" }'

# case <expected exit> <name> <document>: the document is written pretty-printed, as the CLI prints it.
case_doc() {
    local want="$1" name="$2" doc="$3" f="$tmp/$2.json" rc
    if printf '%s' "$doc" | jq . >"$f" 2>/dev/null; then :; else printf '%s\n' "$doc" >"$f"; fi
    bash "$SCRIPT" check "$f" >"$tmp/$name.out" 2>&1
    rc=$?
    chk "$want" "$rc" "check: $name -> exit $want"
}

printf 'passes: only these two shapes\n'
case_doc 0  applied                    "{\"id\":\"1\",\"status\":\"applied\",\"changes\":[$A]}"
case_doc 0  noop-empty                 '{"id":null,"status":"noop","changes":[]}'

printf 'not applied (12): Railway answered, and not with a complete apply\n'
case_doc 12 applied-with-failed-change "{\"status\":\"applied\",\"changes\":[$A,$F]}"
case_doc 12 partially-applied-all-applied "{\"status\":\"partially_applied\",\"changes\":[$A,$A]}"
case_doc 12 partially-applied-pending  "{\"status\":\"partially_applied\",\"changes\":[$A,$P]}"
case_doc 12 failed                     '{"status":"failed","changes":[]}'
case_doc 12 staged                     "{\"status\":\"staged\",\"stagedPatchId\":\"p\",\"changes\":[$A]}"
case_doc 12 applying                   "{\"status\":\"applying\",\"changes\":[$A]}"
case_doc 12 unknown-status             "{\"status\":\"completed\",\"changes\":[$A]}"
case_doc 12 noop-with-changes          "{\"status\":\"noop\",\"changes\":[$A]}"

printf 'undecidable (11): not a document of the shape this was written against\n'
case_doc 11 missing-status             "{\"changes\":[$A]}"
case_doc 11 null-status                "{\"status\":null,\"changes\":[$A]}"
case_doc 11 applied-without-changes    '{"status":"applied"}'
case_doc 11 changes-renamed            "{\"status\":\"applied\",\"items\":[$F]}"
case_doc 11 changes-not-array          '{"status":"applied","changes":{}}'
case_doc 11 prose                      'Applied pinned Railway configuration.'
: >"$tmp/empty.json"; bash "$SCRIPT" check "$tmp/empty.json" >/dev/null 2>&1
chk 11 "$?" "check: empty output -> exit 11"

printf 'what it prints\n'
# A wrapper's lines above the document (the snapshot guard's BACKUP lines) do not hide it.
{ printf 'BACKUP label=x volume=v\n'; printf '%s' "{\"status\":\"applied\",\"changes\":[$A]}" | jq .; } >"$tmp/prefixed.json"
bash "$SCRIPT" check "$tmp/prefixed.json" >/dev/null 2>&1
chk 0 "$?" "check: a document below a wrapper's own lines is still read"
# Unreadable output is described, not echoed: Railway's `outputs` stay out of the log (L4).
{ printf 'BACKUP label=x\n'; printf '{\n  "status": "applied",\n  "changes": [ { "outputs": "SHOULD-NOT-PRINT" } ]\n} trailing\n'; } >"$tmp/broken.json"
bash "$SCRIPT" check "$tmp/broken.json" >"$tmp/broken.out" 2>&1
chk 11 "$?" "check: a document spoiled by trailing text -> exit 11"
grep -q 'SHOULD-NOT-PRINT' "$tmp/broken.out" && leaked=yes || leaked=no
chk no "$leaked" "check: and nothing from inside the document is printed"
grep -q 'BACKUP label=x' "$tmp/broken.out" && shown=yes || shown=no
chk yes "$shown" "check: while the lines above it are"

printf 'run\n'
bash "$SCRIPT" run --out "$tmp/r1" -- echo no-json >/dev/null 2>&1
chk 1 "$?" "run: a command without --json is refused"
printf '#!/usr/bin/env bash\nexit 7\n' >"$tmp/seven"; chmod +x "$tmp/seven"
bash "$SCRIPT" run --out "$tmp/r2" -- "$tmp/seven" --json >/dev/null 2>&1
chk 7 "$?" "run: the wrapped command's own failure code comes back unchanged"
printf '#!/usr/bin/env bash\nprintf "{\\n  \\"status\\": \\"partially_applied\\",\\n  \\"changes\\": []\\n}\\n"\n' >"$tmp/partial"; chmod +x "$tmp/partial"
bash "$SCRIPT" run --out "$tmp/r3" -- "$tmp/partial" --json >/dev/null 2>&1
chk 12 "$?" "run: an exit-0 command whose result is partially_applied -> exit 12"

if [ "$fails" -gt 0 ]; then
    printf '\n\033[31mSELF-TEST FAILED\033[0m  %d of %d assertion(s) failed.\n' "$fails" "$asserts" >&2
    exit 1
fi
if [ "$asserts" -ne "$EXPECTED_ASSERTIONS" ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  ran %s assertion(s), expected %s.\n' "$asserts" "$EXPECTED_ASSERTIONS" >&2
    exit 1
fi
printf '\033[32mSELF-TEST PASSED\033[0m  %s of %s assertions - only applied (no failed change) and an empty noop pass; every other result is red\n' \
    "$asserts" "$EXPECTED_ASSERTIONS"
