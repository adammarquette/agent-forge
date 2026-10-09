# MintQaIdentityToken

One-time operational script that mints a real, patient-scoped OpenEMR access token for the QA integration
suite. Not part of the shipped product and not run by CI - `dotnet build` and
`dotnet format` cover it, but the `unit-tests` and `integration-tests` CI jobs filter by project filename
and never invoke it.

## Why this exists

`Mcp.CrossIdentityAuthorizationTests` proves entitlement is per-identity, not shared - it needs **two**
real, distinct patient-scoped tokens, each from a separate SMART login:

- `OpenEmrQa__CrossIdentityTestAccessTokenA` - identity A, scoped to `OpenEmrQa__TestPatientId` (Ada
  Testpatient). **Not** the same as `OpenEmrQa__TestAccessToken`: once `SystemClientId` is configured
  (the client_credentials fix), that one is a system-role `client_credentials` grant that can read every patient,
  which would make an isolation check against it meaningless.
- `OpenEmrQa__SecondTestAccessToken` / `OpenEmrQa__SecondTestPatientId` - identity B, scoped to a second,
  distinct patient.

`client_credentials` can't stand in for either - it isn't tied to any patient. This script performs a
genuine `authorization_code` + PKCE **standalone launch** (`launch/patient` scope, plus the FHIR resource
scopes `Mcp.GetPatientSummaryAsync` needs: `Patient`, `Condition`, `MedicationRequest`,
`AllergyIntolerance`, all PascalCase - confirmed against the live server's
`.well-known/openid-configuration`, which rejects the lowercase form as `invalid_scope`), the same way the
original `TestAccessToken` was obtained. It prints back whatever patient context OpenEMR's own
login/consent step resolves to - it does not assume or force which patient gets selected.

Run it **twice**: once logging in as/selecting Ada Testpatient for identity A, once for a second, distinct
patient for identity B. SeedDemoPatients seeded 20 synthetic demo patients into QA OpenEMR - use any one of those
for identity B (the seeding run prints each one's pid and uuid). No need for a dedicated QA patient:
the test only needs two patients whose data don't leak into each other.

**Durability:** the scope also includes `offline_access`, so if this OpenEMR deployment grants
a refresh token, use *that* instead of the raw access token - see Output below. `OpenEmrQaFixture` will
exchange it for a fresh access token every test run, the same durable pattern already used for
`OpenEmrQa__TestAccessToken` via `client_credentials`, except this preserves patient scoping
instead of a system-role grant.

## Running it

```bash
dotnet run --project tools/MintQaIdentityToken
```

Needs one interactive step: the script registers (or reuses, via `MintToken__ClientId`/
`MintToken__ClientSecret`) a public OAuth client, prints its client_id, and pauses for you to enable it
(Admin -> System -> API Clients) - freshly-registered clients land disabled. It then prints an authorize
URL; open it, log in as (or select) the target patient identity, approve, and paste back the resulting
address-bar URL (the redirect target doesn't resolve - that's expected, the `code` is still in the URL).

Before printing the values to store in CI, it runs two checks against the real server using the
freshly minted token:
- **Self-check** - can the token read the patient it claims to be scoped to?
- **Isolation check** - if `OpenEmrQa__TestPatientId` is set in the environment and differs from the
  minted token's own patient, is the token genuinely blocked from reading it? (Skipped, with a note,
  otherwise - e.g. this is naturally skipped on the run where you mint identity A's own token.)

The access token this produces lives only in the process's memory - never logged, never written to disk.

### Environment variables

| Variable | Purpose |
|---|---|
| `MintToken__BaseUrl` | OpenEMR base URL (defaults to the local compose front door, `http://localhost:8080`) |
| `MintToken__Site` | OpenEMR multi-site segment (defaults to `default`) |
| `MintToken__ClientId` / `MintToken__ClientSecret` | Reuse an already-registered, already-enabled client instead of registering a new one |
| `OpenEmrQa__TestPatientId` | Identity A's patient id, read only to run the isolation check above |

## Output

Prints the granted scope, token lifetime, resolved `patient` claim, and whether a refresh token came back,
then which GitHub Actions secrets to store it in (`gh secret set <NAME>`, or Settings -> Secrets and variables -> Actions):

```
# If a refresh token was granted (NOT durable - see the note below):
OpenEmrQa__CrossIdentityClientId = <client id - SAME value both times; reuse via MintToken__ClientId>
OpenEmrQa__CrossIdentityClientSecret = <client secret, only if non-empty>
OpenEmrQa__CrossIdentityRefreshTokenA = <refresh token>   # identity A run
# or, identity B run:
OpenEmrQa__CrossIdentityRefreshTokenB = <refresh token>
OpenEmrQa__SecondTestPatientId        = <patient uuid>

# If no refresh token was granted (this deployment doesn't support offline_access - falls back to a
# static, ~1hr-lived access token, as without offline_access):
OpenEmrQa__CrossIdentityTestAccessTokenA = <token>   # identity A run
# or, identity B run:
OpenEmrQa__SecondTestAccessToken = <token>
OpenEmrQa__SecondTestPatientId   = <patient uuid>
```

The refresh path needs **both** logins to register against the *same* OAuth client (the refresh grant is
redeemed by the client that obtained it) - set `MintToken__ClientId`/`MintToken__ClientSecret` to the
first run's printed `CrossIdentityClientId`/`ClientSecret` before running the second.

> **A stored refresh token survives one run** (`DEPLOYMENT.md` §5 *Surviving a
> reseed*). OpenEMR rotates refresh tokens on use: at the pinned fork, `league/oauth2-server`'s
> `AuthorizationServer` revokes the redeemed token and issues a new one. So the `CrossIdentityRefreshToken*`
> lines above are for a local, one-off run only. A post-deploy integration run should store none and
> mint the
> patient-scoped tokens per run through the Playwright login instead.
