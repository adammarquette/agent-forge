#!/usr/bin/env bash
# jq-crlf.sh — sourced by every script under scripts/ that reads jq's output into a shell variable.
#
# WHY THIS EXISTS. Windows Git Bash's jq (measured: 1.8.2) writes CRLF line endings even though every
# caller here runs under bash's own LF world. Command substitution `$(...)` strips only a trailing `\n`,
# never `\r`, so `id="$(jq -r '.id' file)"` on that platform leaves `id` ending in a literal carriage
# return. Every later comparison against it — `[ "$id" = "$expected" ]`, `!= null`, a value re-fed to jq
# via `--argjson` — silently fails or reads as null, which is why railway-stays-up.sh read `deployment
# null` for every service run natively in Git Bash on Windows, while the identical check passed inside
# alpine:3.21. A separate change
#
# THE FIX IS ONE FUNCTION, NOT A `tr -d '\r'` AT EVERY CALL SITE. `jq` is overridden as a shell function
# for the rest of the sourcing script's process — which includes anything that script itself `.`
# sources, such as railway-data-refreshable.sh — so no call site has to remember to launder its own
# output, and a jq call added later is CRLF-safe by default rather than by discipline.
#
# EXIT STATUS SURVIVES THE PIPE ONLY UNDER PIPEFAIL. Every script that sources this already runs with
# `set -o pipefail` (via `-uo pipefail` or `-euo pipefail`), so `command jq "$@" | tr -d '\r'` reports
# the exit status of the LAST command in the pipeline to fail — `tr -d` does not itself fail on
# ordinary input, so a failing jq (a parse error, or `-e` finding nothing truthy) still fails the
# pipeline exactly as a bare `jq` call would. WITHOUT pipefail, the pipeline's status is `tr`'s, which
# is always 0: every `jq -e` guard in the sourcing script would read as a permissive pass whatever jq
# actually decided — the exact failure mode this file exists to close. So this refuses to load rather
# than silently becoming that hazard: a script that sources it without pipefail already on gets a
# clear refusal here instead of a guard that quietly stops meaning anything downstream.
if ! [[ -o pipefail ]]; then
    printf 'jq-crlf.sh: refusing to load — pipefail is not set in the sourcing shell.\n' >&2
    printf '  Without it, "command jq | tr -d %s" reports tr'"'"'s exit status (always 0), not jq'"'"'s,\n' "'\\r'" >&2
    printf '  which would turn every "jq -e" guard downstream into a silent pass. Add\n' >&2
    printf '  "set -o pipefail" (or -uo/-euo pipefail) before sourcing scripts/lib/jq-crlf.sh.\n' >&2
    return 1
fi

# `command jq` INSIDE THE FUNCTION, NOT A BARE `jq`: without it the function would call itself.
jq() { command jq "$@" | tr -d '\r'; }
