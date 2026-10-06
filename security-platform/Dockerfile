# syntax=docker/dockerfile:1
# Deterministic replayer image. Build context is security-platform/, not the repo root:
# the COPY paths below are context-relative, the same reason as reverse-proxy/Dockerfile.
# Staging only. The image's default command is `serve` — it answers /health and does not attack.
# reference: DEPLOYMENT.md, a separate change
FROM python:3.12-alpine@sha256:4c47124a8391cb7a9f571164147d154777cf012a4ece5f86097130d7a4478111

WORKDIR /opt/security-platform
COPY . /opt/security-platform
ENV PYTHONPATH=/opt/security-platform \
    PYTHONDONTWRITEBYTECODE=1

USER nobody
EXPOSE 8080
ENTRYPOINT ["python", "-m", "security_platform"]
CMD ["serve"]
