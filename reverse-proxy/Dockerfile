# syntax=docker/dockerfile:1
# Same-origin reverse-proxy front door: stock nginx, no custom entrypoint. nginx's own image already runs
# docker-entrypoint.d/20-envsubst-on-templates.sh at container start, which
# envsubst's every *.template file under /etc/nginx/templates/ into
# /etc/nginx/conf.d/ - that's how OPENEMR_UPSTREAM/SIDECAR_UPSTREAM/PORT below
# reach the config without a custom image.
FROM nginx:1.30-alpine

COPY nginx.conf.template /etc/nginx/templates/default.conf.template

# Sourced by the stock entrypoint before the envsubst step (see the script).
# Must be executable or the entrypoint skips it silently.
COPY 10-resolver.envsh /docker-entrypoint.d/10-resolver.envsh
RUN chmod +x /docker-entrypoint.d/10-resolver.envsh

# FAILS CLOSED, and runs before the envsubst step for the same reason. A loopback
# SIDECAR_UPSTREAM naming the port nginx itself listens on is a misconfiguration compose cannot
# detect - `docker compose config` validates it happily - and its only symptom is the 502 that a
# forgotten `--profile copilot` also produces. The script refuses to start rather than serve it.
# Executable for the same reason as above: the stock entrypoint skips a non-executable hook
# silently, which would make this guard a comment. A separate change
COPY 15-assert-sidecar-port.sh /docker-entrypoint.d/15-assert-sidecar-port.sh
RUN chmod +x /docker-entrypoint.d/15-assert-sidecar-port.sh

# Defaults match the repo-root docker-compose.yml; override both for any other stack.
# Port 80 for OpenEMR is confirmed against the fork's actual Dockerfile (Apache
# hardcoded to `Listen 0.0.0.0:80`, `EXPOSE 80 443` - no PORT-env override).
#
# THE SIDECAR DEFAULT IS A LOOPBACK LITERAL, NOT A SERVICE NAME, and that is not a
# typo: under compose the sidecar shares THIS container's network namespace, so its
# Kestrel is on this container's loopback and has no DNS name of its own. It runs on
# 8081 rather than its own Dockerfile's ${PORT:-8080} default because nginx already
# holds 8080 in that shared namespace. Any stack where the sidecar IS a separate
# container - Railway, which sets this explicitly - passes its own host:port instead.
# reference: DEPLOYMENT.md §2, a separate change
ENV OPENEMR_UPSTREAM=openemr:80 \
    SIDECAR_UPSTREAM=127.0.0.1:8081

# EMPTY BY DEFAULT, AND EMPTY IS A SUPPORTED STATE, not a missing value: Grafana exists only
# where .railway/railway.ts has observability pins (staging), so production and every compose
# stack run with no grafana service to route to and /grafana answers 404 - see the block in
# nginx.conf.template for how the empty rendering stays valid nginx. DEFINED here rather than
# left unset because the stock entrypoint's envsubst only substitutes variables that exist in
# the environment: unset, `${GRAFANA_UPSTREAM}` would survive into the rendered config as
# literal text and nginx would refuse to start on an unknown variable. A separate change
ENV GRAFANA_UPSTREAM=

# EMPTY BY DEFAULT, not 127.0.0.11: 10-resolver.envsh reads the real resolver out
# of the container's own /etc/resolv.conf at start, so one image works under
# Docker (embedded DNS) and on Railway (private networking) with no per-stack
# hardcoding. Set this to pin a resolver and skip detection; an IPv6 literal must
# be bracketed, because nginx's resolver directive requires it.
ENV DNS_RESOLVER=

# Resolve upstreams over IPv4 only. Railway private DNS is dual-stack (A + AAAA),
# but BOTH upstreams listen on IPv4 only - the sidecar's entrypoint binds
# 0.0.0.0 and the OpenEMR fork's Apache hardcodes `Listen 0.0.0.0:80` - so an
# AAAA answer yields a connection refused on an otherwise healthy stack. Harmless
# under Docker, whose embedded DNS returns A records anyway. Set to `on` only for
# a legacy IPv6-only Railway environment (pre-2025-10-16). A separate change (deleted GitHub repository)
ENV RESOLVER_IPV6=off

# The only peers whose X-Real-IP header nginx believes (set_real_ip_from), for the per-client rate
# limits. 100.64.0.0/10 is where Railway's edge connects from; under compose the peer is the Docker
# bridge, outside it, so the header is ignored and the peer address is the client. One CIDR - it is a
# single directive in the template. A separate change, reverse-proxy/nginx.conf.template
ENV REAL_IP_TRUSTED_CIDR=100.64.0.0/10

# Overridden at runtime by the environment; the template listens on it directly.
ENV PORT=8080
