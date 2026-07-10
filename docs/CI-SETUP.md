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
  deploy.yml                # railway up: development (auto) -> sdet -> production (manual)
  verify.yml                # post-deploy smoke test against development
```

Commit all six files together — `include:` fails the whole pipeline if any
referenced file is missing, so there's no working intermediate state with
only some of them present.

## Stages

1. **lint** — style and static analysis. Runs against the `openemr-ci`
   image `build:ci-image` just pushed, so phpstan sees the real extension
   set (`ext-ldap`, `ext-soap`, `ext-xsl`, ...) rather than false negatives
   from a host toolchain missing those extensions.
2. **build** — `build:ci-image` builds and pushes the `openemr-ci` Dockerfile
   target (full dev dependencies) to this project's Container Registry,
   tagged with the commit SHA; lint and test pull it rather than rebuilding.
   `build:production-image` builds the real deploy target as a pure
   validation gate — it's not consumed anywhere downstream, it just fails
   the pipeline immediately if the Dockerfile itself is broken (buildkit
   cache-mount syntax, `VOLUME` directives, CRLF-corrupted `COPY`'d
   scripts — this class of bug has hit this repo more than once).
3. **test** — `phpunit-isolated` (no database required). DB-backed suites
   (`unit`/`api`/`e2e`/`services`) need the full docker-compose stack
   (MySQL, OpenLDAP, Selenium, ...) that this pipeline doesn't provision, so
   they're intentionally out of scope here rather than half-wired against
   infrastructure that doesn't exist. Run those locally via `openemr-cmd`
   before merging changes that touch DB-backed code paths.
4. **deploy** — `railway up` against the three Railway environments in one
   project, promoted in order: `development` (automatic on every push to
   `main`) → `sdet` (manual) → `production` (manual, and GitLab refuses to
   run it until `sdet` has deployed in that same pipeline).
5. **verify** — a post-deploy smoke test against `development` only
   (`sdet`/`production` don't have a URL stable enough to smoke-test yet).
   Polls the login page until it returns HTTP 200, since a green `railway
   up` only means Railway accepted the source upload — `openemr.sh`'s
   first-boot `auto_configure.php` run can take several minutes before the
   app actually answers requests.

## GitLab merge-check settings to enable

**Settings → Merge requests**:

- **Pipelines must succeed** — blocks merging an MR whose lint/build/test
  jobs failed.
- **All threads must be resolved** — optional, but keeps review discussions
  from being silently merged past.

**Settings → CI/CD → Variables** — see `docs/DEPLOYMENT.md` for the full
list (`RAILWAY_TOKEN_*`, `OE_PASS`/`MYSQL_PASS`, `DEVELOPMENT_URL`).

## Day-to-day workflow

1. Open an MR. The pipeline runs lint + build + test (deploy/verify are
   gated to `$CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH` and don't fire on MR
   pipelines) — feedback before merge, no deploy triggered.
2. Merge to `main` → a new pipeline runs the full lint/build/test suite,
   then auto-deploys to **development**, then smoke-tests it.
3. When development looks good, open the pipeline in GitLab and press ▶ on
   `deploy:sdet`. Run manual/exploratory tests against the sdet URL.
4. When sdet passes, press ▶ on `deploy:production`.

GitLab's **Operate → Environments** page tracks what commit is live in each
environment.

## Known gaps (not silently dropped, tracked here instead)

- **DB-backed test suites aren't wired into this pipeline.** Running the
  full `unit`/`api`/`e2e`/`services` suites in CI would require standing up
  MySQL/OpenLDAP/Selenium alongside the pipeline job, which is a
  meaningfully bigger lift than this pipeline's scope. Run them locally via
  `openemr-cmd cst` before merging changes that touch DB-backed behavior.
- **`sdet` and `production` aren't smoke-tested.** Their Railway domains
  have changed unexpectedly during this project's setup, so there's no URL
  yet stable enough to hardcode or configure a variable for. Add
  `verify:sdet` / `verify:production` jobs (same pattern as
  `verify:development`) once those domains are confirmed durable.
