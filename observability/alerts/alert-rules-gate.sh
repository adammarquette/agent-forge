#!/bin/sh
# alert-rules-gate.sh - the whole alert-rules gate, in one place, for both hosts.
#
# Run it inside prom/prometheus:v3.15.0, so the parser that checks the rules is the parser that will
# load them. It is one script so every caller runs the same checks.
#
# Six checks, in the order a failure is cheapest to read:
#   1. promtool check rules   - a rule Prometheus rejects stops the WHOLE file loading, so every
#                               other rule goes quiet too and nothing in this stack notices.
#   2. case count             - `promtool test rules` prints SUCCESS and exits 0 on a `tests: []`
#                               file, so "no failure line" is the shape of a pass AND of a suite
#                               that ran nothing. Counted key-order-independently: promtool accepts
#                               `name:` before `interval:`, and a case written that way must not be
#                               invisible to the count this job exists to make trustworthy.
#   3. promtool test rules    - the synthetic series.
#   4. EXPRESSION PIN         - every rule in the pinned group must have a promql_expr_test whose
#                               expression is the rule's own, character for character once
#                               whitespace is collapsed. THIS IS THE CHECK THAT CLOSES THE
#                               ONE-DIRECTION GAP: the test file asserts the firing half against a
#                               COPY of each expression (promtool compares a firing alert's
#                               annotations for exact equality, so alert_rule_test cannot carry
#                               these paragraph-long descriptions), and without this check a
#                               threshold raised to an unfireable value in the rules file alone
#                               left the suite green - the exact failure the suite exists to
#                               prevent. With it, the one-sided edit is red here, and the two-sided
#                               edit is red in check 3 because the synthetic series no longer
#                               satisfies the raised threshold.
#   5. FOR: PIN                - `for:` sits entirely outside check 4: `promql_expr_test` evaluates
#                               at a single instant and never exercises the debounce, and every
#                               `alert_rule_test` silent-half case in the tests file asserts
#                               `exp_alerts: []` at an eval_time still short of `for:` - true
#                               whether `for:` is 10m or 30d, so none of it moves when `for:` widens.
#                               Measured: widening `for:` alone silenced three alerts - two Week 2,
#                               one Week 1's `agentforge-slo` - and every check above stayed green
#                               . There is no second FILE to pin `for:` against the way check
#                               4 pins `expr:` against the tests file, so this pins each rule's
#                               `for:` against a table declared in THIS script - covering every rule
#                               in EITHER group that declares one, not just PINNED_GROUP, because the
#                               Week 1 rule silenced on a separate change lives in `agentforge-slo`.
#   6. MIRRORED CONSTANT      - AgentForgeEvalCategoryRegression's threshold is a second copy of
#                               evals/baseline.json's `max_regression`, by necessity: nothing in
#                               Prometheus can read that file. This asserts the two are equal, so
#                               tightening the CI policy without moving the alert is red instead of
#                               silently leaving the runtime control looser than the gate.
#
# Fails CLOSED. Every count is asserted non-zero before it is used, because a parser that stopped
# matching produces "0 of 0 pinned", which reads exactly like a pass.
#
# Proven red by alert-rules-gate-selftest.sh, which runs immediately before it in the same job.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
RULES="${ALERT_RULES_FILE:-$HERE/agentforge-alerts.yml}"
TESTS="${ALERT_TESTS_FILE:-$HERE/agentforge-alerts-tests.yml}"
BASELINE="${ALERT_BASELINE_FILE:-$HERE/../../evals/baseline.json}"
PINNED_GROUP="${ALERT_PINNED_GROUP:-agentforge-week2}"
MIRRORED_ALERT=AgentForgeEvalCategoryRegression

die() { printf 'alert-rules-gate: ERROR: %s\n' "$*" >&2; exit 1; }

command -v promtool >/dev/null 2>&1 || die "promtool is not on PATH; this gate must run inside prom/prometheus."
[ -f "$RULES" ]    || die "rules file not found: $RULES"
[ -f "$TESTS" ]    || die "tests file not found: $TESTS"
[ -f "$BASELINE" ] || die "eval baseline not found: $BASELINE"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# --- 1. the rules parse --------------------------------------------------------------------
promtool check rules "$RULES"

# --- 2. the suite has cases in it ----------------------------------------------------------
# Every case is a sequence entry directly under `tests:`, whatever its first key happens to be.
CASES="$(awk '
  /^tests:/ { in_tests = 1; next }
  in_tests && /^[^ #]/ { in_tests = 0 }
  in_tests && /^  - / { n++ }
  END { print n + 0 }
' "$TESTS")"
[ "$CASES" -gt 0 ] || die "$TESTS declares no test cases; promtool would print SUCCESS and exit 0."

# --- 3. the synthetic series -----------------------------------------------------------------
# promtool resolves `rule_files:` relative to the test file, so run from its directory.
( cd "$(dirname "$TESTS")" && promtool test rules "$(basename "$TESTS")" )

# --- 4. the expression pin -------------------------------------------------------------------
# Emits "<group>\t<alert>\t<expr>" per rule, with the expression flattened to one whitespace-
# normalised line, to rule-exprs.tsv, and "<group>\t<alert>\t<for>" for every rule that declares a
# `for:` to rule-fors.tsv (check 5 reads that one). Handles both an inline scalar and a `|`/`>`
# block for `expr:`. Indentation is this file's convention; if it ever changes, this yields no
# rules and the count assertions below go red.
awk -v EXPRFILE="$work/rule-exprs.tsv" -v FORFILE="$work/rule-fors.tsv" '
  function emit() {
    if (alert != "" && expr != "") {
      gsub(/[ \t]+/, " ", expr); sub(/^ +/, "", expr); sub(/ +$/, "", expr)
      print group "\t" alert "\t" expr > EXPRFILE
    }
    if (alert != "" && forval != "") {
      print group "\t" alert "\t" forval > FORFILE
    }
    alert = ""; expr = ""; forval = ""
  }
  {
    line = $0; sub(/\r$/, "", line)
    if (in_block) {
      if (line ~ /^[ \t]*$/) next
      match(line, /^ */)
      if (RLENGTH > key_indent) { expr = expr " " line; next }
      in_block = 0
    }
    if (match(line, /^  - name:[ ]*/))        { emit(); group = substr(line, RSTART + RLENGTH) }
    else if (match(line, /^      - alert:[ ]*/)) { emit(); alert = substr(line, RSTART + RLENGTH) }
    else if (match(line, /^        expr:[ ]*/)) {
      v = substr(line, RSTART + RLENGTH)
      if (v == "|" || v == ">" || v == "|-" || v == ">-") { in_block = 1; key_indent = 8; expr = "" }
      else { expr = v }
    }
    else if (match(line, /^        for:[ ]*/)) { forval = substr(line, RSTART + RLENGTH) }
  }
  END { emit() }
' "$RULES"
# awk only creates a file on its first write to it; both are read unconditionally below.
[ -f "$work/rule-exprs.tsv" ] || : > "$work/rule-exprs.tsv"
[ -f "$work/rule-fors.tsv" ]  || : > "$work/rule-fors.tsv"

# Emits one whitespace-normalised line per `promql_expr_test` expression.
awk '
  function emit() {
    if (expr != "") {
      gsub(/[ \t]+/, " ", expr); sub(/^ +/, "", expr); sub(/ +$/, "", expr)
      print expr
    }
    expr = ""
  }
  {
    line = $0; sub(/\r$/, "", line)
    if (in_block) {
      if (line ~ /^[ \t]*$/) next
      match(line, /^ */)
      if (RLENGTH > key_indent) { expr = expr " " line; next }
      in_block = 0; emit()
    }
    if (line ~ /^    promql_expr_test:[ ]*$/) { in_pet = 1 }
    else if (in_pet && match(line, /^      - expr:[ ]*/)) {
      v = substr(line, RSTART + RLENGTH)
      if (v == "|" || v == ">" || v == "|-" || v == ">-") { in_block = 1; key_indent = 8; expr = "" }
      else { expr = v; emit() }
    }
    else if (line ~ /^    [a-z_]+:/) { in_pet = 0 }
  }
  END { if (in_block) emit() }
' "$TESTS" > "$work/test-exprs.txt"

TEST_EXPRS="$(wc -l < "$work/test-exprs.txt" | tr -d ' ')"
[ "$TEST_EXPRS" -gt 0 ] || die "parsed no promql_expr_test expressions out of $TESTS - the parser, not the file, is what to check first."

PINNED=0; UNPINNED=0
while IFS='	' read -r g a e; do
  [ "$g" = "$PINNED_GROUP" ] || continue
  if grep -qxF "$e" "$work/test-exprs.txt"; then
    PINNED=$((PINNED + 1))
  else
    UNPINNED=$((UNPINNED + 1))
    printf 'alert-rules-gate: ERROR: %s has no promql_expr_test asserting its own expression.\n' "$a" >&2
    printf '  rule expr: %s\n' "$e" >&2
  fi
done < "$work/rule-exprs.tsv"

GROUP_RULES=$((PINNED + UNPINNED))
[ "$GROUP_RULES" -gt 0 ] || die "parsed no rules in group '$PINNED_GROUP' out of $RULES - the parser, not the file, is what to check first."

# The other direction, reported BEFORE either verdict: a test expression that matches no live rule
# is a copy of something that moved or was deleted, and it goes on passing while asserting nothing
# about this stack. A one-sided threshold edit trips both directions, and naming both is what tells
# the reader it is one edit rather than two problems.
STALE=0
cut -f3 "$work/rule-exprs.tsv" > "$work/all-rule-exprs.txt"
while IFS= read -r e; do
  grep -qxF "$e" "$work/all-rule-exprs.txt" || {
    STALE=$((STALE + 1))
    printf 'alert-rules-gate: ERROR: this promql_expr_test expression matches no rule in %s:\n  %s\n' "$(basename "$RULES")" "$e" >&2
  }
done < "$work/test-exprs.txt"

[ "$UNPINNED" -eq 0 ] || die "$UNPINNED of $GROUP_RULES '$PINNED_GROUP' rule expression(s) are not pinned. A threshold moved in one file and not the other."
[ "$STALE" -eq 0 ] || die "$STALE promql_expr_test expression(s) assert nothing about a live rule."

# --- 5. the for: pin ---------------------------------------------------------------------------
# There is no second FILE to compare `for:` against the way check 4 compares `expr:` against the
# tests file - promql_expr_test never sees `for:`, and every alert_rule_test silent-half case
# stays silent whether `for:` is 10m or 30d, so nothing in the tests file moves when it widens.
# The second copy has to live here instead: this table is what "pinned" means for `for:`, and
# every rule in EITHER group that declares one is checked against it, not just $PINNED_GROUP -
# a separate change was Week 1's agentforge-slo rule going silently unpinned. A rule with no `for:` (a batch
# gauge, re-published per run rather than sampled) is not required to have one.
FOR_PINS='
AgentForgeHighTurnLatencyP95 15m
AgentForgeHighTurnErrorRate 5m
AgentForgeHighToolFailureRate 5m
AgentForgeElevatedVerificationFailureRate 15m
AgentForgeRetrievalDegradation 10m
AgentForgeHighExtractionFailureRate 10m
AgentForgeHighEvidenceRetrievalLatencyP95 10m
'

pinned_for() { printf '%s\n' "$FOR_PINS" | awk -v a="$1" '$1 == a { print $2; found=1 } END { if (!found) exit 1 }'; }

FOR_PINNED=0; FOR_UNPINNED=0
while IFS='	' read -r g a f; do
  [ -n "$a" ] || continue
  if pin="$(pinned_for "$a")" && [ "$pin" = "$f" ]; then
    FOR_PINNED=$((FOR_PINNED + 1))
  else
    FOR_UNPINNED=$((FOR_UNPINNED + 1))
    if [ -z "${pin:-}" ]; then
      printf 'alert-rules-gate: ERROR: %s declares for: %s but has no entry in the gate'"'"'s FOR_PINS table.\n' "$a" "$f" >&2
    else
      printf 'alert-rules-gate: ERROR: %s for: is %s; the gate pins %s. A duration changed in the rules file alone.\n' "$a" "$f" "$pin" >&2
    fi
  fi
done < "$work/rule-fors.tsv"

FOR_RULES=$((FOR_PINNED + FOR_UNPINNED))
[ "$FOR_RULES" -gt 0 ] || die "parsed no for: durations out of $RULES - the parser, not the file, is what to check first."
[ "$FOR_UNPINNED" -eq 0 ] || die "$FOR_UNPINNED of $FOR_RULES rule for: duration(s) do not match the gate's FOR_PINS table."

# The other direction: a FOR_PINS entry that names no live rule's for: - either the whole `for:`
# line was deleted from a rule the table still pins (the rule survives, the debounce silently
# doesn't), or the entry is a typo/rename that never matched anything. Both leave the table
# claiming a pin that pins nothing, which the forward loop above cannot see - it only walks rules
# that HAVE a for:, so a for: that vanished from the rules file is invisible to it.
cut -f2 "$work/rule-fors.tsv" > "$work/live-for-alerts.txt"
STALE_FOR=0
printf '%s\n' "$FOR_PINS" | awk 'NF == 2 { print $1 }' > "$work/for-pin-names.txt"
while IFS= read -r name; do
  [ -n "$name" ] || continue
  grep -qxF "$name" "$work/live-for-alerts.txt" || {
    STALE_FOR=$((STALE_FOR + 1))
    printf 'alert-rules-gate: ERROR: FOR_PINS pins %s, but no live rule in %s declares a for: for it (deleted for:, renamed, or removed alert).\n' "$name" "$(basename "$RULES")" >&2
  }
done < "$work/for-pin-names.txt"
[ "$STALE_FOR" -eq 0 ] || die "$STALE_FOR FOR_PINS entr(y/ies) match no live rule's for: - the gate's table, not the rules file, is stale."

# --- 6. the mirrored constant ------------------------------------------------------------------
RULE_EXPR="$(awk -F'\t' -v a="$MIRRORED_ALERT" '$2 == a { print $3 }' "$work/rule-exprs.tsv")"
[ -n "$RULE_EXPR" ] || die "$MIRRORED_ALERT is not in $RULES; check 6 has lost its subject."
RULE_MAXREG="$(printf '%s' "$RULE_EXPR" | sed -n 's/.*>[ ]*\([0-9][0-9.]*\)[ ]*$/\1/p')"
[ -n "$RULE_MAXREG" ] || die "could not read a threshold off the end of $MIRRORED_ALERT's expression: $RULE_EXPR"
BASE_MAXREG="$(sed -n 's/.*"max_regression"[ \t]*:[ \t]*\([0-9][0-9.]*\).*/\1/p' "$BASELINE" | head -1)"
[ -n "$BASE_MAXREG" ] || die "could not read max_regression out of $BASELINE."
awk -v r="$RULE_MAXREG" -v b="$BASE_MAXREG" 'BEGIN { exit !(r + 0 == b + 0) }' || die \
  "$MIRRORED_ALERT fires above $RULE_MAXREG but evals/baseline.json's max_regression is $BASE_MAXREG. These are two copies of one policy and must agree, or the CI gate and the runtime alert enforce different things."

printf 'alert-rules-gate: %s parses; %s unit-test case(s) passed; %s of %s "%s" rule expression(s) pinned byte-identical; %s of %s rule for: duration(s) pinned; %s test expression(s) all match a live rule; eval threshold %s == baseline max_regression %s\n' \
  "$(basename "$RULES")" "$CASES" "$PINNED" "$GROUP_RULES" "$PINNED_GROUP" "$FOR_PINNED" "$FOR_RULES" "$TEST_EXPRS" "$RULE_MAXREG" "$BASE_MAXREG"
