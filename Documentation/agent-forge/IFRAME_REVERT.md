# Reverting the AgentForge Launch to Pure-Iframe Embedding

This documents how to walk back the SameSite bridge-cookie / `window.open()`
approach built for issue #21, once the sidecar side lands a same-origin
deployment topology (in progress in `agent-forge-copilot`) that makes the
whole problem moot.

## Background: why #21 happened

Both AgentForge launch entry points — the per-patient "Launch AgentForge"
button and the "Daily Agenda" tab — originally embedded the sidecar in a
cross-origin `<iframe>` (`dlgopen()`'s modal for the button, OpenEMR's own
iframe-based tab framework for the agenda). The sidecar completes its OAuth
authorization-code exchange by redirecting that iframe **back** to OpenEMR's
own `/oauth2/default/authorize`. Because the sidecar lives on a different
origin (a separate Railway service/domain), that redirect is
cross-site-*initiated*, and OpenEMR's core session cookie
(`SessionConfigurationBuilder::forCore()`, `cookie_samesite: 'Strict'`)
is excluded from it — so OpenEMR can't tell the browser is already logged
in, and the iframe shows a login form instead of the sidecar.

MR !23 (`fix/agentforge-samesite-launch-bridge`, **not yet merged as of this
writing**) worked around this from the OpenEMR side: a narrow, short-lived,
`SameSite=Lax` "bridge cookie" plus escaping both launch flows into real
top-level browser tabs (`window.open()`), since `Lax`'s cross-site exception
only covers top-level navigations, not iframes. Full detail in
`agent-forge#21` and MR !23's description.

## Why we're reversing course instead

If the sidecar is deployed **same-site with OpenEMR** (e.g. behind a shared
reverse proxy / load balancer, same registrable domain, different path or
subdomain), the redirect back to `/oauth2/default/authorize` is no longer
cross-site at all — the existing `SameSite=Strict` core cookie is sent
normally, and none of MR !23's machinery (bridge cookie, `window.open()`
escapes) is needed. This also opens the door to load-balancing the sidecar
behind that same proxy layer, which the bridge-cookie/new-tab approach
didn't provide any path toward.

That work is happening in `agent-forge-copilot`, not this repo.

## Prerequisite gate — do not skip this

**Do not revert or avoid merging MR !23 until the same-origin sidecar
deployment is live and verified.** If the sidecar is still cross-site when
this reverts, issue #21's original bug (login form inside the iframe) comes
straight back. Verify same-site delivery first:

1. Confirm the sidecar's effective launch URL (`AGENTFORGE_LAUNCH_URI` /
   `AgentForgeGlobalConfig`) resolves to the same registrable domain as the
   OpenEMR deployment (ideally same-origin: same scheme + host + port).
2. Click "Launch AgentForge" on staging and confirm the iframe/modal shows
   the sidecar's actual chat UI, not OpenEMR's login form.
3. Only then proceed with the revert below.

## Current state (as of this writing)

MR !23 has **not been merged**. That means `main` is already sitting in the
"just iframes" state for the per-patient button (`dlgopen()`'s modal). The
Daily Agenda entry point (`public/agenda-launch.php`) is *also* still its
original plain redirect on `main` — it was authored as a simple
`header('Location: ...')` inside OpenEMR's iframe-based tab from the very
first commit (`4c7e6bd`, MR !22) and has never had the `window.open()`
escape merged.

So the practical action right now is: **close MR !23 without merging it.**
The rest of this document is for two other cases: (a) understanding exactly
what would need undoing if !23 *had* already been merged, and (b) as a
reference for anyone touching this code later who needs to understand why
the pure-iframe version is the deliberate, current design.

## What MR !23 changed, file by file

If MR !23 is ever merged and later needs reverting, `git revert` of its
merge commit is the mechanical answer. This section explains what that
reverts, for review purposes.

### `interface/modules/custom_modules/oe-module-agentforge/src/Bootstrap.php`

- **!23 state**: `renderLaunchButton()` only computes the pid and points the
  button at `public/patient-launch.php` (a dedicated entry point); no token
  building or cookie-setting happens in the page-render path.
- **Pure-iframe (pre-!23) state**: `renderLaunchButton()` builds the
  `SMARTLaunchToken` and full sidecar launch URL directly, inline, and
  passes that URL straight to `buildLaunchActionButton()`.

### `interface/modules/custom_modules/oe-module-agentforge/src/Launch/AgentForgeLaunchService.php`

- **!23 state**: `buildLaunchActionButton()` takes a `$windowName` param;
  `buildLaunchHeaderScript()` calls `window.open(launchUrl, windowName)` and
  detects popup-blocking (`win.closed`) as the failure mode.
- **Pure-iframe (pre-!23) state**: `buildLaunchActionButton()` takes only
  the launch URL; `buildLaunchHeaderScript()` calls
  `dlgopen(launchUrl, "agentforge-launch", "modal-full", ..., {allowExternal: true})`
  and detects load failure via polling for the iframe element after an 8s
  timeout.

### `interface/modules/custom_modules/oe-module-agentforge/public/patient-launch.php`

- **!23 state**: new file. Built specifically because `setcookie()` silently
  fails once headers are sent, which is already true by the time a
  `PageHeadingRenderEvent` listener runs deep into page rendering - so the
  bridge cookie had to be set from a fresh, dedicated request instead.
- **Pure-iframe (pre-!23) state**: doesn't exist. Delete it.

### `interface/modules/custom_modules/oe-module-agentforge/public/agenda-launch.php`

- **!23 state**: renders a small HTML page with an inline `<script>` that
  calls `window.open(launchUrl, "agentforge-agenda")` to escape OpenEMR's
  iframe-based tab, plus a popup-blocked warning.
- **Pure-iframe (pre-!23) state** (already what's on `main`): a plain
  `header('Location: ' . $launchUrl); exit;` - the OpenEMR tab's iframe
  navigates directly to the sidecar.

### `src/Common/Session/SessionUtil.php`

- **!23 state**: adds `EHR_LAUNCH_BRIDGE_COOKIE_NAME`, `EHR_LAUNCH_BRIDGE_TTL`,
  and `setEhrLaunchBridgeCookie()` / `getEhrLaunchBridgeCookie()` /
  `clearEhrLaunchBridgeCookie()`.
- **Pure-iframe (pre-!23) state**: none of the above exist. Remove them
  entirely - this is core session code, not scoped to the module, so leaving
  dead/unused security-relevant methods around is worse than deleting them.

### `src/RestControllers/AuthorizationController.php`

- **!23 state**: `getLoggedInCoreUserUuid()` falls back to
  `SessionUtil::getEhrLaunchBridgeCookie()` when the primary core session
  cookie is empty, and clears it after one successful use.
- **Pure-iframe (pre-!23) state**: only reads the primary core session
  cookie; returns `null` (→ login prompt) if it's absent. This is exactly
  correct once the sidecar is same-site, since the primary cookie will
  simply arrive normally.

### `library/auth.inc.php`

- **!23 state**: `authCloseSession()` calls
  `SessionUtil::clearEhrLaunchBridgeCookie()` before destroying the core
  session.
- **Pure-iframe (pre-!23) state**: no such call. Remove it (it becomes a
  no-op referencing a deleted method otherwise).

### Tests

- Delete `tests/Tests/Unit/Common/Session/SessionUtilEhrLaunchBridgeCookieTest.php`
  entirely (it only tests the removed methods).
- Revert `tests/Tests/Unit/Modules/AgentForge/AgentForgeLaunchServiceTest.php`
  to assert `dlgopen(` / `allowExternal: true` / the 8-second timeout
  instead of `window.open(` / `win.closed` / popup-blocked detection.

## After reverting

1. Re-run the full AgentForge unit test suite, PHPStan (full codebase), and
   PSR-12 - same verification bar as every other change in this module.
2. Live-verify via Selenium (or by hand) that the per-patient button opens
   the sidecar in the modal iframe and the Daily Agenda tab loads it inline,
   with no login-form regression - i.e., re-confirm the prerequisite gate
   above actually holds under real use, not just in theory.
3. Close out `agent-forge#21` referencing this doc and the same-origin fix
   in `agent-forge-copilot`, rather than the bridge-cookie approach.
