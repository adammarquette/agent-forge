# MintSessionCookie

Mints a fresh sidecar session cookie — the `.AspNetCore.Session=<value>` pair the Bruno collection's
session-gated requests send — by driving a **real SMART login** in a headless browser. It does
by script what `tests/bruno-collection/README.md` "Getting a session cookie" step 4 has a human do with
DevTools, and nothing more.

Not part of the shipped product: it lives under `tools/`, which no image copies (`Dockerfile` builds
`src/` only), and no CI job runs it. `dotnet build` and `dotnet format` cover it.

## Why it exists, and why it is not a login bypass

Staging redeploys on every `develop` merge and a redeploy empties the session store, so a
cookie minted by hand stops working almost as soon as it is minted. The fix that was ruled out is the
obvious one — a non-interactive login inside the deployed sidecar — because that is a new
authentication-bypass surface on an internet-facing service (AC4; the collection README's "Is
the cookie requirement itself a defect?").

This tool adds no endpoint and changes nothing in the sidecar or OpenEMR. It opens the sidecar's own
`/agenda/launch` (or `/launch`), types the credentials into OpenEMR's own login page, picks the patient if
asked, clicks OpenEMR's own **Authorize**, and lets OpenEMR redirect to the sidecar's real `/callback` with a
real, single-use authorization code. The session it ends up holding is exactly the one a person's browser
would — same client, same scopes, same one-hour token behind it.

## Running it

```bash
export MintSession__Username=dr_cardio        # or leave both unset and be prompted
read -rs MintSession__Password && export MintSession__Password   # typed, not echoed, not in history

SESSION=$(dotnet run --project tools/MintSessionCookie -- \
  --base-url https://reverse-proxy-staging-5c25.up.railway.app/agentforge)

cd tests/bruno-collection
npx @usebruno/cli run --env staging --disable-cookies --env-var "sessionCookie=$SESSION"
```

PowerShell: `$env:MintSession__Username = 'dr_cardio'`, then leave `MintSession__Password` unset and let
the tool prompt for it — the prompt reads keys without echoing them.

**Credentials.** From `MintSession__Username` / `MintSession__Password`, or prompted for on the terminal
when either is unset (the password without echo). **Never from arguments** — `--password`, `--user` and
their relatives are refused with exit 2 before anything runs, because an argument lands in shell history
and in any process listing. Nothing prints either value, and the tool never retries a rejected login
(a second attempt only moves the account towards lockout). `MintSession__Password` is removed from the
tool's own environment before the browser starts, so neither the Playwright driver nor the browser it
launches inherits it; the variable in your shell is untouched.

**Where the credentials may go.** Only into a login page served from the expected OpenEMR origin, and
only when the login form submits back to that same origin — by default the sidecar's own origin, which
is the one-front-door topology every deployed stack uses (`DEPLOYMENT.md` §2). Both are checked before
the username is typed, again before the password is typed and again before the form is submitted, so a
page that navigates away or repoints its form mid-login is caught before the next step. A
failed check stops the run with exit 1 and nothing further typed or sent; pass `--openemr-base-url` if
your OpenEMR genuinely sits on another origin (the bare-sidecar recipe in the collection README). Plain
`http` is refused unless the host is loopback.

**Where the cookie goes.** Standard output, and only the pair — every message is on standard error — so
`$(...)` captures exactly it and the value never enters history as text. `--out <file>` writes it to that
file instead, readable and writable by you alone **whether the file is new or is being overwritten**
: mode `0600` on Linux and macOS; on Windows, which has no mode bits, the equivalent — a
protected ACL that inherits nothing from the directory and whose only entry is the current user. A
`.bru` path is refused: environments are checked in, and the collection README forbids a cookie in one.

**Which launch.** `--launch agenda` (the default) is the one that covers every session-gated request in a
full collection run: `Agenda/Select Patient` turns the roster session into a patient context for
`Evidence/` and `Patient/` (collection README step 5). `--launch patient --patient-id <uuid>` is the
single-patient launch the authenticated run used; it leaves `Agenda/` at 401, and it is the one
launch the tool re-checks, with a `GET /patient` that sends the cookie as a plain header exactly as
Bruno will. An agenda session is not re-checked, because `GET /agenda` runs an agent turn per roster
patient — real model spend.

**How long it lasts.** Until the OpenEMR access token behind it expires — about an hour
(`INTERFACES.md` A.3) — or until the sidecar restarts or redeploys, whichever is first. Mint one
per run.

| Option | Default | |
|---|---|---|
| `--base-url <url>` / `MintSession__BaseUrl` | — (required) | Sidecar base including its path base, e.g. `https://<front door>/agentforge`, `http://localhost:8080/agentforge` for local compose |
| `--launch agenda\|patient` | `agenda` | Launch entry point |
| `--patient-id <uuid>` | — | Patient to pick on OpenEMR's patient-select page (`--launch patient` only). OpenEMR lists a limited set; a patient it does not list stops the run |
| `--openemr-base-url <url>` | sidecar's origin | The only origin credentials may be typed into |
| `--out <file>` | stdout | Write the pair to this file instead |
| `--cookie-name <name>` | `.AspNetCore.Session` | Session cookie name |
| `--browser-channel chrome\|msedge` | Playwright's Chromium | Use an installed browser; no download needed |
| `--headed` | off | Show the browser window |
| `--timeout-seconds <n>` | `60` | Per-step timeout |
| `--no-verify` | off | Skip the `GET /patient` check after a patient launch |

Exit codes: `0` minted, `1` the login did not produce a session, or the `GET /patient` re-check could
not reach the sidecar (the message says where it stopped — scheme, host and path only, never a query
string), `2` usage or configuration error, including an `--out` file that cannot be written (the pair is
then not printed either — you asked for it to stay off stdout). None of them prints a stack trace.

**A browser.** Either an installed Chrome or Edge via `--browser-channel`, or Playwright's own Chromium,
installed once after the first build: `pwsh tools/MintSessionCookie/bin/Debug/net10.0/playwright.ps1
install chromium`. Prefer `chrome`: on this workstation `msedge` completed every login just as fast but
stalled on close, so the tool now gives a graceful close 10 s and then stops the browser — a run still
succeeds, about 40 s slower.

**What it does not handle.** An account with MFA enabled, TOTP or U2F (it stops and says so), a patient OpenEMR's
picker does not list, and anything but OpenEMR's stock OAuth2 pages — it keys on the fork's own markup
(`input[name=password]`, `button[name=user_role][value=api]`, `button[data-patient-id]`,
`button[name=proceed]`), the same selectors `tools/LoadTestChat`'s session bootstrap and the integration
suite's `PlaywrightLoginAutomation` already depend on.

## Self-test (no real credentials, no real host)

```bash
dotnet run --project tools/MintSessionCookie.SelfTest -- --browser-channel chrome   # or msedge, or none; --verbose for timings
dotnet run --project tools/MintSessionCookie.SelfTest -- --no-browser               # only the checks that need no browser
```

First, with no browser, it calls the guards directly: the plain-`http` refusal (a page on the named
origin but off loopback), a login form that submits off-origin, to a non-http URL or from no form at all,
the masking of the password out of a Playwright error, `--out` over an existing file with wider access,
and that the password variable is gone once the Playwright driver has started — on Linux it reads the
driver's own environment from `/proc`, which Windows offers no way to do.

Then it starts a fake front door on loopback — the sidecar's `/launch`, `/agenda/launch`, both callbacks and
`/patient`, plus a fake OpenEMR login, patient picker and consent page copying the fork's markup and its
script-driven submits — and runs the real tool against it: both launches end to end, a rejected password
(one attempt, OpenEMR's own message), a login page on an unexpected origin (nothing typed), the same page
allowed once named, a login form that submits to another origin, one repointed off-origin while the
username is typed (the password is never typed) and one repointed while the password is typed (never
submitted), a refusing callback, the TOTP and the U2F MFA pages, a `GET /patient` that drops the
connection, a missing and an unlisted patient, and — out of process, against the built executable — that
stdout is exactly the pair, `--out` writes only the file, an `--out` that cannot be written exits 2 with
no stack trace, neither stream carries the password or the cookie value where it should not, and a
credential argument or a missing credential stops the run before any launch. The username, password,
patients and cookies are made up per run. The browser scenarios need a browser, which is why no CI job
runs it.
