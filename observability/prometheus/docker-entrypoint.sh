#!/bin/sh
# Renders the scrape target into the config, then execs Prometheus.
#
# WHY THIS EXISTS. Prometheus does no environment substitution of its own, and the sidecar's address
# differs per stack: `reverse-proxy:${SIDECAR_PORT:-8081}` on a compose network - where the sidecar shares
# the proxy's network namespace and so has no name of its own (DEPLOYMENT.md §2) - and
# `agent-forge-api.railway.internal:8080` on Railway's private network, where it is an ordinary
# separate service (IPv6-only, hence the [::] bind in the Dockerfile). The default below is
# the compose value, so an unset SIDECAR_TARGET renders exactly the template as committed.
#
# docker-compose.observability.yml BUILDS THIS IMAGE rather than mounting a config into stock
# prom/prometheus, and this script is the reason: the overlay passes SIDECAR_TARGET derived from
# ${SIDECAR_PORT}, so moving that knob moves the scrape with Kestrel and the proxy instead of leaving a dead
# target behind a /ready that still reports Healthy. A mounted static config could only hold a literal.
#
# THE DEFAULT, THE sed PATTERN AND prometheus.deployed.yml's TARGET ARE ONE VALUE IN THREE PLACES.
# The substitution is a literal match on the template's own address, so changing the template alone
# makes this a silent no-op - which the fail-closed check below then turns into a refusal to start.
# Move all three together, plus ENV SIDECAR_TARGET in the Dockerfile.
#
# reference: DEPLOYMENT.md, a separate change
set -eu

TARGET="${SIDECAR_TARGET:-reverse-proxy:8081}"

sed "s|reverse-proxy:8081|${TARGET}|g" \
    /etc/prometheus/prometheus.template.yml > /etc/prometheus/prometheus.yml

# FAIL CLOSED. Prometheus starts happily with zero scrape targets and reports nothing wrong - a
# dashboard of empty panels is the only symptom, and it reads as "no traffic yet". So assert the
# render actually placed the target: if the template's default address is ever renamed, the
# substitution above silently no-ops and this is what catches it.
if ! grep -qF "\"${TARGET}\"" /etc/prometheus/prometheus.yml; then
    echo "prometheus: scrape target '${TARGET}' is not in the rendered config - refusing to start" >&2
    echo "  (the template's default address no longer matches what this script substitutes)" >&2
    exit 1
fi

# TSDB RETENTION, opt-in per deployment. Unset leaves Prometheus's own default - 15d
# and NO size cap - which is what both local wirings run. Set on Railway, where the TSDB sits on
# a fixed-size volume. A malformed value is refused by Prometheus itself at start, never ignored.
if [ -n "${PROMETHEUS_RETENTION_TIME:-}" ]; then
    set -- "$@" "--storage.tsdb.retention.time=${PROMETHEUS_RETENTION_TIME}"
fi
if [ -n "${PROMETHEUS_RETENTION_SIZE:-}" ]; then
    set -- "$@" "--storage.tsdb.retention.size=${PROMETHEUS_RETENTION_SIZE}"
fi

exec /bin/prometheus "$@"
