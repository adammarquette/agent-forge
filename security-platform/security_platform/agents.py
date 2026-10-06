"""Loads each agent's runtime definition and its versioned system prompt from files.

A prompt is never inlined in code: agents/<name>.json names its prompt file, its version and the sha256 of
its text. The loader refuses the prompt unless its front-matter agent and version agree with the definition
and its text hashes to the pinned value, so any edit to a prompt needs a matching edit to its definition.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

from security_platform.allowlist import ConfigError, platform_root

AGENT_NAMES = ("orchestrator", "red_team", "judge", "documentation")

_REQUIRED = (
    "schema_version", "agent", "role", "inputs", "outputs", "trust_level",
    "may_call_target", "prompt", "model", "coordinates_with",
)


def agents_dir() -> Path:
    return platform_root() / "agents"


def load_definition(name: str) -> dict[str, Any]:
    if name not in AGENT_NAMES:
        raise ConfigError(f"unknown agent {name!r}")
    path = agents_dir() / f"{name}.json"
    try:
        definition = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ConfigError(f"{path.name} cannot be read: {type(exc).__name__}") from exc
    missing = [k for k in _REQUIRED if k not in definition]
    if missing:
        raise ConfigError(f"{path.name} is missing {missing}")
    if definition["agent"] != name:
        raise ConfigError(f"{path.name} names agent {definition['agent']!r}")
    return definition


def _split_front_matter(text: str) -> tuple[dict[str, str], str]:
    lines = text.splitlines()
    if not lines or lines[0].strip() != "---":
        raise ConfigError("prompt has no front matter")
    meta: dict[str, str] = {}
    for index, line in enumerate(lines[1:], start=1):
        if line.strip() == "---":
            return meta, "\n".join(lines[index + 1 :]).strip()
        key, sep, value = line.partition(":")
        if not sep:
            raise ConfigError(f"front matter line is not key: value: {line!r}")
        meta[key.strip()] = value.strip()
    raise ConfigError("prompt front matter is not closed")


def prompt_sha256(text: str) -> str:
    """The pin agents/<name>.json carries: sha256 of the prompt file with line endings normalised to LF."""
    return hashlib.sha256(text.replace("\r\n", "\n").encode("utf-8")).hexdigest()


def load_prompt(name: str) -> tuple[dict[str, str], str]:
    """(front matter, prompt body) for the agent's current prompt version."""
    definition = load_definition(name)
    spec = definition["prompt"]
    path = (platform_root() / spec["file"]).resolve()
    if platform_root().resolve() / "prompts" not in path.parents:
        raise ConfigError(f"{name}: prompt file must be under prompts/")
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise ConfigError(f"{name}: prompt cannot be read: {type(exc).__name__}") from exc
    if prompt_sha256(text) != spec.get("sha256"):
        raise ConfigError(f"{name}: prompt text does not match the sha256 pinned in {name}.json")
    meta, body = _split_front_matter(text)
    if meta.get("agent") != name:
        raise ConfigError(f"{name}: prompt front matter names agent {meta.get('agent')!r}")
    if meta.get("version") != spec["version"]:
        raise ConfigError(f"{name}: prompt version {meta.get('version')!r} is not {spec['version']!r}")
    if not body:
        raise ConfigError(f"{name}: prompt body is empty")
    return meta, body
