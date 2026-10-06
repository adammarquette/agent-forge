#!/usr/bin/env bash
# Deploy the images tagged :<environment> to that environment. Call it with the version being deployed,
# and again with the previous version to roll back; exit non-zero on failure.
# Credentials come from the caller's environment (e.g. secrets.STAGING_DEPLOY_TOKEN), never from this file.
#
# Usage: scripts/deploy.sh <staging|production> <version>
set -euo pipefail
env="${1:?environment}"; version="${2:?version}"

case "$env" in
  staging|production)
    # guide: replace this with your hosting provider's command that makes the environment run the
    # :${env} images (it pulls the moving tag, so a rollback is the same command), e.g.
    #   az containerapp update --name "<app>-${env}" --image "ghcr.io/<owner>/<repo>:${env}"
    #   kubectl -n "${env}" rollout restart deployment/<app> && kubectl -n "${env}" rollout status deployment/<app>
    echo "::notice::No deployment target configured for ${env} yet (scripts/deploy.sh): ${version} is tagged :${env}, not running anywhere."
    ;;
  *)
    echo "unknown environment '${env}'" >&2
    exit 2
    ;;
esac
