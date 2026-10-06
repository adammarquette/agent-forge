#!/bin/sh
# Pins reverse-proxy/nginx.conf.template's public-metrics block: renders the template
# and runs a REAL nginx against it, then asserts on the HTTP response a client outside the
# network would see. `nginx-config-lint` only proves the rendered config is syntactically valid
# (`nginx -t`) - it stays green whether or not the /agentforge/metrics location exists at all, so
# a later edit that drops the block, or narrows it to miss a trailing slash, would pass that job
# and still leak Prometheus text on the front door. This suite is the one that would go red for
# that reason. Since a separate change it also pins the per-client rate limits (429 past the burst, never
# before it) on chat, evidence, the agenda roster and /ready, and the trusted-X-Real-IP keying they depend on.
# Since a separate change it pins the dotfile deny (404 at any depth) and the .well-known exception SMART discovery needs.
#
# Both upstreams are deliberately unreachable loopback addresses. The cases exercise routes
# nginx answers itself (a bare `return`). The rate-limit cases read a proxied route's 502 as
# "nginx let it through to the upstream" and a 429 as "nginx refused it" - no real sidecar needed.
#
# The rate-limit cases trust 127.0.0.1 as the edge (REAL_IP_TRUSTED_CIDR), so X-Real-IP picks the
# bucket for requests from it, and send the spoofing case from 127.0.0.2, which is not trusted.
set -u

HERE=$(cd "$(dirname "$0")" && pwd)
TEMPLATE="$HERE/nginx.conf.template"
SBX=$(mktemp -d) || exit 2
NGINX_PID=""
cleanup() {
    [ -n "$NGINX_PID" ] && kill "$NGINX_PID" 2>/dev/null
    rm -rf "$SBX"
}
trap cleanup EXIT

[ -r "$TEMPLATE" ] || { echo "nginx-behavior-selftest: CANNOT RUN - $TEMPLATE is not readable" >&2; exit 2; }
command -v nginx >/dev/null 2>&1 || { echo "nginx-behavior-selftest: CANNOT RUN - nginx is not on PATH; run this in the nginx:1.30-alpine image, same as nginx-config-lint" >&2; exit 2; }
command -v curl >/dev/null 2>&1 || { echo "nginx-behavior-selftest: CANNOT RUN - curl is not on PATH" >&2; exit 2; }
command -v envsubst >/dev/null 2>&1 || { echo "nginx-behavior-selftest: CANNOT RUN - envsubst is not on PATH" >&2; exit 2; }

asserts=0; fails=0
assert_eq() { # assert_eq <got> <want> <description>
    asserts=$((asserts + 1))
    if [ "$1" = "$2" ]; then
        printf '  ok    %s\n' "$3"
    else
        fails=$((fails + 1))
        printf '  FAIL  %s (expected %s, got %s)\n' "$3" "$2" "$1" >&2
    fi
}

PORT=18080
OPENEMR_UPSTREAM="127.0.0.1:9"
SIDECAR_UPSTREAM="127.0.0.1:9"
DNS_RESOLVER="127.0.0.11"
RESOLVER_IPV6="off"
GRAFANA_UPSTREAM=""
REAL_IP_TRUSTED_CIDR="127.0.0.1/32"
export PORT OPENEMR_UPSTREAM SIDECAR_UPSTREAM DNS_RESOLVER RESOLVER_IPV6 GRAFANA_UPSTREAM REAL_IP_TRUSTED_CIDR

envsubst '$PORT $OPENEMR_UPSTREAM $SIDECAR_UPSTREAM $GRAFANA_UPSTREAM $DNS_RESOLVER $RESOLVER_IPV6 $REAL_IP_TRUSTED_CIDR' \
    < "$TEMPLATE" > "$SBX/default.conf" || { echo "nginx-behavior-selftest: CANNOT RUN - envsubst failed" >&2; exit 2; }

mkdir -p "$SBX/logs"
cat > "$SBX/nginx.conf" <<EOF
worker_processes 1;
error_log $SBX/logs/error.log;
pid $SBX/nginx.pid;
events { worker_connections 32; }
http {
    access_log off;
    include $SBX/default.conf;
}
EOF

nginx -t -c "$SBX/nginx.conf" >"$SBX/nginx-t.out" 2>&1
if [ $? -ne 0 ]; then
    echo "nginx-behavior-selftest: CANNOT RUN - the render this suite built does not pass nginx -t:" >&2
    cat "$SBX/nginx-t.out" >&2
    exit 2
fi

nginx -c "$SBX/nginx.conf" -g "daemon off;" &
NGINX_PID=$!

i=0
until curl -s -o /dev/null "http://127.0.0.1:$PORT/agentforge/metrics" 2>/dev/null; do
    i=$((i + 1))
    if [ "$i" -gt 50 ]; then
        echo "nginx-behavior-selftest: CANNOT RUN - nginx did not come up on :$PORT within 5s" >&2
        exit 2
    fi
    sleep 0.1
done

echo "== /agentforge/metrics is blocked at the front door =="
status=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/metrics")
assert_eq "$status" "404" "GET /agentforge/metrics answers 404, not the sidecar's Prometheus text"

status_slash=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/metrics/")
assert_eq "$status_slash" "404" "the trailing-slash spelling is blocked too, not just the exact path"

body=$(curl -s "http://127.0.0.1:$PORT/agentforge/metrics")
case "$body" in
    *"# TYPE"*|*"# HELP"*) leaked="yes" ;;
    *) leaked="no" ;;
esac
assert_eq "$leaked" "no" "the 404 body carries no Prometheus exposition-format text"

# The sidecar matches paths case-insensitively (UsePathBase + endpoint routing), so a case-sensitive
# nginx prefix location would let these spellings fall through to /agentforge/ and reach the sidecar.
# --path-as-is keeps curl from normalising the path, so the percent-encoded byte reaches nginx literally.
# These are the exact spellings the reviewer proxied to the sidecar at the pre-fix head. A separate change
status_mixed=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/Metrics")
assert_eq "$status_mixed" "404" "the mixed-case spelling /agentforge/Metrics is blocked (case-insensitive block)"

status_upper=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/METRICS")
assert_eq "$status_upper" "404" "the upper-case spelling /agentforge/METRICS is blocked"

# %4D decodes to 'M', so /agentforge/%4Detrics is /agentforge/Metrics after nginx normalises it.
status_pct=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/%4Detrics")
assert_eq "$status_pct" "404" "the percent-encoded upper-case spelling /agentforge/%4Detrics is blocked"

echo "== the pre-existing W2-D17 block is unaffected, and closed to the same case bypass =="
documents_status=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/documents/")
assert_eq "$documents_status" "404" "/agentforge/documents/ still 404s (this suite didn't just delete every location)"

documents_mixed=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/agentforge/Documents/x")
assert_eq "$documents_mixed" "404" "the mixed-case spelling /agentforge/Documents/ is blocked too, not just the sidecar-lowercased path"

echo "== dotfiles are denied at the front door, .well-known is not =="
# The Nuclei baseline's finding: OpenEMR's docroot served /.gitmodules. OpenEMR is unreachable here, so
# 404 means nginx refused it and 502 means nginx let it through to OpenEMR.
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/.gitmodules")
assert_eq "$status" "404" "GET /.gitmodules answers 404, not OpenEMR's docroot"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/.git/config")
assert_eq "$status" "404" "a dot-directory (/.git/config) is denied too"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/interface/.htaccess")
assert_eq "$status" "404" "a dotfile below the root (/interface/.htaccess) is denied, not only top-level ones"
# %2e decodes to '.', so this is /.gitmodules after nginx normalises it.
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/%2egitmodules")
assert_eq "$status" "404" "the percent-encoded spelling /%2egitmodules is denied"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/.well-known-evil")
assert_eq "$status" "404" "/.well-known-evil does not ride the .well-known exception"
# SMART and OIDC discovery must still reach OpenEMR: the sidecar's readiness probe reads the first.
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/apis/default/fhir/.well-known/smart-configuration")
assert_eq "$status" "502" "SMART discovery (/apis/default/fhir/.well-known/smart-configuration) still reaches OpenEMR"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/oauth2/default/.well-known/openid-configuration")
assert_eq "$status" "502" "OIDC discovery (/oauth2/default/.well-known/openid-configuration) still reaches OpenEMR"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/.well-known/security.txt")
assert_eq "$status" "502" "a root /.well-known/ path still reaches OpenEMR"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/interface/login/login.php")
assert_eq "$status" "502" "an ordinary OpenEMR path still reaches OpenEMR (the deny is not a blanket 404)"

echo "== per-client edge rate limits on chat and evidence =="
# hit <n> <method> <path> [curl args...] - sends n requests and prints "<how many answered 429> <last status>".
hit() {
    _n=$1; _m=$2; _p=$3; shift 3
    _c=0; _last=""; _i=0
    while [ "$_i" -lt "$_n" ]; do
        _i=$((_i + 1))
        _last=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' -X "$_m" "$@" "http://127.0.0.1:$PORT$_p")
        [ "$_last" = "429" ] && _c=$((_c + 1))
    done
    echo "$_c $_last"
}

# Chat allows 1 + burst(30) at once. 31 back-to-back requests from one client is more than a page load
# plus a reconnect storm; none may be refused, or normal SignalR traffic trips the limit.
# shellcheck disable=SC2046
set -- $(hit 31 GET /agentforge/hubs/chat/negotiate -H "X-Real-IP: 198.51.100.1")
assert_eq "$1" "0" "chat: 31 back-to-back requests from one client are all let through (the burst is generous enough)"
# shellcheck disable=SC2046
set -- $(hit 14 GET /agentforge/hubs/chat/negotiate -H "X-Real-IP: 198.51.100.1")
assert_eq "$2" "429" "chat: the same client past its burst is answered 429"
status=$(curl -s --path-as-is -o /dev/null -w '%{http_code}' -H "X-Real-IP: 198.51.100.1" "http://127.0.0.1:$PORT/agentforge/HUBS/Chat/negotiate")
assert_eq "$status" "429" "chat: the upper-case spelling is the same bucket, not a way around it"

# Only the limited routes: the same exhausted client still reaches the rest of the sidecar and OpenEMR.
status=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Real-IP: 198.51.100.1" "http://127.0.0.1:$PORT/agentforge/launch")
assert_eq "$status" "502" "an exhausted chat client still reaches /agentforge/launch (the SMART launch is not limited)"
status=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Real-IP: 198.51.100.1" "http://127.0.0.1:$PORT/apis/default/fhir/Patient")
assert_eq "$status" "502" "an exhausted chat client still reaches OpenEMR at / (FHIR is not limited)"

# The key is the forwarded client, not the TCP peer: another client behind the same edge is unaffected.
status=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Real-IP: 198.51.100.3" "http://127.0.0.1:$PORT/agentforge/hubs/chat/negotiate")
assert_eq "$status" "502" "chat: a different client behind the same trusted edge has its own bucket"

# Evidence allows 1 + burst(10); /evidence/ask is an LLM call per request.
# shellcheck disable=SC2046
set -- $(hit 11 POST /agentforge/evidence/ask -H "X-Real-IP: 198.51.100.2")
assert_eq "$1" "0" "evidence: 11 back-to-back requests from one client are all let through"
# shellcheck disable=SC2046
set -- $(hit 9 POST /agentforge/Evidence/ask -H "X-Real-IP: 198.51.100.2")
assert_eq "$2" "429" "evidence: the same client past its burst is answered 429, whatever the path's case"

# /ready allows 1 + burst(10) and is sustained at 30/min; post-deploy-verify.sh --wait polls it every 5s
# (12/min), which must never trip it. /health is liveness only and not limited.
# shellcheck disable=SC2046
set -- $(hit 11 GET /agentforge/ready -H "X-Real-IP: 198.51.100.4")
assert_eq "$1" "0" "ready: 11 back-to-back probes from one client are all let through"
# shellcheck disable=SC2046
set -- $(hit 9 GET /agentforge/Ready -H "X-Real-IP: 198.51.100.4")
assert_eq "$2" "429" "ready: the same client past its burst is answered 429, whatever the path's case"
status=$(curl -s -o /dev/null -w '%{http_code}' -H "X-Real-IP: 198.51.100.4" "http://127.0.0.1:$PORT/agentforge/health")
assert_eq "$status" "502" "ready: an exhausted client still reaches /agentforge/health (liveness is not limited)"

# The agenda roster allows 1 + burst(5) at 10/min: every load runs one LLM summary per rostered patient.
# Only the roster fetch - the drill-down that follows it must still work for an exhausted client.
# shellcheck disable=SC2046
set -- $(hit 6 GET /agentforge/agenda -H "X-Real-IP: 198.51.100.5")
assert_eq "$1" "0" "agenda: 6 back-to-back roster loads from one client are all let through"
# shellcheck disable=SC2046
set -- $(hit 6 GET /agentforge/Agenda -H "X-Real-IP: 198.51.100.5")
assert_eq "$2" "429" "agenda: the same client past its burst is answered 429, whatever the path's case"
status=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Real-IP: 198.51.100.5" "http://127.0.0.1:$PORT/agentforge/agenda/select-patient")
assert_eq "$status" "502" "agenda: an exhausted client can still drill down (select-patient is not limited)"

# X-Real-IP is believed only from the trusted edge. From an untrusted peer a fresh header per request
# must not buy a fresh bucket - otherwise the limit is one header away from not existing.
spoof_last=""
_i=0
while [ "$_i" -lt 45 ]; do
    _i=$((_i + 1))
    spoof_last=$(curl -s -o /dev/null -w '%{http_code}' --interface 127.0.0.2 -H "X-Real-IP: 203.0.113.$_i" "http://127.0.0.1:$PORT/agentforge/hubs/chat/negotiate")
done
assert_eq "$spoof_last" "429" "chat: an untrusted peer rotating X-Real-IP is still limited on its own address"

EXPECTED_ASSERTIONS=32
echo
if [ "$asserts" -ne "$EXPECTED_ASSERTIONS" ]; then
    echo "SELF-TEST FAILED - ran $asserts assertions, expected $EXPECTED_ASSERTIONS: a case stopped running, or one was added without updating EXPECTED_ASSERTIONS." >&2
    exit 1
fi
if [ "$fails" -ne 0 ]; then
    echo "SELF-TEST FAILED - $fails of $asserts assertions" >&2
    exit 1
fi
echo "SELF-TEST PASSED - $asserts of $EXPECTED_ASSERTIONS assertions: /agentforge/metrics and dotfiles are not publicly reachable, and chat/evidence/agenda/ready are rate-limited per client"
