#!/usr/bin/env python3
"""Pre-publish check: the allowlist carries the staging origin and production is refused.

publish-security-platform-image runs this inside the candidate image and refuses to publish on a
non-zero exit; security-platform/run-selftest.sh runs it against the tree so a broken check fails
in the MR pipeline, not on develop. Optional argument: an allowlist path (default: the one beside
this file).
"""

from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from security_platform.allowlist import (  # noqa: E402
    PRODUCTION_ORIGIN,
    STAGING_ORIGIN,
    ConfigError,
    assert_target_allowed,
    parse_allowlist,
)


def refused(fn) -> bool:
    try:
        fn()
    except ConfigError:
        return True
    return False


def main(argv: list[str]) -> None:
    path = Path(argv[1]) if len(argv) > 1 else ROOT / "allowlist.json"
    raw = path.read_text()
    if STAGING_ORIGIN not in raw:
        raise SystemExit("staging origin missing from allowlist.json")
    if "reverse-proxy-production" in raw:
        raise SystemExit("production origin is in allowlist.json")
    if not refused(lambda: assert_target_allowed(PRODUCTION_ORIGIN + "/", (STAGING_ORIGIN, PRODUCTION_ORIGIN))):
        raise SystemExit("production origin was allowlisted")
    # Normalisation decides the next three: a spelling that is not the constant it names.
    upper = PRODUCTION_ORIGIN.replace("reverse-proxy-production", "REVERSE-PROXY-PRODUCTION")
    if not refused(lambda: parse_allowlist(PRODUCTION_ORIGIN + "./")) or not refused(lambda: parse_allowlist(upper + "/")):
        raise SystemExit("a respelled production origin was allowlisted")
    if assert_target_allowed(STAGING_ORIGIN + "./", (STAGING_ORIGIN,)) != STAGING_ORIGIN:
        raise SystemExit("a trailing-dot staging host did not compare as the staging origin")
    print("check_image_allowlist: OK")


if __name__ == "__main__":
    main(sys.argv)
