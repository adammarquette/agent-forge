# syntax=docker/dockerfile:1
# check=skip=SecretsUsedInArgOrEnv
#   ^ BuildKit flags any ENV matching *AUTH* as a secret. GF_AUTH_ANONYMOUS_ENABLED is a boolean
#     policy flag, not a credential; the real credentials come from the environment at runtime.
# Grafana image for a DEPLOYED sidecar: the same AgentForge dashboard + datasources as local
# compose, pre-provisioned into the image. Build context = repo root, like reverse-proxy/Dockerfile.
# That holds for an image built FROM THIS TREE. It is not a claim about whatever tag an environment
# happens to pin. Staging runs each develop push's build of this tree; the hand-built
# grafana-sha-4768e5d79880 it ran before predates a separate change (18 non-row panels). See .railway/railway.ts.
#
# Admin credentials are NOT baked here — they come from the environment at runtime
# (GF_SECURITY_ADMIN_USER / GF_SECURITY_ADMIN_PASSWORD), and anonymous access is
# disabled (GF_AUTH_ANONYMOUS_ENABLED=false), so the dashboard requires a login.
# reference: DEPLOYMENT.md, a separate change

# The metrics-only dashboard: every Loki-backed panel removed, at the top level or inside a
# row. Built here rather than at start because the Grafana image has no JSON tool. The build FAILS if
# the variant still mentions Loki or removed nothing, so a Loki panel added in some new shape cannot
# ship to an environment without Loki.
FROM alpine:3.21 AS dashboards
RUN apk add --no-cache jq
COPY observability/grafana/dashboards/agentforge.json /in/agentforge.json
RUN mkdir -p /out \
    && jq 'def strip: map(select((.datasource.type? // "") != "loki") | if has("panels") then .panels |= strip else . end); .panels |= strip' \
        /in/agentforge.json > /out/agentforge.metrics-only.json \
    && ! grep -qi loki /out/agentforge.metrics-only.json \
    && [ "$(jq '[.. | objects | select(has("gridPos"))] | length' /in/agentforge.json)" -gt \
         "$(jq '[.. | objects | select(has("gridPos"))] | length' /out/agentforge.metrics-only.json)" ]

FROM grafana/grafana:12.4.11

# Owned by the runtime uid (472) so the entrypoint can prune the Loki and Tempo files.
COPY --chown=472:0 observability/grafana/dashboards /var/lib/grafana/dashboards
COPY --chown=472:0 observability/grafana/provisioning /etc/grafana/provisioning
# Replace the local-compose datasources with the deployed stack's service addresses. This image only;
# local compose (which mounts ../provisioning directly) is untouched.
COPY --chown=472:0 observability/grafana/deployed/datasource.yml /etc/grafana/provisioning/datasources/prometheus.yml
COPY --chown=472:0 observability/grafana/deployed/loki-datasource.yml /etc/grafana/provisioning/datasources/loki.yml
COPY --chown=472:0 observability/grafana/deployed/tempo-datasource.yml /etc/grafana/provisioning/datasources/tempo.yml
COPY --from=dashboards /out/agentforge.metrics-only.json /usr/share/agentforge/agentforge.metrics-only.json
COPY observability/grafana/docker-entrypoint.sh /usr/local/bin/agentforge-entrypoint.sh

USER root
RUN chmod +x /usr/local/bin/agentforge-entrypoint.sh
USER 472

# PROMETHEUS_URL defaults to the compose container name, so this image behaves identically on a
# compose network; a deployment overrides it with its private hostname. LOKI_URL HAS NO DEFAULT, on
# purpose: unset means "this environment runs no Loki", and the entrypoint then provisions
# neither the datasource nor its panel. TEMPO_URL likewise: unset means no Tempo datasource.
ENV PROMETHEUS_URL=http://agentforge-prometheus:9090

# The auth posture, SET rather than inherited. Both of these are already Grafana's defaults, which is
# why the header above could claim them - but this image is the one that gets published at a public
# origin, and a default is not a gate. An upstream default that changes, or a stray env var on the
# platform, silently turns the dashboard into an open one.
ENV GF_AUTH_ANONYMOUS_ENABLED=false \
    GF_USERS_ALLOW_SIGN_UP=false

ENTRYPOINT ["/usr/local/bin/agentforge-entrypoint.sh"]
