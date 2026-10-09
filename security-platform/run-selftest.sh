#!/bin/sh
# Counted self-test for the deterministic replayer. run_selftest.py checks the
# same number, so an assertion that silently stops running fails the suite.
EXPECTED_ASSERTIONS=57
set -eu
here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
cd "$here"
python run_selftest.py --expected "$EXPECTED_ASSERTIONS"
# The pre-publish check publish-security-platform-image runs in the image; a broken one fails here first.
python check_image_allowlist.py
# The agent definitions, prompt files and message schemas the image loads.
python check_agents.py
