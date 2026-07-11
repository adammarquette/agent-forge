# Deployment Runbook: GitLab CI/CD → Railway

This project deploys two applications from GitLab (`labs.gauntletai.com`) to
[Railway](https://railway.com):

| Repo | App | Railway service |
|------|-----|-----------------|
| `adammarquette/agent-forge` | OpenEMR (this repo) | `openemr` (+ `MySQL`) |
| `adammarquette/agent-forge-copilot` | Copilot companion app | `agent-forge-api` |

**Single-environment model:** push to `main` auto-deploys to Railway's
`staging` environment. (An earlier version of this doc described a
development/sdet/production promotion chain; Railway's actual project only
ever had two environments, and the working deployment has lived in
`staging` since 2026-07-10 — see `agent-forge#8`'s update. The live project
is currently named `lucid-clarity` in the Railway dashboard, not
`agent-forge` — Railway auto-names projects created without an explicit
name, and this one was never renamed.)

Railway does not integrate natively with self-hosted GitLab, so deploys run
from GitLab CI using the Railway CLI and an environment-scoped **project
token** (`railway up` uploads the source; Railway builds it with
`docker/railway/Dockerfile` per `railway.json`).

---

## 1. One-time Railway setup

### 1.1 Project and environment

1. In the [Railway dashboard](https://railway.com/dashboard), **New Project**
   → "Empty Project". Name it `agent-forge` (or use an existing project —
   the live one is currently named `lucid-clarity`).
2. Project **Settings → Environments**. Rename the default environment to
   `staging`, or add a `staging` environment if you'd rather keep the
   default around for something else.

### 1.2 MySQL service

1. In the project canvas: **Create → Database → MySQL**.
2. Verify the `staging` environment shows its own MySQL instance and volume
   (switch environments with the dropdown at the top if the project has
   more than one).

### 1.3 OpenEMR service

1. **Create → Empty Service**, name it `openemr` (must match
   `RAILWAY_SERVICE` in `.gitlab-ci.yml`).
2. In the `staging` environment, set the service **Variables** (use Railway
   reference syntax so credentials follow the MySQL service):

   ```
   MYSQL_HOST=${{MySQL.MYSQLHOST}}
   MYSQL_PORT=${{MySQL.MYSQLPORT}}
   MYSQL_ROOT_PASS=${{MySQL.MYSQLPASSWORD}}
   MYSQL_USER=openemr
   MYSQL_PASS=<pick a password>
   OE_USER=admin
   OE_PASS=<pick an admin password>
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

1. **Create → Empty Service**, name it `agent-forge-api`.
2. Configure variables/volumes as that app requires (its Railway build is
   auto-detected by Railpack unless the copilot repo adds its own
   `railway.json`/Dockerfile).
3. If it needs a public URL, generate a domain the same way.

### 1.5 Project token

Project **Settings → Tokens**: create one token scoped to the `staging`
environment (e.g. named `gitlab-ci-staging`).

Copy the value immediately — Railway shows it only once.

## 2. One-time GitLab setup (both repos)

In **each** repo (`agent-forge` and `agent-forge-copilot`):
**Settings → CI/CD → Variables**, add (Masked; Protected only if `main` is a
protected branch):

| Key | Value |
|-----|-------|
| `RAILWAY_TOKEN` | the staging-scoped token |

The same Railway token works for both repos — the token selects the
*environment*, the `--service` flag in each repo's CI selects the *service*.

In `agent-forge` only, also add (plain — these aren't secrets):

| Key | Value |
|-----|-------|
| `STAGING_URL` | the staging environment's public Railway domain for the `openemr` service, e.g. `https://openemr-staging-xxxx.up.railway.app` |

`STAGING_URL` is read by the `verify` stage's post-deploy smoke test
(`.gitlab/ci/verify.yml`), which polls
`${STAGING_URL}/interface/login/login.php?site=default` until it
returns HTTP 200.

`agent-forge-copilot` uses its own `RAILWAY_TOKEN`-equivalent variable
and Railway domain for its `/health`/`/ready` smoke test — see that
repo's `documentation/CI-SETUP.md`.

## 3. CI pipeline for the copilot repo

`agent-forge-copilot` maintains its own `.gitlab-ci.yml` /
`.gitlab/ci/deploy.yml` — see that repo directly rather than copying an
example here, since keeping two independently-maintained copies in sync by
hand is exactly the kind of drift this project has already been bitten by
(the `RAILWAY_SERVICE`/environment mismatches this doc itself went through).
Both repos deploy `railway up --service "$RAILWAY_SERVICE" --ci` against the
same `staging` environment; only the service name and any
service-specific variable reassertion differ.

## 4. Day-to-day workflow

1. Merge/push to `main` → the pipeline auto-deploys to **staging**.
2. `verify:staging` smoke-tests the deployed login page; a red job means the
   deploy went out but isn't actually serving traffic — check deploy logs
   before re-running.

GitLab's **Operate → Environments** page tracks what commit is live in
`staging`.

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
  `RAILWAY_SERVICE` (`openemr` / `agent-forge-api`).
- `Unauthorized` in CI → token missing/wrong scope; the project token must
  be scoped to the `staging` environment.
- Healthcheck timeout on first deploy → check deploy logs; usually MySQL
  variables are missing/wrong in that environment.
- Build OOM/timeout → the webpack + composer build is heavy; retry, or
  build the image in GitLab CI and `railway up` an artifact instead (not
  currently needed).
