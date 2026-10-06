# reverse-proxy

Same-origin front door for the deferred reverse-proxy topology (,
tracked in this repo as `#62`). A stock `nginx:1.30-alpine` container - no custom
application code, just two start-up hooks in the image's own `docker-entrypoint.d/`
(`10-resolver.envsh`, `15-assert-sidecar-port.sh`) - routes:

- `/` -> OpenEMR
- `/agentforge/hubs/chat` -> the sidecar's SignalR hub (websocket upgrade)
- `/agentforge/documents/` -> `404`, always — the ingestion endpoint authenticates by trusted
  private-network origin, not a token, and must be unreachable from outside it (W2-D17); blocked with a
  **case-insensitive** regex (`~* ^/agentforge/documents/`) for the same reason as `/agentforge/metrics`
  below
- `/agentforge/metrics` -> `404`, always — the sidecar's unauthenticated Prometheus exposition
  endpoint; blocked with a **case-insensitive** regex (`~* ^/agentforge/metrics(/|$)`) because the sidecar
  routes case-insensitively. Prometheus itself never comes through here, it scrapes the sidecar directly at
  Kestrel over the private network — `reverse-proxy:${SIDECAR_PORT}` under compose, `agent-forge-api:8080` on
  Railway
- any path segment starting with a dot (`/.gitmodules`, `/.git/*`, `/.env`, `/interface/.htaccess`) -> `404`,
  always, whichever upstream holds it. **`.well-known` is the one exception**, because SMART and
  OIDC discovery live under it (`/apis/{site}/fhir/.well-known/smart-configuration`, which the sidecar's
  readiness probe reads, and `/oauth2/{site}/.well-known/openid-configuration`); `/.well-known-anything` is
  still denied. It is the first regex location, `~ /\.(?!well-known(/|$))`, so it outranks every other
  block. It matches the decoded path, so `/%2egitmodules` is denied too
- `/agentforge/` -> everything else the sidecar serves. Four of its paths are **rate-limited per client**
  and answer `429` past the limit: `/agentforge/hubs/chat*` (60/min, burst 30),
  `/agentforge/evidence/*` (20/min, burst 10), the agenda roster `/agentforge/agenda` (10/min, burst 5) and
  `/agentforge/ready` (30/min, burst 10). Everything else, `/` included, is unlimited. The limits live in
  one block at the top of `nginx.conf.template`. See *Configuration* for how a client is identified.
- `/grafana/api/live/ws` -> Grafana Live's websocket (upgrade), **where a Grafana exists**
- `/grafana` -> Grafana, **where a Grafana exists** — `404` where one does not, which is production
  and every compose stack (and *Configuration* below)

This removes the cross-site condition behind a separate change (OpenEMR's session
cookie not attaching after the launch iframe/tab traverses a third-party origin),
so the session cookie config on both sides can stay at their normal, unweakened
defaults (`SameSite=Lax`/`Strict`) instead of `SameSite=None`.

## Configuration

`OPENEMR_UPSTREAM` / `SIDECAR_UPSTREAM` (see `Dockerfile`) are the upstream host:port pairs the proxy
routes to. In the repo-root `docker-compose.yml` OpenEMR is the compose service name, `openemr:80`;
**the sidecar is `127.0.0.1:8081`**, because it runs in *this container's* network namespace
(`network_mode: "service:reverse-proxy"`) rather than beside it, so it has no service name to route to.
In any other deployment — Railway, say — both are whatever that stack's private service DNS resolves to.

That namespace share is what makes the SMART launch work locally: `OpenEmr__BaseUrl` is both the URL the
browser follows and the base address of the sidecar's own token/FHIR calls, plain HTTP means it has to be a
loopback origin for the browser's sake, and loopback in a container of its own is that container. Sharing
this one makes `localhost:${PORT}` mean nginx on both sides. See
[`DEPLOYMENT.md`](../DEPLOYMENT.md) §2. Two consequences live here: this
container now listens on `${DEMO_PORT}` rather than a fixed 8080 (the number has to be the same inside and
out), and its lifetime governs the sidecar's — recreate it and the sidecar's network goes with it.

**`15-assert-sidecar-port.sh` refuses to start when `SIDECAR_UPSTREAM` is a loopback address naming the port
`PORT` listens on.** Sharing a namespace means sharing a port space, so `DEMO_PORT=8081` against the default
`SIDECAR_PORT=8081` gives nginx and Kestrel the same number: nginx starts first and wins the bind, Kestrel
crash-loops on *address already in use*, and `/agentforge/*` `502`s in a way that is **indistinguishable from
a forgotten `--profile copilot`**. `docker compose config` validates it, so nothing upstream catches it. The
hook exits non-zero with both knob names instead — and only for a **loopback** upstream, so a stack where the
sidecar is a separate container with its own port space (Railway) is untouched by it. A separate change

**`GRAFANA_UPSTREAM` is the third upstream, and it is EMPTY BY DEFAULT — a supported state, not a missing
value.** Grafana is declared only where `.railway/railway.ts` has both `OBSERVABILITY_IMAGES` pins filled,
which today is **both deployed environments** — staging, production since its separate changes
promotion (2026-09-24, carried to `develop`'s source) — but not `docker-compose.yml`, which has no
`grafana` service. Empty, both
`/grafana` locations answer **`404`** rather than proxying to a name that resolves to nothing, so the route is
*absent* where Grafana is absent instead of `502`-ing there. Two details make that work and are easy to undo
by accident:

- **The placeholder is quoted in the template.** Rendered from an empty value, `set $grafana_upstream ;` is a
  config-time syntax error — nginx would not start *at all*, on the environments that have no Grafana —
  while `set $grafana_upstream "";` parses and lets the request-time test answer `404`. `nginx-config-lint`
  renders the template **twice**, empty and set, precisely so the shape no stack exercises is still gated.
- **The `Dockerfile` defines it rather than leaving it unset**, because the stock entrypoint's `envsubst`
  substitutes only variables that exist in the environment; unset, `${GRAFANA_UPSTREAM}` would survive into
  the rendered config as literal text and nginx would refuse to start on an unknown variable.

**Grafana must also be told it lives under `/grafana`** — `GF_SERVER_ROOT_URL` + `GF_SERVER_SERVE_FROM_SUB_PATH`,
set on the `grafana` service in `.railway/railway.ts`. Without both it still believes it is mounted at `/`, and
every redirect and asset URL it emits drops the prefix; the symptom looks like a proxy fault and is not one.
`proxy_pass` therefore carries **no trailing URI**, the same prefix-preservation rule as `/agentforge/`.

**Under `docker-compose.yml` nothing sets `GRAFANA_UPSTREAM`, deliberately**, so `/grafana` is a `404` on the
local front door and the observability overlay's Grafana stays where it has always been —
`http://localhost:3000`, published on loopback, served from `/` rather than a sub-path
([`../observability/README.md`](../observability/README.md)). Wiring the local front door to it would mean
turning `serve_from_sub_path` on there too, which would *break* that direct address for no local gain.

**`REAL_IP_TRUSTED_CIDR`** (default `100.64.0.0/10`) sets the only peers whose `X-Real-IP` header nginx
believes, and so which client a rate limit counts against. On Railway every connection arrives
from the edge, inside that range, and the edge sets `X-Real-IP` to the real client. Any other peer is
counted by its own address, whatever header it sends, so the header cannot be spoofed to get a fresh
bucket. Under compose the peer is the Docker bridge, outside the range, so local traffic shares one
bucket. It is a single CIDR, because the template renders it into a single `set_real_ip_from`. Sizing and
the reasons behind it are in
[`DEPLOYMENT.md`](../DEPLOYMENT.md) §3 *Edge rate limits and the per-session LLM budget*.

`DNS_RESOLVER` is the resolver nginx uses to look upstreams up **at request time** (rather than once
at startup), which is what lets the proxy boot before its upstreams exist and follow container
restarts without a reload. Under compose it is Docker's embedded DNS, `127.0.0.11`.

`PORT` is read at container start, so the listen port is set by the environment rather than baked
into the config — same convention as the main sidecar's own `Dockerfile`.

## Status

**Live** — this is the `reverse-proxy` service in the repo-root `docker-compose.yml`, and the **only
container in that file that publishes a port** (`${DEMO_PORT:-8080}`; running observability publishes three
more, and the root overlay puts them on this network, bound to `127.0.0.1` —
`DEPLOYMENT.md` §3). The full launch round-trip runs
through it: OpenEMR at `/`, the sidecar at `/agentforge/*` (with `Bff__PathBase=/agentforge` set on the
sidecar so its cookie path, SignalR URLs, and post-launch redirects all carry the prefix), and both
`/agentforge/documents/` (W2-D17) and `/agentforge/metrics` explicitly `404`ed so those two
endpoints are unreachable from outside the network — the ingestion endpoint because it authenticates by
trusted private-network origin rather than a token, the metrics endpoint because it has no auth of its
own at all. Its config lint renders the template **twice** and checks each render, and
`reverse-proxy/nginx-behavior-selftest.sh` runs a real nginx against each render and
asserts on the HTTP response, since `nginx -t` alone would stay green even if a location block were
deleted outright. That includes the rate limits, whose `429`s it proves on both sides of the limit,
and the dotfile deny: `404` for `/.gitmodules`, `/.git/config`, a nested dotfile and the
percent-encoded spelling, while SMART, OIDC and root `.well-known` paths still reach OpenEMR.

**On staging, and on production since its promotion, it also serves Grafana at `/grafana`**
which is why each environment's observability tier needs no public domain of its own: the proxy
still holds the **one** internet-facing origin, and Grafana's own login is what stands in front of it. Locally
there is no Grafana to route to and the path is a `404` — see *Configuration* above and
[`DEPLOYMENT.md`](../DEPLOYMENT.md) § *Observability: three
wirings*.

## Dependencies

Both prerequisites have shipped:

- Sidecar path-base support (`#60` - `UsePathBase`, prefix-aware SignalR/chat URLs,
  cookie `Path=/agentforge`)
- OpenEMR-side config (`#25`, `#26` - redirect_uri, launch URI,
  `cookie_samesite` revert) - config only, no fork code changes

The per-environment values (Site Address Override, the two OAuth clients and their redirect URIs, the
module's launch URIs) are database state, scripted by `tools/RegisterSmartClients` +
`tools/BootstrapOpenEmr` — see [`DEPLOYMENT.md`](../DEPLOYMENT.md) §4.
