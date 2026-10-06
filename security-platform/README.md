# security-platform

The deterministic HTTP replayer image (`security-platform-sha-<12>`). It is not part of
`docker-compose.yml`. How it is built and deployed is `DEPLOYMENT.md`.

`serve` (the image default, and the Railway service) answers `GET /health` and does not send
requests. `run` replays a case file named by `SECURITY_PLATFORM_CASES_FILE` against
`SECURITY_PLATFORM_TARGET_URL` only when that origin is on the committed allowlist (env may
narrow, never replace). Every step of a case's `input_sequence` is sent in order, in one cookie session
that no other case shares, and `expected_statuses` is checked against the last step. A case is sent
whole or not at all: each step counts against the spend ceiling, and after a halt nothing more is sent
. Concrete cases belong to a separate change and `evals/adversarial/`, and conform to
`schema/attack-case.schema.json`; live runs against staging are a separate change.
The production front door is refused in code.

`check_image_allowlist.py` is the pre-publish check: `publish-security-platform-image` runs it in the
candidate image and does not push unless it exits 0, and `run-selftest.sh` runs it against the tree

The agents (defined, not run): `agents/<name>.json` is each agent's runtime definition and
`prompts/<name>.md` its system prompt, versioned in its front matter; `security_platform/agents.py`
loads both and refuses a prompt whose version or sha256 disagrees with its definition. Nothing in the
image calls the loader yet; `check_agents.py` does. `schema/` holds the
attack-case and inter-agent message schemas, with synthetic examples, and `security_platform/schema_check.py`
validates against them without third-party packages. `check_agents.py`, run by `run-selftest.sh`, checks
all of it. The Red Team prompt is a structure-only draft to be rewritten before use, and every model
choice is a proposal. The definitions themselves are `SECURITY-PLATFORM.md`.
