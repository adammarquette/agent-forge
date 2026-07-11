# CI/CD Pipeline Setup

This describes the `agent-forge` GitLab CI/CD pipeline structure, modeled on
`agent-forge-copilot`'s pipeline (same five stages, same include layout).
For the Railway/GitLab account setup this depends on, see
`docs/DEPLOYMENT.md`.

## File layout

```
.gitlab-ci.yml           # entry point: stages, workflow rules, includes
.gitlab/ci/
  lint.yml                # psr12, phpstan, rector, codespell
  build.yml                # openemr-ci image (pushed) + production image (validation gate)
  test.yml                 # phpunit-isolated
  deploy.yml                # railway up: staging (auto on push to main)
  verify.yml                # post-deploy smoke test against staging
```

Commit all six files together — `include:` fails the whole pipeline if any
referenced file is missing, so there's no working intermediate state with
only some of them present.

## Stages

1. **lint** — style and static analysis. Runs against the `openemr-ci`
   image `build:ci-image` just built, so phpstan sees the real extension
   set (`ext-ldap`, `ext-soap`, `ext-xsl`, ...) rather than false negatives
   from a host toolchain missing those extensions.
2. **build** — `build:ci-image` builds and locally tags the `openemr-ci`
   Dockerfile target (full dev dependencies) with the commit SHA; lint and
   test reference that tag directly (`pull_policy: never`) rather than
   rebuilding or pulling from a registry — this GitLab instance doesn't
   have a Container Registry configured (`CI_REGISTRY`/`CI_REGISTRY_IMAGE`
   come back empty), and since every job runs through the same single
   self-hosted runner sharing one Docker daemon (Docker-outside-of-Docker,
   see `build.yml`), the image built here is already there locally for
   lint/test to use. `build:production-image` builds the real deploy
   target as a pure validation gate — it's not consumed anywhere
   downstream, it just fails the pipeline immediately if the Dockerfile
   itself is broken (buildkit cache-mount syntax, `VOLUME` directives,
   CRLF-corrupted `COPY`'d scripts — this class of bug has hit this repo
   more than once).
3. **test** — `phpunit-isolated` (no database required). DB-backed suites
   (`unit`/`api`/`e2e`/`services`) need the full docker-compose stack
   (MySQL, OpenLDAP, Selenium, ...) that this pipeline doesn't provision, so
   they're intentionally out of scope here rather than half-wired against
   infrastructure that doesn't exist. Run those locally via `openemr-cmd`
   before merging changes that touch DB-backed code paths.
4. **deploy** — `railway up` against Railway's `staging` environment,
   automatic on every push to `main`. Single-environment model, mirroring
   `agent-forge-copilot`'s `deploy.yml` — an earlier version of this
   pipeline promoted through `development` → `sdet` → `production`, but
   Railway's project only ever had two environments and the working
   deployment has lived in `staging` since 2026-07-10 (`agent-forge#8`).
5. **verify** — a post-deploy smoke test against `staging`. Polls the login
   page until it returns HTTP 200, since a green `railway up` only means
   Railway accepted the source upload — `openemr.sh`'s first-boot
   `auto_configure.php` run can take several minutes before the app
   actually answers requests.

## GitLab merge-check settings to enable

**Settings → Merge requests**:

- **Pipelines must succeed** — blocks merging an MR whose lint/build/test
  jobs failed.
- **All threads must be resolved** — optional, but keeps review discussions
  from being silently merged past.

**Settings → CI/CD → Variables** — see `docs/DEPLOYMENT.md` for the full
list (`RAILWAY_TOKEN_STAGING`, `OE_PASS`/`MYSQL_PASS`, `STAGING_URL`).

## Day-to-day workflow

1. Open an MR. The pipeline runs lint + build + test (deploy/verify are
   gated to `$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH` and don't fire on MR
   pipelines) — feedback before merge, no deploy triggered.
2. Merge to `main` → a new pipeline runs the full lint/build/test suite,
   then auto-deploys to **staging**, then smoke-tests it.

GitLab's **Operate → Environments** page tracks what commit is live in
`staging`.

## Known gaps (not silently dropped, tracked here instead)

- **DB-backed test suites aren't wired into this pipeline.** Running the
  full `unit`/`api`/`e2e`/`services` suites in CI would require standing up
  MySQL/OpenLDAP/Selenium alongside the pipeline job, which is a
  meaningfully bigger lift than this pipeline's scope. Run them locally via
  `openemr-cmd cst` before merging changes that touch DB-backed behavior.
- **Single environment, no pre-production gate.** There is currently no
  separate staging-before-production step — `staging` is both the
  integration target and the closest thing to a production environment
  that exists. If a genuine production environment is stood up later,
  reintroduce a promotion gate (manual `deploy:production` job, `needs` on
  a passing `staging` deploy) rather than deploying straight to it from
  every push to `main`.
