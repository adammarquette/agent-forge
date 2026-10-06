#!/bin/sh
# Refuse to start when nginx's listen port and the sidecar's upstream port are the same number
# inside one network namespace. The collision is otherwise SILENT at config time and then reads
# as a completely unrelated misconfiguration.
#
# WHY THIS EXISTS. Under this repo's docker-compose.yml the sidecar declares
# `network_mode: "service:reverse-proxy"`, so nginx and Kestrel share one loopback
# (DEPLOYMENT.md section 2). nginx listens on ${PORT} - compose's DEMO_PORT - and
# proxies /agentforge/ to ${SIDECAR_UPSTREAM}, which compose renders as 127.0.0.1:${SIDECAR_PORT}.
# Set DEMO_PORT=8081, the obvious next number when 8080 is taken, and both become 8081. nginx wins
# the bind because it starts first; Kestrel dies on "Failed to bind to address ...: address already
# in use" and crash-loops under `restart: unless-stopped`; / serves OpenEMR perfectly while
# /agentforge/* 502s - which is exactly what a forgotten `--profile copilot` looks like, and is
# documented to mean that. `docker compose config` validates either way. With no sidecar running at
# all the same collision instead points nginx's /agentforge/ upstream at nginx itself.
#
# ONLY A LOOPBACK UPSTREAM CAN COLLIDE. Where the sidecar is a separate container - Railway, which
# passes `agent-forge-api.railway.internal:8080` - it has its own namespace and its own port space,
# so this check does not fire there, and must not.
#
# This is a `.sh`, not a `.envsh`: the stock nginx entrypoint runs `set -e` and executes *.sh
# directly, so a non-zero exit here aborts startup instead of being logged and ignored.
# reference: DEPLOYMENT.md section 2, a separate change
set -eu

_listen="${PORT:-8080}"
_upstream="${SIDECAR_UPSTREAM:-}"

case "$_upstream" in
    127.0.0.1:*|localhost:*)
        if [ "${_upstream##*:}" = "$_listen" ]; then
            echo "reverse-proxy: SIDECAR_UPSTREAM ($_upstream) names the same port nginx listens on ($_listen)." >&2
            echo "  They are one network namespace here, so only one process can hold that port: nginx would win" >&2
            echo "  the bind and the sidecar's Kestrel would crash-loop on 'address already in use', leaving" >&2
            echo "  /agentforge/* on 502 as though the copilot profile were missing. Refusing to start." >&2
            echo "  Under docker-compose.yml these two are DEMO_PORT and SIDECAR_PORT - give them different" >&2
            echo "  values (DEPLOYMENT.md section 2)." >&2
            exit 1
        fi
        ;;
esac

echo "15-assert-sidecar-port.sh: nginx listens on ${_listen}, sidecar upstream is ${_upstream:-<unset>} - no collision"
