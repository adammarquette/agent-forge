# CI/CD Pipeline Setup

This describes the `agent-forge` CI/CD pipeline, which runs on **GitHub
Actions** (`adammarquette/agent-forge`). For the Railway account setup the
deploy depends on, see [`docs/DEPLOYMENT.md`](DEPLOYMENT.md).

> **The GitLab pipeline is retired.** Until 2026-07-17 this repo deployed from
> GitLab CI on `labs.gauntletai.com` (`.gitlab-ci.yml` + `.gitlab/ci/`).
> Development moved to GitHub, those files are deleted, and their deploy and
> verify stages are ported into the workflow below. [What the GitLab pipeline
> did that this one does not yet](#what-the-retired-gitlab-pipeline-did) is
> recorded at the bottom, so re-adding it does not mean rediscovering it.

## The pipeline

One workflow, `.github/workflows/publish-openemr-ghcr.yml`, five jobs:

```
php-syntax ───┐
              ├──> publish ──> deploy-staging ──> verify-staging
build-verify ─┘   (main only)   (main + opt-in)
```

| Job | Runs on | What it does |
|-----|---------|--------------|
| `php-syntax` | PR + main | `php -l` over every tracked `.php` file, one process per file. No vendor tree needed. |
| `build-verify` | PR + main | Builds `docker/railway/Dockerfile` to the local daemon and asserts the image really is **this fork** — the AgentForge module files are present, and `SessionUtil.php` carries the EHR launch-bridge patch. A stock-OpenEMR build fails here rather than later, at SMART launch. |
| `publish` | main only | Rebuilds from the buildx cache `build-verify` populated (a cache hit, not a second full build) and pushes `:latest`, `:main` and `:sha-<12>` to GHCR. |
| `deploy-staging` | main, opt-in | `railway up` against Railway's `staging` environment. |
| `verify-staging` | main, opt-in | Polls the deployed login page until it returns HTTP 200. |

Everything is in one workflow on purpose. This fork inherited 53 workflows from
upstream OpenEMR, every one of them filtered to `master`/`rel-*`, so none has
ever run on this fork's `main` — there was no green pipeline to chain from with
`workflow_run`. Expressing the gate as `needs:` keeps it in one place.

### Why the deploy uploads source instead of deploying the published image

`deploy-staging` runs `railway up`, which uploads this commit's source for
Railway to build with `docker/railway/Dockerfile` (selected by the service's
`dockerfilePath` setting, not `railway.json` — see `DEPLOYMENT.md` §1.3) — the
same Dockerfile, from the same commit, that produced the image `publish` just
pushed. Deploying that *exact* image would be better, and is not currently
possible with a deploy-scoped credential:

- Pointing the service at `ghcr.io/…:sha-<sha>` means rewriting the service's
  image source on every run (`serviceInstanceUpdate`), an account-scoped API
  call that a Railway **project token** cannot make.
- `railway redeploy` needs only a project token, but reuses the deployment's
  existing build rather than re-resolving a moved tag — so it can silently
  redeploy the *previous* image and report success.

The tradeoff taken is a second build (Railway's, cached) in exchange for a
deploy that cannot go stale without saying so. If the `openemr` service is ever
switched to an image source and an account-scoped token is available, revisit.

## One-time GitHub setup

**Settings → Secrets and variables → Actions.**

Secrets:

| Secret | Value |
|--------|-------|
| `RAILWAY_TOKEN_STAGING` | Railway project token scoped to the `staging` environment |
| `OE_PASS` | OpenEMR admin password for staging |
| `MYSQL_PASS` | OpenEMR's MySQL user password for staging |

Variables (not secrets):

| Variable | Value |
|----------|-------|
| `RAILWAY_STAGING_ENABLED` | `true` turns the deploy on. Anything else, or unset, skips `deploy-staging` and `verify-staging`. |
| `STAGING_URL` | The `openemr` service's public Railway domain, e.g. `https://openemr-staging-a41b.up.railway.app` |

The deploy is gated on a **variable** rather than on the presence of the secret
because a job-level `if:` cannot read the `secrets` context. Without the gate, a
repo with no Railway credentials would fail every run on `main`.

`OE_PASS`/`MYSQL_PASS` are reasserted onto the Railway service immediately
before each deploy, so a value edited only in the Railway dashboard cannot
drift away from what CI believes is set. A missing secret **warns and leaves
Railway's value alone** rather than overwriting it with an empty string.

## Branch protection

**Settings → Branches → `main`**: require `php syntax` and
`build image + verify it is the fork` to pass before merging. Both run on pull
requests, so the publish gate is exercised before it ever guards a publish.

## Day-to-day workflow

1. Open a PR → `php-syntax` and `build-verify` run. No publish, no deploy.
2. Merge to `main` → the same two run, then `publish` pushes to GHCR, then
   (if enabled) `deploy-staging` and `verify-staging`.

The repo's **Environments** tab tracks what commit is live in `staging` — the
replacement for GitLab's Operate → Environments view.

## Known gaps

- **Style and static analysis are not gates yet.** `php-syntax` is the only PHP
  check. phpcs, phpstan, rector and codespell all ran in GitLab; see below for
  what re-adding them takes. Run them locally with `openemr-cmd cq` before
  pushing.
- **DB-backed test suites are not wired in.** `unit`/`api`/`e2e`/`services`
  need the full docker-compose stack (MySQL, OpenLDAP, Selenium). Run them
  locally with `openemr-cmd cst`.
- **The isolated PHPUnit suite is not wired in either.** It needs only a vendor
  tree, no database — the most tractable gate to add next.
- **Single environment, no pre-production gate.** `staging` is both the
  integration target and the closest thing to production that exists. If a real
  production environment is stood up, add a promotion gate (a manual job gated
  on a passing staging deploy) rather than deploying straight to it.

## What the retired GitLab pipeline did

Kept because every item below was paid for in debugging time. Anyone re-adding
these gates on GitHub Actions needs them.

**`phpstan` must not run through Composer.** `composer phpstan` wraps execution
in a subprocess with a 300s default process timeout, which killed phpstan
mid-run at 79% — level 10 on this codebase legitimately takes longer than that.
Call the binary directly:
`vendor/bin/phpstan analyze --memory-limit=4G --configuration=phpstan.neon.dist`.
Same for rector: `php -d memory_limit=4g ./vendor/bin/rector process --dry-run`.

**Lint and test jobs need two things copied out of the built image**, because a
fresh checkout has neither (both are gitignored):

- `vendor/` — populated by `composer install` at image build time.
- `interface/modules/custom_modules/oe-module-claimrev-connect` — a real
  Composer dependency (`claimrevolution/oe-module-claimrev-connect`) that
  `openemr/oe-module-installer-plugin` *relocates* out of `vendor/` during
  install. Without it, anything referencing
  `OpenEMR\Modules\ClaimRevConnector\*` (e.g. `oe-module-dorn`) looks like a
  missing class to phpstan, even though the dependency resolves fine.

**`codespell` is not an Alpine package** on the image's Alpine version (not even
in community). It is a Python tool: `apk add py3-pip`, then
`pip3 install --break-system-packages codespell`. `composer codespell` silently
no-ops — prints a warning and exits 0 — when the binary is absent, so installing
it explicitly is what makes the check actually check anything.

**The isolated PHPUnit suite needs a raised memory limit and a zero-tests
guard.** `composer phpunit-isolated` sets no memory limit and reliably exhausts
the default 128M on PHPStan's `RuleTestCase`-based isolated tests; invoke
`php -d memory_limit=1G vendor/bin/phpunit -c phpunit-isolated.xml` directly.
PHPUnit's exit code does not distinguish "0 tests ran" from "all passed", so the
GitLab job asserted `grep -qE "Tests: [1-9][0-9]*,"` against the output — a
config or autoload regression that collects zero tests otherwise passes green.
Use `set -o pipefail` when piping to `tee`, or a real failure is masked by
`tee`'s always-zero exit status.

**Image cleanup was a pipeline job.** The GitLab runner was self-hosted with one
shared Docker daemon, so SHA-tagged build images accumulated unboundedly; a
`cleanup` stage ran `docker image prune -af --filter until=48h` after every
pipeline. GitHub-hosted runners are ephemeral, so this is no longer needed.
