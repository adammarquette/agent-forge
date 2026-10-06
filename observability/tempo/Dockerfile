# syntax=docker/dockerfile:1
# Tempo image for a DEPLOYED sidecar. Build context is the repo root, so the COPY path below is
# root-relative, exactly like observability/loki/Dockerfile. Tempo must stay PRIVATE - it has no auth of
# its own - so no environment gives it a domain (.railway/railway.ts TEMPO_IMAGE_BY_ENV).
# reference: DEPLOYMENT.md, a separate change
FROM grafana/tempo:2.10.8

COPY observability/tempo/tempo.yaml /etc/tempo/tempo.yaml

# Run as root: the stock image runs as uid 10001, and a Railway volume mounts root-owned, so Tempo could not
# create /var/tempo/wal - the failure Loki crash-looped on. Acceptable for this private,
# synthetic-data-only service.
USER root

ENTRYPOINT ["/tempo"]
CMD ["-config.file=/etc/tempo/tempo.yaml"]
