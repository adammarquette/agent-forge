# Deployment Runbook: GitLab CI/CD → Railway

This project deploys two applications from GitLab (`labs.gauntletai.com`) to
[Railway](https://railway.com):

| Repo | App | Railway service |
|------|-----|-----------------|
| `adammarquette/agent-forge` | OpenEMR (this repo) | `openemr` (+ `MySQL`) |
| `adammarquette/agent-forge-copilot` | Copilot companion app | `copilot` |

Three environments live in **one Railway project**, promoted in order:

```
push to main ──auto──▶ development ──manual──▶ sdet ──manual──▶ production
```

Railway does not integrate natively with self-hosted GitLab, so deploys run
from GitLab CI using the Railway CLI and environment-scoped **project tokens**
(`railway up` uploads the source; Railway builds it with
`docker/railway/Dockerfile` per `railway.json`).

---

## 1. One-time Railway setup

### 1.1 Project and environments

1. In the [Railway dashboard](https://railway.com/dashboard), **New Project**
   → "Empty Project". Name it `agent-forge`.
2. Project **Settings → Environments**. The default environment is
   `production`. Add two more: `development` and `sdet`.
   (Services exist across all environments; each environment gets its own
   instances, variables, volumes, and domains.)

### 1.2 MySQL service

1. In the project canvas: **Create → Database → MySQL**.
2. Repeat nothing — the service exists in every environment automatically,
   but verify each environment shows its own MySQL instance and volume
   (switch environments with the dropdown at the top).

### 1.3 OpenEMR service

1. **Create → Empty Service**, name it `openemr` (must match
   `RAILWAY_SERVICE` in `.gitlab-ci.yml`).
2. In **each** environment, set the service **Variables** (use Railway
   reference syntax so credentials follow the MySQL service):

   ```
   MYSQL_HOST=${{MySQL.MYSQLHOST}}
   MYSQL_PORT=${{MySQL.MYSQLPORT}}
   MYSQL_ROOT_PASS=${{MySQL.MYSQLPASSWORD}}
   MYSQL_USER=openemr
   MYSQL_PASS=<pick a password, different per environment>
   OE_USER=admin
   OE_PASS=<pick an admin password, different per environment>
   SWARM_MODE=yes
   ```

   `SWARM_MODE=yes` matches this fork's documented setup (see §5 below) and
   enables `handle_swarm_mode()`'s leader-election and `/swarm-pieces/`
   restore logic. Without it that coordination is skipped entirely, which
   this doc has always assumed is on -- not previously listed here, which
   was an oversight.

3. **Settings → Volumes**: attach a volume mounted at
   `/var/www/localhost/htdocs/openemr/sites` (persists site config,
   uploaded documents, and generated certificates across restarts).
4. **Settings → Networking → Public Networking**: generate a domain and set
   the **target port to 80** (the container serves HTTP on 80; Railway
   terminates TLS at the edge).
5. **Set the `PORT` variable to `80`.** The domain's "target port" setting
   only controls edge routing — Railway's own healthcheck/internal proxy
   targets whatever `PORT` resolves to, which otherwise silently defaults
   away from 80. Without this, the build succeeds and Apache starts
   normally, but the healthcheck never passes ("service unavailable" on
   every retry until the 10-minute timeout) because Railway is probing a
   port nothing is listening on.

### 1.4 Copilot service

1. **Create → Empty Service**, name it `copilot`.
2. Configure variables/volumes as that app requires (its Railway build is
   auto-detected by Railpack unless the copilot repo adds its own
   `railway.json`/Dockerfile).
3. If it needs a public URL, generate a domain the same way.

### 1.5 Project tokens (one per environment)

Project **Settings → Tokens**: create three tokens, each scoped to one
environment:

| Token name | Environment |
|------------|-------------|
| `gitlab-ci-development` | development |
| `gitlab-ci-sdet` | sdet |
| `gitlab-ci-production` | production |

Copy each value immediately — Railway shows it only once.

## 2. One-time GitLab setup (both repos)

In **each** repo (`agent-forge` and `agent-forge-copilot`):
**Settings → CI/CD → Variables**, add (Masked; Protected only if `main` is a
protected branch):

| Key | Value |
|-----|-------|
| `RAILWAY_TOKEN_DEVELOPMENT` | the development-scoped token |
| `RAILWAY_TOKEN_SDET` | the sdet-scoped token |
| `RAILWAY_TOKEN_PRODUCTION` | the production-scoped token |

The same three Railway tokens work for both repos — the token selects the
*environment*, the `--service` flag in each repo's CI selects the *service*.

## 3. CI pipeline for the copilot repo

Copy this as `.gitlab-ci.yml` in `agent-forge-copilot` (identical to this
repo's pipeline except for the service name):

```yaml
stages:
  - deploy

variables:
  RAILWAY_SERVICE: copilot

.railway-deploy:
  stage: deploy
  image: ghcr.io/railwayapp/cli:latest
  variables:
    GIT_DEPTH: "1"
  script:
    - railway up --service "$RAILWAY_SERVICE" --ci

deploy:development:
  extends: .railway-deploy
  variables:
    RAILWAY_TOKEN: $RAILWAY_TOKEN_DEVELOPMENT
  environment:
    name: development
  rules:
    - if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH'

deploy:sdet:
  extends: .railway-deploy
  variables:
    RAILWAY_TOKEN: $RAILWAY_TOKEN_SDET
  environment:
    name: sdet
  rules:
    - if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH'
      when: manual
      allow_failure: true

deploy:production:
  extends: .railway-deploy
  variables:
    RAILWAY_TOKEN: $RAILWAY_TOKEN_PRODUCTION
  environment:
    name: production
  needs:
    - deploy:sdet
  rules:
    - if: '$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH'
      when: manual
      allow_failure: true
```

## 4. Day-to-day workflow

1. Merge/push to `main` → the pipeline auto-deploys to **development**.
2. When development looks good, open the pipeline in GitLab and press ▶ on
   `deploy:sdet`. Run tests against the sdet URL.
3. When sdet passes, press ▶ on `deploy:production` (GitLab refuses to run
   it until `deploy:sdet` succeeded in that pipeline).

GitLab's **Operate → Environments** page tracks what commit is live in each
environment.

## 5. OpenEMR-on-Railway specifics

- **First deploy is slow.** `openemr.sh` runs the full database setup
  (`auto_configure.php`) on first boot — expect several minutes before the
  healthcheck (`/interface/login/login.php?site=default`, 600 s timeout)
  goes green. Subsequent deploys reuse the configured database and volume.
- **Build**: `docker/railway/Dockerfile` is derived from
  `docker/release/Dockerfile` but builds **this repo's source** (`COPY .`)
  instead of cloning upstream `openemr/openemr` from GitHub. If upstream's
  release Dockerfile changes materially, re-derive.
- **Login**: `OE_USER`/`OE_PASS` from the service variables (per
  environment).
- **Logs**: Railway dashboard → service → Deployments → View Logs, or
  locally `railway logs` after `railway link`.
- **Resetting an environment**: delete the MySQL volume + the `sites` volume
  for that environment and redeploy; OpenEMR re-runs setup from scratch.

## 6. Troubleshooting

- `Could not find service` in CI → service name in Railway doesn't match
  `RAILWAY_SERVICE` (`openemr` / `copilot`).
- `Unauthorized` in CI → token missing/wrong scope; project tokens are
  per-environment, check the job used the matching `RAILWAY_TOKEN_*`.
- Healthcheck timeout on first deploy → check deploy logs; usually MySQL
  variables are missing/wrong in that environment.
- Build OOM/timeout → the webpack + composer build is heavy; retry, or
  build the image in GitLab CI and `railway up` an artifact instead (not
  currently needed).
