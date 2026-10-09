#!/bin/sh
# alert-rules-gate-selftest.sh - prove alert-rules-gate.sh can still go RED, and still go green.
#
# A guard that only checks one direction is the defect this whole change exists to fix, so the fix
# itself has to be mutation-proven rather than asserted. Every case below mutates a THROWAWAY COPY
# of the real files - the originals are never written to - and asserts both an exit code and, on the
# red cases, the line naming the reason. An exit code alone is not enough here: `set -eu` means a
# typo in the gate also exits non-zero, which would read as a pass on every red case.
#
# The directions that matter, and which check owns each:
#   - a threshold raised to an UNFIREABLE value in the rules file only .... check 4 (expression pin)
#   - the same raise applied to the rules file AND the test copy ........... check 3 (promtool: the
#     synthetic series no longer satisfies the threshold, so the expected sample vanishes)
#   - a threshold LOOSENED ................................................. check 3 (near-miss cases)
#   - the volume floor deleted ............................................. check 3
#   - a `for:` duration changed in the rules file alone, either way ........ check 5 (for: pin,
#     invisible to checks 3 and 4, which is why it needed its own table)
#   - evals/baseline.json tightened without moving the alert ............... check 6
#   - a rules file Prometheus would reject ................................. check 1
#   - a test suite with no cases in it ..................................... check 2
#   - the parser silently matching nothing ................................. checks 2/4/5 fail closed
#
# Runs in the same prom/prometheus:v3.15.0 container as the gate, so promtool is present; POSIX sh,
# not bash, because that image is BusyBox.
set -u

# Fails CLOSED the same way the gate it tests does: a suite that silently stops running cases
# still prints a PASS/TOTAL line that reads exactly like a pass (platform.md, "Read a gate's
# POSITIVE statement"). Update this when you add or remove an `expect` call - the count check
# below is what turns a case that quietly never ran into a red suite instead of a smaller "N of N".
# Two changes each added cases independently; reconciled to the true
# total of both on rebase.
EXPECTED_ASSERTIONS=32

HERE="$(cd "$(dirname "$0")" && pwd)"
GATE="$HERE/alert-rules-gate.sh"
PASS=0
FAIL=0

# The gate must be a PROGRAM before anything it prints means anything.
if ! sh -n "$GATE" 2>/dev/null; then
  echo "SELF-TEST FAILED - $GATE is not parseable by sh; nothing below would have tested it." >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# fixture <name> - a pristine copy of the three real files, returned by path.
fixture() {
  d="$work/$1"
  rm -rf "$d"
  mkdir -p "$d/alerts" "$d/evals"
  cp "$HERE/agentforge-alerts.yml" "$HERE/agentforge-alerts-tests.yml" "$d/alerts/"
  cp "$HERE/../../evals/baseline.json" "$d/evals/"
  printf '%s' "$d"
}

# run <dir> - the gate against a fixture, output captured, exit code echoed last.
run() {
  ALERT_RULES_FILE="$1/alerts/agentforge-alerts.yml" \
  ALERT_TESTS_FILE="$1/alerts/agentforge-alerts-tests.yml" \
  ALERT_BASELINE_FILE="$1/evals/baseline.json" \
  sh "$GATE" > "$work/out" 2>&1
  echo $?
}

# expect <wanted-exit> <substring-or-empty> <description>
expect() {
  want="$1"; needle="$2"; desc="$3"
  if [ "$got" != "$want" ]; then
    FAIL=$((FAIL + 1))
    printf 'FAIL  %s\n      expected exit %s, got %s. Gate output:\n' "$desc" "$want" "$got" >&2
    sed 's/^/      | /' "$work/out" >&2
    return
  fi
  if [ -n "$needle" ] && ! grep -qF "$needle" "$work/out"; then
    FAIL=$((FAIL + 1))
    printf 'FAIL  %s\n      exit %s was right but the output never said why (looked for: %s). Gate output:\n' "$desc" "$want" "$needle" >&2
    sed 's/^/      | /' "$work/out" >&2
    return
  fi
  PASS=$((PASS + 1))
  printf 'ok    %s\n' "$desc"
}

# expect_count <wanted-count> <needle-line> <description> - like expect(), but for a claim
# `expect` cannot make: that something DID NOT ALSO run. "RAN" is not "ran, and nothing after it
# also fired" - check 3 re-parses the same rule file check 1 does, so a die that stopped being
# fatal (`promtool check rules "$RULES" || true`) still lets check 1 run and still lets the
# generic 'FAILED' or the 'Checking ' header print, and the case would stay green. Counting the
# line check 1's own header shares with nothing else catches that a second failure block printed
# after it.
expect_count() {
  want="$1"; needle="$2"; desc="$3"
  got_count="$(grep -Fc "$needle" "$work/out")"
  if [ "$got_count" != "$want" ]; then
    FAIL=$((FAIL + 1))
    printf 'FAIL  %s\n      expected %s occurrence(s) of %s, got %s. Gate output:\n' "$desc" "$want" "$needle" "$got_count" >&2
    sed 's/^/      | /' "$work/out" >&2
    return
  fi
  PASS=$((PASS + 1))
  printf 'ok    %s\n' "$desc"
}

# Rewrites a threshold inside the agentforge-week2 group only, so the identically-worded
# thresholds in the agentforge-slo group above it are left alone.
week2_sed() { # <file> <sed-expr>
  sed -n '1,/^  - name: agentforge-week2$/p' "$1" > "$1.head"
  sed '1,/^  - name: agentforge-week2$/d' "$1" | sed "$2" > "$1.tail"
  cat "$1.head" "$1.tail" > "$1"
  rm -f "$1.head" "$1.tail"
}

# remove_rule <file> <alert-name> - deletes one whole "- alert: NAME" rule block from the rules
# file, start marker through the line before the next "      - alert:" (or EOF). Used to orphan a
# promql_expr_test left behind in the tests file: the rule it was pinning no longer exists, so the
# UNPINNED direction (check 4's forward pin) has nothing to flag, and only the STALE/orphan
# direction can catch it.
remove_rule() { # <file> <alert-name>
  awk -v name="$2" '
    $0 ~ ("^      - alert: " name "$") { skip = 1; next }
    skip && /^      - alert:/ { skip = 0 }
    !skip { print }
  ' "$1" > "$1.new" && mv "$1.new" "$1"
}

# --- 0. the positive control ------------------------------------------------------------------
d="$(fixture clean)"; got="$(run "$d")"
expect 0 'pinned byte-identical' "an untouched tree passes, and says how many rules it pinned"
expect 0 'rule for: duration(s) pinned' "and how many for: durations it pinned -, the summary no longer says only expression(s)"
expect 0 'eval threshold 0.05 == baseline max_regression 0.05' "and names the two copies it found in step"

# --- 1. the finding: all three thresholds raised out of reach, tests untouched -----------------
# This is the reviewer's own mutation, verbatim. Before the expression pin it was SUCCESS, exit 0
# - the suite green with all three Week 2 alerts permanently silent.
d="$(fixture unfireable)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/) > 0\.2$/) > 0.9/; s/by (le)) > 6$/by (le)) > 20/; s/) > 0\.05$/) > 0.5/'
got="$(run "$d")"
expect 1 'are not pinned' "three thresholds raised out of reach in the rules file alone is RED"
expect 1 'AgentForgeHighExtractionFailureRate' "and the extraction rule is named"
expect 1 'AgentForgeHighEvidenceRetrievalLatencyP95' "and the retrieval rule is named"
expect 1 'AgentForgeEvalCategoryRegression' "and the eval rule is named"

# One at a time, because three together could be passing on the strength of any one of them.
d="$(fixture unfireable-extraction)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/) > 0\.2$/) > 0.9/'
got="$(run "$d")"
expect 1 'AgentForgeHighExtractionFailureRate has no promql_expr_test' "the extraction threshold alone is RED"

d="$(fixture unfireable-retrieval)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/by (le)) > 6$/by (le)) > 20/'
got="$(run "$d")"
expect 1 'AgentForgeHighEvidenceRetrievalLatencyP95 has no promql_expr_test' "the retrieval threshold alone is RED"

# The verdict rule (review B1): the only rule that sees a safety-floor, orphan or population
# failure. Made unfireable in the rules file alone, the pin names it; in both files, the synthetic
# failing run no longer matches; widened in both to fire on a passing run, the near-miss catches it.
d="$(fixture unfireable-verdict)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/agentforge_eval_run_passed == 0$/agentforge_eval_run_passed == 2/'
got="$(run "$d")"
expect 1 'AgentForgeEvalGateFailed has no promql_expr_test' "the verdict rule made unfireable alone is RED"

d="$(fixture unfireable-verdict-both)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/agentforge_eval_run_passed == 0$/agentforge_eval_run_passed == 2/'
sed -i 's/- expr: agentforge_eval_run_passed == 0$/- expr: agentforge_eval_run_passed == 2/' "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'FAILED' "the verdict rule made unfireable in BOTH files is RED (the safety-only failing run)"

d="$(fixture widened-verdict-both)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/agentforge_eval_run_passed == 0$/agentforge_eval_run_passed <= 1/'
sed -i 's/- expr: agentforge_eval_run_passed == 0$/- expr: agentforge_eval_run_passed <= 1/' "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'FAILED' "the verdict rule widened to fire on a passing run is RED (the near-miss)"

# --- 2. the same raise applied to BOTH copies -------------------------------------------------
# The expression pin is satisfied - they agree - so this one has to be caught by the synthetic
# series instead: at > 0.9 the sample the test expects is filtered out and promtool fails.
d="$(fixture unfireable-both)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/) > 0\.2$/) > 0.9/'
sed -i 's/) > 0\.2$/) > 0.9/' "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'FAILED' "raising the threshold in the rules file AND the test copy is still RED"

# --- 3. the directions round 1 already held, asserted against check 3 rather than the pin ------
# Both copies are edited, so the expression pin is satisfied and the synthetic series is the only
# thing left that can catch it. That is the half round 1 got right, and it must not regress.
d="$(fixture loosened)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/) > 0\.2$/) > 0.05/'
sed -i 's/) > 0\.2$/) > 0.05/' "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'FAILED' "loosening the extraction threshold in both files is RED (the near-miss case)"

d="$(fixture no-floor)"
week2_sed "$d/alerts/agentforge-alerts.yml" '/^          and$/d; />= 5$/d'
sed -i '/^          and$/d; />= 5$/d' "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'FAILED' "deleting the >=5 volume floor from both files is RED (the too-few-to-mean-anything case)"

# --- 3b. the for: pin -------------------------------------------------------------------
# Measured, not reasoned: before this check, widening `for:` in the rules file alone left every
# check above green - `promql_expr_test` never reads `for:`, and the tests file's silent-half
# cases assert `exp_alerts: []` at an eval_time already short of the OLD `for:`, which stays true
# whatever the new one is. Rewrites only the named alert's own `for:` line, leaving its neighbours
# (including the identically-worded `for: 10m` on other rules) untouched.
alert_for_sed() { # <file> <alert-name> <new-for-value>
  awk -v alert="$2" -v newval="$3" '
    /^      - alert: / { in_alert = ($0 == "      - alert: " alert) }
    in_alert && /^        for: / { sub(/for: .*/, "for: " newval) }
    { print }
  ' "$1" > "$1.tmp" && mv "$1.tmp" "$1"
}

# This is the own finding, reproduced: the two Week 2 rules and the Week 1 rule it named are
# each widened ALONE, tests file untouched, and each must independently redden the gate.
d="$(fixture for-widened-extraction)"
alert_for_sed "$d/alerts/agentforge-alerts.yml" AgentForgeHighExtractionFailureRate 30d
got="$(run "$d")"
expect 1 'AgentForgeHighExtractionFailureRate for: is 30d' "widening for: on the Week 2 extraction rule alone is RED"

d="$(fixture for-widened-retrieval)"
alert_for_sed "$d/alerts/agentforge-alerts.yml" AgentForgeHighEvidenceRetrievalLatencyP95 30d
got="$(run "$d")"
expect 1 'AgentForgeHighEvidenceRetrievalLatencyP95 for: is 30d' "widening for: on the Week 2 retrieval-latency rule alone is RED"

d="$(fixture for-widened-week1-degradation)"
alert_for_sed "$d/alerts/agentforge-alerts.yml" AgentForgeRetrievalDegradation 30d
got="$(run "$d")"
expect 1 'AgentForgeRetrievalDegradation for: is 30d' "widening for: on the Week 1 agentforge-slo retrieval-degradation rule alone is RED - the AC5 rule"

# The other direction of the same edit: NARROWING is not exempt either, because the pin is exact
# equality against the table, not a floor. Proves the check is not accidentally one-directional
# the same way check 4's expression pin had to be proven both ways.
d="$(fixture for-narrowed-turn-error)"
alert_for_sed "$d/alerts/agentforge-alerts.yml" AgentForgeHighTurnErrorRate 1m
got="$(run "$d")"
expect 1 'AgentForgeHighTurnErrorRate for: is 1m' "narrowing for: on a Week 1 agentforge-slo rule alone is RED too, not just widening"

# The untouched control from section 0 already proves this whole direction: an unmodified tree
# reports "7 of 7 rule for: duration(s) pinned" and exits 0, so a real edit red above is the
# finding and not an artefact of a suite that only ever goes one way.

# The forward loop above only ever walks rules that STILL declare a `for:` - a `for:` line deleted
# outright never enters it, so a table entry pinning a debounce that quietly stopped existing would
# pass every check above vacuously. Only the reverse check (FOR_PINS row -> live rule) catches it.
strip_for_line() { # <file> <alert-name> - deletes the named alert's own for: line, nothing else
  awk -v alert="$2" '
    /^      - alert: / { in_alert = ($0 == "      - alert: " alert) }
    in_alert && /^        for: / { next }
    { print }
  ' "$1" > "$1.tmp" && mv "$1.tmp" "$1"
}

d="$(fixture for-line-deleted)"
strip_for_line "$d/alerts/agentforge-alerts.yml" AgentForgeHighToolFailureRate
got="$(run "$d")"
expect 1 'FOR_PINS pins AgentForgeHighToolFailureRate, but no live rule' "deleting a pinned rule's for: line outright is RED (N1: the debounce is gone, the alert is not)"

# The other N1 case, kept distinct from the line-deletion above: a FOR_PINS entry that names no
# live rule AT ALL, because the rule itself is gone - not merely its for: line. Deletes the whole
# "- alert: NAME" block so no row for it reaches rule-fors.tsv (the forward loop stays untouched,
# same as strip_for_line above - only the reverse check has anything to say about a name the table
# still carries).
remove_alert_block() { # <file> <alert-name> - deletes the whole rule, its own line through the
                        # line before the next "- alert:" (or EOF)
  awk -v name="$2" '
    $0 == "      - alert: " name { skip = 1; next }
    skip && /^      - alert:/ { skip = 0 }
    !skip { print }
  ' "$1" > "$1.tmp" && mv "$1.tmp" "$1"
}

d="$(fixture for-pin-orphaned)"
remove_alert_block "$d/alerts/agentforge-alerts.yml" AgentForgeElevatedVerificationFailureRate
got="$(run "$d")"
expect 1 'FOR_PINS pins AgentForgeElevatedVerificationFailureRate, but no live rule' "a FOR_PINS row matching no live rule (here: the whole rule was removed) is RED (N1)"

# --- 4. the mirrored constant -----------------------------------------------------------------
# The concrete failure a separate change named: the CI gate tightens, the runtime alert does not, and a
# disclosure-class regression arriving by a hand-edited pin is blocked at merge and silent at run.
d="$(fixture baseline-tightened)"
sed -i 's/"max_regression": 0\.05/"max_regression": 0.02/' "$d/evals/baseline.json"
got="$(run "$d")"
expect 1 'max_regression is 0.02' "tightening evals/baseline.json without moving the alert is RED"

d="$(fixture baseline-loosened)"
sed -i 's/"max_regression": 0\.05/"max_regression": 0.10/' "$d/evals/baseline.json"
got="$(run "$d")"
expect 1 'max_regression is 0.10' "loosening evals/baseline.json without moving the alert is RED"

# --- 5. the two halves that were already there ------------------------------------------------
d="$(fixture broken-rules)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/^        expr: histogram_quantile(0\.95,/        expr: histogram_quantile(0.95 NOT_PROMQL/'
got="$(run "$d")"
# 'FAILED' alone is not the ordering claim: `promtool test rules` (check 3) prints its own
# "  FAILED:" on the same broken file, INCLUDING when check 3 is the one that ends up parsing it
# (i.e. with check 1 deleted) - verified: check 1 removed from a scratch copy of the gate still
# exits 1 and still prints 'FAILED' on this fixture, because check 3 re-parses the rule file too.
# 'Checking ' is check 1's own header line (`promtool check rules` prints "Checking <file>" before
# its verdict) and `promtool test rules` never prints it on any fixture in this suite - confirmed
# by deleting check 1's invocation and re-running this exact case, which then loses the needle
# though the exit code stays 1.
expect 1 'Checking ' "a rules file Prometheus would reject is RED, and check 1 is what ran"
# 'Checking ' proves check 1 RAN; it does not prove nothing AFTER it also fired - if the die on
# check 1 stopped being fatal (`promtool check rules "$RULES" || true`), check 1 still prints its
# header and its own "  FAILED:" block, check 3 then re-parses the same broken file and prints a
# SECOND "  FAILED:" block, and the case above stayed green throughout (exit still 1, needle still
# present). Counting the line only a FAILED block's header carries closes that: one occurrence
# means check 1's die is still what stopped the script; two means check 3 got a turn it should
# never have had. Verified against the pinned image: normal run = 1, `|| true` mutation = 2.
expect_count 1 '  FAILED:' "and nothing after check 1 also fired (a non-fatal check 1 would print a second FAILED: block from check 3)"

d="$(fixture no-cases)"
awk '/^tests:/ { print "tests: []"; exit } { print }' "$d/alerts/agentforge-alerts-tests.yml" > "$d/alerts/t" && mv "$d/alerts/t" "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'declares no test cases' "a suite with no cases in it is RED, though promtool prints SUCCESS for it"

# --- 6. the gate fails CLOSED when its own parser stops matching -------------------------------
# A gate that silently parses nothing reports "0 of 0 pinned", which reads exactly like a pass.
d="$(fixture unparseable-group)"
ALERT_PINNED_GROUP=no-such-group \
ALERT_RULES_FILE="$d/alerts/agentforge-alerts.yml" \
ALERT_TESTS_FILE="$d/alerts/agentforge-alerts-tests.yml" \
ALERT_BASELINE_FILE="$d/evals/baseline.json" \
sh "$GATE" > "$work/out" 2>&1; got=$?
expect 1 'parsed no rules in group' "a pinned group that matches nothing is RED, not vacuously green"

d="$(fixture missing-baseline)"
rm -f "$d/evals/baseline.json"
got="$(run "$d")"
expect 1 'eval baseline not found' "a missing evals/baseline.json is RED, not a skipped check"

# Deleting the firing assertions altogether leaves a file promtool still calls SUCCESS - every
# remaining case asserts only silence - so the gate, not promtool, has to be what notices.
d="$(fixture no-firing-half)"
awk '
  /^    promql_expr_test:/ { skip = 1; next }
  skip && /^    [a-z_]+:/  { skip = 0 }
  skip && /^  - /          { skip = 0 }
  !skip                    { print }
' "$d/alerts/agentforge-alerts-tests.yml" > "$d/alerts/t" && mv "$d/alerts/t" "$d/alerts/agentforge-alerts-tests.yml"
got="$(run "$d")"
expect 1 'parsed no promql_expr_test expressions' "a suite with the firing half removed is RED, not vacuously pinned"

# --- 7. a test expression that no longer names a live rule ------------------------------------
# Both directions of one edit: the rule is unpinned AND the orphaned copy is named, so the reader
# sees one moved threshold rather than two unrelated complaints.
d="$(fixture stale-test-expr)"
week2_sed "$d/alerts/agentforge-alerts.yml" 's/by (le)) > 6$/by (le)) > 6.0/'
got="$(run "$d")"
expect 1 'matches no rule' "a promql_expr_test copy left behind by a rule edit is named as orphaned"
expect 1 'AgentForgeHighEvidenceRetrievalLatencyP95 has no promql_expr_test' "and the rule it was pinning is named as unpinned"

# --- 8. the orphan direction alone: a rule DELETED outright, its test copy left behind ---------
# The exact shape a separate change was filed about: the rule is gone (so nothing is unpinned - the forward
# direction is vacuously satisfied), and its promql_expr_test survives in the tests file,
# asserting nothing about anything live. Only check 4's STALE die (alert-rules-gate.sh:168) catches
# this; gutting that die leaves this fixture SUCCESS/exit 0 while the gate's own trailer line still
# claims every test expression matches a live rule - verified by removing the die from a scratch
# copy of the gate and re-running this exact case, which then goes from FAIL to PASS at exit 0.
d="$(fixture orphaned-rule)"
remove_rule "$d/alerts/agentforge-alerts.yml" AgentForgeHighEvidenceRetrievalLatencyP95
got="$(run "$d")"
expect 1 'matches no rule' "a rule deleted outright, with its promql_expr_test left behind, is RED (the orphan direction, not the forward pin)"

# ----------------------------------------------------------------------------------------------
TOTAL=$((PASS + FAIL))
echo ""
if [ "$TOTAL" -ne "$EXPECTED_ASSERTIONS" ]; then
  echo "SELF-TEST FAILED - ran $TOTAL assertions, expected $EXPECTED_ASSERTIONS. A case was" >&2
  echo "                   skipped or the parser counting them stopped matching - that reads" >&2
  echo "                   exactly like a smaller pass, which is why this is checked rather than" >&2
  echo "                   only printed. Update EXPECTED_ASSERTIONS if you added or removed a case." >&2
  exit 1
fi
if [ "$FAIL" -eq 0 ]; then
  echo "SELF-TEST PASSED - $PASS of $TOTAL assertions: alert-rules-gate.sh still reddens on a"
  echo "                   threshold nobody can reach, and on a policy constant that drifted."
  exit 0
fi
echo "SELF-TEST FAILED - $FAIL of $TOTAL assertions failed; the alert-rules gate cannot be trusted." >&2
exit 1
