#!/usr/bin/env bash
# post-deploy-verify-selftest.sh — prove the post-deploy check can still go RED, and still go green.
#
# A gate asserted in a comment is not a gate. Everything in this
# family fails PERMISSIVE when it breaks — an unreachable host read as "nothing to report", a probe
# whose output changed shape read as a pass — so the directions that matter most here are the three
# where the check learns nothing: a transport failure, a response with no status in it, and a check
# set that shrank. All three must be red. So must each individual assertion, one at a time, and the
# internal-hostname refusal that carries DEPLOYMENT.md §2's one-origin invariant.
#
# The HTTP client and the bootstrap command are injected (PDV_CURL / PDV_BOOTSTRAP_CMD) so the
# decision logic can be driven through every branch without a live stack. What is under test is that
# logic — which response makes it red — not curl. Two cases instead mutate a COPY of the gate
# (GATE_BIN), for the one thing no seam can inject: the size of the check set itself.

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/post-deploy-verify.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"

# A stand-in curl: answers from a spec file of "<path> <status> [location]" lines, in the shape the
# real one produces under `-D - -w PDV_STATUS:%{http_code}`.
# A request carrying the admin/admin login body is keyed "<path>#admin:admin", so a gate
# that stopped sending the credentials finds no spec line and goes red rather than reading an
# unauthenticated 401 as "refused". Every request is logged to PDV_LOG when it is set.
cat > "$tmp/bin/curl" <<'FAKE'
#!/usr/bin/env bash
url="${!#}"
out=""; prev=""; for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
path="$(printf '%s' "$url" | sed -e 's#^[A-Za-z][A-Za-z0-9+.-]*://[^/]*##')"
[ -n "$path" ] || path=/
case " $* " in *'"user":"admin","password":"admin"'*) path="$path#admin:admin" ;; esac
[ -z "${PDV_LOG:-}" ] || printf '%s\n' "$path" >> "$PDV_LOG"
while read -r p code loc body; do
  [ "$p" = "$path" ] || continue
  printf 'HTTP/1.1 %s\r\n' "$code"
  [ -n "${loc:-}" ] && [ "$loc" != "-" ] && printf 'Location: %s\r\n' "$loc"
  printf '\r\n'
  printf 'PDV_STATUS:%s\n' "$code"
  [ -z "$out" ] || [ "$out" = /dev/null ] || printf '%s' "${body:-}" > "$out"
  exit 0
done < "$PDV_SPEC"
printf 'HTTP/1.1 599\r\n\r\nPDV_STATUS:599\n'
FAKE

# A curl that cannot reach the host at all. The whole point of the script is that this is RED.
cat > "$tmp/bin/curl-dead" <<'FAKE'
#!/usr/bin/env bash
echo "curl: (7) Failed to connect" >&2
exit 7
FAKE

# A curl that SUCCEEDS and says nothing readable — the silent-pass shape.
cat > "$tmp/bin/curl-mute" <<'FAKE'
#!/usr/bin/env bash
printf 'HTTP/1.1 200\r\n\r\n'
exit 0
FAKE

# A curl that fails ONLY for a grafana path, and answers everything else from the spec exactly as
# the main stand-in does. $CURL is one seam for the whole script (PDV_CURL), so curl-dead above fails
# every check, http included, and the red http half then carries the run whatever
# run_grafana_check does: the shape. This stub keeps the http half healthy. The case that
# uses it also asserts the guard's message, because the exit code alone cannot tell it apart from the
# health-status check below it.
cat > "$tmp/bin/curl-dead-grafana" <<'FAKE'
#!/usr/bin/env bash
url="${!#}"
out=""; prev=""; for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
path="$(printf '%s' "$url" | sed -e 's#^[A-Za-z][A-Za-z0-9+.-]*://[^/]*##')"
[ -n "$path" ] || path=/
case "$path" in
  /grafana*) echo "curl: (7) Failed to connect" >&2; exit 7 ;;
esac
case " $* " in *'"user":"admin","password":"admin"'*) path="$path#admin:admin" ;; esac
[ -z "${PDV_LOG:-}" ] || printf '%s\n' "$path" >> "$PDV_LOG"
while read -r p code loc body; do
  [ "$p" = "$path" ] || continue
  printf 'HTTP/1.1 %s\r\n' "$code"
  [ -n "${loc:-}" ] && [ "$loc" != "-" ] && printf 'Location: %s\r\n' "$loc"
  printf '\r\n'
  printf 'PDV_STATUS:%s\n' "$code"
  [ -z "$out" ] || [ "$out" = /dev/null ] || printf '%s' "${body:-}" > "$out"
  exit 0
done < "$PDV_SPEC"
printf 'HTTP/1.1 599\r\n\r\nPDV_STATUS:599\n'
FAKE

bootstrap() { # <exit-code> <stdout line...>  -> writes a stand-in BootstrapOpenEmr
  local rc="$1"; shift
  {
    printf '#!/usr/bin/env bash
'
    printf 'cat <<%s
' "'BOOTSTRAP_OUT'"
    [ "$#" -eq 0 ] || printf '%s
' "$@"
    printf 'BOOTSTRAP_OUT
'
    printf 'exit %s
' "$rc"
  } > "$tmp/bin/bootstrap"
  chmod +x "$tmp/bin/bootstrap"
}

chmod +x "$tmp/bin/curl" "$tmp/bin/curl-dead" "$tmp/bin/curl-mute" "$tmp/bin/curl-dead-grafana"

spec() { printf '%s\n' "$@" > "$tmp/spec"; }

# /ready's healthy answer, in ReadinessResponse's own shape. A spec line is
# "<path> <status> <location or -> <body>", and every http spec below carries this one, so each
# older case still reddens for the one thing it names and not because /ready went unanswered.
READY_OK='/agentforge/ready 200 - {"status":"Healthy","checks":[{"name":"observability","status":"Healthy","description":"Prometheus reachable."}]}'

healthy() {
  spec "/ 302 /interface/login/login.php" \
       "/agentforge/health 200" \
       "/apis/default/fhir/metadata 200" \
       "$READY_OK"
}

fails=0
ran=0
expect() { # <expected-exit> <what> [extra args to the gate...]
  local want="$1" what="$2"; shift 2
  ran=$((ran + 1))
  set +e
  local out
  out="$(PDV_SPEC="$tmp/spec" PDV_CURL="${CURL_BIN:-$tmp/bin/curl}" \
    PDV_BOOTSTRAP_CMD="${BOOTSTRAP_BIN:-$tmp/bin/bootstrap}" \
    bash "${GATE_BIN:-$GATE}" "${FRONT_DOOR:-http://front-door.example:8080}" "$@" 2>&1)"
  local got=$?
  set -e
  if [ "$got" -ne "$want" ]; then
    printf '\033[31mSELF-TEST FAILED\033[0m  %s — expected exit %s, got %s\n' "$what" "$want" "$got" >&2
    fails=$((fails + 1))
    return 0
  fi
  # EXPECT_SAYING: where two branches share an exit code, only the message says which one fired.
  case "$out" in
    *"${EXPECT_SAYING:-}"*) : ;;
    *)
      printf '\033[31mSELF-TEST FAILED\033[0m  %s — exit %s was right but the output never said "%s"\n' \
        "$what" "$want" "$EXPECT_SAYING" >&2
      fails=$((fails + 1)) ;;
  esac
}

# ---- the HTTP half --------------------------------------------------------------------------
healthy
expect 0 "a healthy front door is green"

healthy
spec "/ 200" "/agentforge/health 200" "/apis/default/fhir/metadata 200" "$READY_OK"
expect 1 "GET / answering 200 instead of the login redirect reddens"

spec "/ 302 /somewhere/else.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" "$READY_OK"
expect 1 "a 302 to the wrong place reddens"

spec "/ 302 /interface/login/login.php" "/agentforge/health 500" "/apis/default/fhir/metadata 200" "$READY_OK"
expect 1 "the sidecar not answering behind the proxy reddens"

spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 404" "$READY_OK"
expect 1 "FHIR metadata 404 — rest_api/rest_fhir_api off — reddens"

# ---- /agentforge/ready ---------------------------------------------------
# THE SHAPE: /health 200, and /ready 503 because the sidecar cannot reach Prometheus. This
# passed every check on 2026-09-23. Drop the /ready entry from CHECKS (and EXPECTED_CHECKS with it)
# and this goes green; the message is asserted so the failing dependency is named, not just counted.
READY_503='/agentforge/ready 503 - {"status":"Unhealthy","checks":[{"name":"llm-provider","status":"Healthy","description":"ok"},{"name":"observability","status":"Unhealthy","description":"Prometheus unreachable."}]}'
spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" "$READY_503"
EXPECT_SAYING="observability=Unhealthy" expect 1 "the shape - /health 200, /ready 503 - reddens and names the dependency"

spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" "/agentforge/ready 503"
EXPECT_SAYING="the body named no check" expect 1 "/ready 503 with no body still reddens, and says it could not name the dependency"

# DEGRADED IS A PASS, deliberately (see CHECKS): the sidecar answers 200 for optional dependencies
# left unconfigured. It must be named, though, so this asserts the warning as well as the exit.
spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" \
     '/agentforge/ready 200 - {"status":"Degraded","checks":[{"name":"observability","status":"Degraded","description":"not configured"},{"name":"openemr","status":"Healthy","description":"ok"}]}'
EXPECT_SAYING="observability=Degraded" expect 0 "/ready Degraded (200) passes, and names the degraded check"

# A 200 that is not the readiness document proves nothing about dependencies: a proxy location
# serving the login page, or an empty 200. Remove check_ready_body and both go green.
spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" \
     '/agentforge/ready 200 - <html><body>OpenEMR login</body></html>'
EXPECT_SAYING="does not say Healthy or Degraded" expect 1 "/ready 200 serving HTML, not the readiness JSON, reddens"

spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" "/agentforge/ready 200"
EXPECT_SAYING="does not say Healthy or Degraded" expect 1 "/ready 200 with an empty body reddens"

# ...and a 200 whose document says Unhealthy is not a pass either, whatever the status code says.
spec "/ 302 /interface/login/login.php" "/agentforge/health 200" "/apis/default/fhir/metadata 200" \
     '/agentforge/ready 200 - {"status":"Unhealthy","checks":[{"name":"openemr","status":"Unhealthy","description":"down"}]}'
EXPECT_SAYING="does not say Healthy or Degraded" expect 1 "/ready 200 carrying status Unhealthy reddens"

healthy
CURL_BIN="$tmp/bin/curl-dead" expect 1 "a host it cannot reach at all reddens, rather than passing quietly"

healthy
CURL_BIN="$tmp/bin/curl-mute" expect 1 "a response with no status in it reddens, rather than passing quietly"

healthy
FRONT_DOOR="http://openemr" expect 2 "a container's own hostname is refused, not checked (DEPLOYMENT.md §2)"

healthy
FRONT_DOOR="http://agent-forge-api.railway.internal:8080" expect 2 "a .railway.internal host is refused too"

healthy
FRONT_DOOR="localhost:8080" expect 2 "a base URL with no scheme is refused"

# DNS is case-insensitive and ignores the trailing root label, so these reach the same container as
# `http://openemr/`. The C# guard in tools/BootstrapOpenEmr matches a lower-cased Uri.Host; these
# keep the shell one honest about the parity it claims.
healthy
FRONT_DOOR="http://OPENEMR" expect 2 "an UPPER-CASE internal hostname is refused too"
healthy
FRONT_DOOR="http://OpenEMR:8080" expect 2 "a mixed-case internal hostname is refused too"
healthy
FRONT_DOOR="http://agent-forge-api.RAILWAY.internal." expect 2 "case and a trailing dot do not evade the .railway.internal arm"

# ... and the other direction, because a refusal list that grew into a substring match would break
# every real deployment. These are the front doors that must keep working.
healthy
FRONT_DOOR="http://openemr.example.com" expect 0 "a real host merely CONTAINING a service name still passes"
healthy
FRONT_DOOR="https://reverse-proxy-production-395f.up.railway.app" expect 0 "the real Railway front door still passes"

# ---- the check set itself ---------------------------------------------------------------------
# The third fail-closed direction, and the one that reads as a PASS whenever the guard compares the
# array against its own length. Driven by mutating a COPY of the gate — deleting CHECKS entries, the
# same damage a bad merge does — because the count it is guarding is not injectable by design.
# Remove the EXPECTED_CHECKS comparison from post-deploy-verify.sh and both of these go green.
grep -v '^  "/agentforge/health' "$GATE" | grep -v '^  "/apis/default/fhir/metadata' > "$tmp/bin/gate-shrunk.sh"
grep -v '^  "/' "$GATE" > "$tmp/bin/gate-empty.sh"

healthy
GATE_BIN="$tmp/bin/gate-shrunk.sh" expect 1 "a CHECKS array that lost entries reddens — it does not pass with fewer"

healthy
GATE_BIN="$tmp/bin/gate-empty.sh" expect 1 "an EMPTY check set reddens — not 'PASS (0 checks)'"

# The /ready probe alone, dropped - the edit that would quietly restore the blind spot.
grep -v '^  "/agentforge/ready' "$GATE" > "$tmp/bin/gate-no-ready.sh"
healthy
GATE_BIN="$tmp/bin/gate-no-ready.sh" EXPECT_SAYING="ran 3 of 4 checks" \
  expect 1 "a CHECKS array that lost only its /ready probe reddens"

# ---- the globals half ----------------------------------------------------
healthy
# "Done: 0 changed" here on purpose: with "1 changed" the positive-invariant check further down
# reddens this fixture too once the drift-verb check is deleted, backstopping it.
bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" \
              "  SET     agentforge_launch_mode = tab   (was 'iframe')" "Done: 0 changed, 1 already correct."
expect 1 "an unexpected SET reddens: the deploy did not carry the setting" --globals

bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" \
              "  ENABLED AgentForge Agenda (abc)" "Done: 0 changed, 1 already correct."
expect 1 "a client the deploy left disabled reddens" --globals

bootstrap 0 ""
expect 1 "a bootstrap that reports nothing at all reddens, rather than passing on an empty list" --globals

# The `Done: 0 changed` line is here on purpose: without it the positive-invariant check further
# down reddens this fixture too, so deleting the rc check left the case red. Only the exit code is
# wrong now.
bootstrap 1 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" "No AgentForge SMART clients found." "Done: 0 changed, 1 already correct."
expect 1 "a bootstrap that exits non-zero reddens even when its lines all say ok" --globals

bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" "Done: 0 changed, 1 already correct."
expect 0 "--globals with a converged stack is green"  --globals

# The denylist above only knows the verbs SET and ENABLED. tools/BootstrapOpenEmr is free to rename
# them and exits 0 either way, so on the verb alone this drift is a silent PASS. `Done: 0 changed`
# is what catches it. Drop that test from post-deploy-verify.sh and these two go green.
bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" \
              "  UPDATED agentforge_launch_mode = tab   (was 'iframe')" "Done: 1 changed, 1 already correct."
expect 1 "drift under a verb the denylist never heard of still reddens" --globals

bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" \
              "  ok      agentforge_launch_mode = tab"
expect 1 "output with no 'Done: N changed' line at all reddens — the tool changed shape" --globals

# The halves are independent: a broken front door is red even when the globals are perfect.
bootstrap 0 "globals:" "  ok      site_addr_oath = http://front-door.example:8080" "Done: 0 changed, 1 already correct."
spec "/ 302 /interface/login/login.php" "/agentforge/health 503" "/apis/default/fhir/metadata 200" "$READY_OK"
expect 1 "a red http half is not rescued by a green globals half" --globals

# ---- the Grafana half --------------------------------------------------
# The failure this exists for is green everywhere else: Grafana up, and admin/admin accepted.
grafana_up() { # <login status>
  healthy
  printf '%s\n' "/grafana/api/health 200" "/grafana/login#admin:admin $1" >> "$tmp/spec"
}

grafana_up 401
expect 0 "--grafana passes when Grafana answers and REFUSES admin/admin" --grafana

grafana_up 200
expect 1 "--grafana reddens when Grafana ACCEPTS admin/admin (the unset-credential fail-open)" --grafana

# The refusing login is here on purpose. Without it, deleting the health-status check sends the login
# probe to the stand-in's unmatched-path 599, and the case stays red for the wrong reason. With it,
# only the health check can redden this case.
healthy
printf '%s\n' "/grafana/api/health 404" "/grafana/login#admin:admin 401" >> "$tmp/spec"
expect 1 "--grafana reddens where /grafana is not routed (404): absent is not refused" --grafana

healthy
printf "%s
" "/grafana/api/health 503" "/grafana/login#admin:admin 401" >> "$tmp/spec"
expect 1 "--grafana reddens on an unhealthy Grafana even when its login refuses admin/admin" --grafana

grafana_up 404
expect 1 "--grafana reddens when the login answers 404 - only an exact 401 proves refusal" --grafana

grafana_up 502
expect 1 "--grafana reddens when the login answers 502" --grafana

grafana_up 200
expect 0 "without --grafana the Grafana half does not run, so an environment without it is not reddened"

grafana_up 401
CURL_BIN="$tmp/bin/curl-dead" expect 1 "a host it cannot reach at all reddens for --grafana too (the whole transport is down, http included)" --grafana

# THE SAME CLAIM, ISOLATED, AND ASSERTED BY MESSAGE. The http half stays healthy here, so it cannot
# redden the run. The exit code alone still proves nothing about the transport guard: delete it and
# the empty status trips the health-status check below it, same exit 1. Only the message differs.
grafana_up 401
EXPECT_SAYING="GET /grafana/api/health could not be probed" CURL_BIN="$tmp/bin/curl-dead-grafana" \
  expect 1 "--grafana's own transport failure reddens even when the http half is healthy" --grafana

grafana_up 401
spec "/ 302 /interface/login/login.php" "/agentforge/health 503" "/apis/default/fhir/metadata 200" "$READY_OK" \
     "/grafana/api/health 200" "/grafana/login#admin:admin 401"
expect 1 "a red http half is not rescued by a green grafana half" --grafana

# ONE login attempt per run, even when --wait retries the http half: Grafana locks a user name
# out after repeated failures, so a retried probe could lock out a real account named "admin".
spec "/ 302 /interface/login/login.php" "/agentforge/health 503" "/apis/default/fhir/metadata 200" "$READY_OK" \
     "/grafana/api/health 200" "/grafana/login#admin:admin 401"
: > "$tmp/log"
PDV_LOG="$tmp/log" expect 1 "(setup) a retried run" --grafana --wait 6
logins="$(grep -c '^/grafana/login' "$tmp/log" || true)"
retries="$(grep -c '^/agentforge/health$' "$tmp/log" || true)"
ran=$((ran + 1))
if [ "$logins" -ne 1 ] || [ "$retries" -lt 2 ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  expected one login across %s http rounds, saw %s\n' "$retries" "$logins" >&2
  fails=$((fails + 1))
fi

# The count, so "every case passed" cannot be confused with "no case ran".
EXPECTED_CASES=44
if [ "$ran" -ne "$EXPECTED_CASES" ]; then
  printf '\033[31mSELF-TEST FAILED\033[0m  ran %s of %s cases\n' "$ran" "$EXPECTED_CASES" >&2
  fails=$((fails + 1))
fi

if [ "$fails" -gt 0 ]; then
  printf '\n\033[31m%d self-test failure(s).\033[0m The post-deploy check is not behaving as documented.\n' "$fails" >&2
  exit 1
fi
printf '\033[32mok\033[0m  %s of %s cases - the post-deploy check still reddens on a broken deploy, and still passes a working one.\n' "$ran" "$EXPECTED_CASES"
