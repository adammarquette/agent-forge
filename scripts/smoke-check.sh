#!/usr/bin/env bash
# The check that decides whether a deployment stays or is rolled back: the environment's URL must answer
# 2xx on HEALTH_PATH within about a minute. The URL comes from the repository variable STAGING_URL or
# PRODUCTION_URL; with none set, there is nothing deployed to check and it passes with a notice.
#
# Usage: scripts/smoke-check.sh <staging|production>
set -euo pipefail
env="${1:?environment}"
var="$(tr '[:lower:]' '[:upper:]' <<<"$env")_URL"
url="${!var:-}"
if [[ -z "$url" ]]; then
  echo "::notice::${var} is not set - no smoke check for ${env}."
  exit 0
fi
target="${url%/}${HEALTH_PATH:-/}"
for attempt in $(seq 1 10); do
  if curl --fail --silent --show-error --max-time 10 --output /dev/null "$target"; then
    echo "Smoke check passed: ${target}"
    exit 0
  fi
  echo "attempt ${attempt}/10: ${target} not healthy yet"
  sleep 6
done
echo "::error::Smoke check failed: ${target} did not answer 2xx."
exit 1
