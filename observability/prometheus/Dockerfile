# syntax=docker/dockerfile:1
# Prometheus image for a DEPLOYED sidecar — as opposed to observability/docker-compose.yml,
# which mounts the local config and scrapes a `dotnet run` sidecar on the host. Build context is the repo
# root, so the COPY paths below are root-relative, exactly like reverse-proxy/Dockerfile.
# reference: DEPLOYMENT.md, a separate change
FROM prom/prometheus:v3.15.0

# The scrape config arrives as a TEMPLATE, not the final file: docker-entrypoint.sh renders
# SIDECAR_TARGET into it at start, because the sidecar's address differs between a compose network
# and Railway's. Default below is the compose value, so the rendered file is unchanged there.
COPY observability/prometheus/prometheus.deployed.yml /etc/prometheus/prometheus.template.yml
COPY observability/alerts/agentforge-alerts.yml /etc/prometheus/alerts/agentforge-alerts.yml
COPY observability/prometheus/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

USER root
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
USER nobody

# The compose value (see the entrypoint): under compose the sidecar shares the proxy's network
# namespace, so `reverse-proxy:8081` IS the sidecar's Kestrel. A separate change
ENV SIDECAR_TARGET=reverse-proxy:8081

# Bind [::] rather than the default 0.0.0.0:9090 (IPv4 only): some container networks resolve service
# names to IPv6 addresses, and an IPv4-only bind is unreachable there. Fixed :9090 keeps the Grafana
# datasource URL stable. This service must stay private — it has no auth of its own.
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["--config.file=/etc/prometheus/prometheus.yml", \
     "--storage.tsdb.path=/prometheus", \
     "--web.console.libraries=/usr/share/prometheus/console_libraries", \
     "--web.console.templates=/usr/share/prometheus/consoles", \
     "--web.listen-address=[::]:9090"]
