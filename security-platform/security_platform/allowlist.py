"""Target allowlist. Production is refused even if a config names it."""

from __future__ import annotations

import json
import os
from pathlib import Path
from urllib.parse import urlsplit

# The production front door. Refused until a maintainer says otherwise.
PRODUCTION_ORIGIN = "https://reverse-proxy-production-395f.up.railway.app"
STAGING_ORIGIN = "https://reverse-proxy-staging-5c25.up.railway.app"
ALWAYS_DENIED = frozenset({PRODUCTION_ORIGIN})

_LOOPBACK = frozenset({"127.0.0.1", "localhost"})


class ConfigError(Exception):
    """The run must not start. Nothing has been sent."""


def platform_root() -> Path:
    return Path(__file__).resolve().parent.parent


def normalize_hostname(host: str) -> str:
    return host.lower().rstrip(".")


def origin_of(url: str) -> str:
    parts = urlsplit(url.strip())
    if parts.username or parts.password:
        raise ConfigError("target URL must not carry userinfo")
    if parts.scheme not in {"https", "http"} or not parts.hostname:
        raise ConfigError("target URL must be http(s) with a host")
    host = normalize_hostname(parts.hostname)
    # Loopback may be http so the self-test can bind a local server. Every other host is https.
    if parts.scheme != "https" and host not in _LOOPBACK:
        raise ConfigError("target URL must be https")
    port = parts.port
    default_port = (parts.scheme == "https" and port in (None, 443)) or (
        parts.scheme == "http" and port in (None, 80)
    )
    if default_port:
        return f"{parts.scheme}://{host}"
    return f"{parts.scheme}://{host}:{port}"


def parse_allowlist(raw: str) -> tuple[str, ...]:
    entries: list[str] = []
    for piece in raw.split(","):
        piece = piece.strip()
        if not piece:
            continue
        origin = origin_of(piece)
        if origin in ALWAYS_DENIED:
            raise ConfigError(f"{origin} is production and is not an allowable target")
        entries.append(origin)
    if not entries:
        raise ConfigError("allowlist is empty")
    return tuple(dict.fromkeys(entries))


def default_allowlist_text() -> str:
    path = platform_root() / "allowlist.json"
    values = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(values, list) or not all(isinstance(v, str) for v in values):
        raise ConfigError("allowlist.json must be a list of URL strings")
    return ",".join(values)


def committed_allowlist() -> tuple[str, ...]:
    return parse_allowlist(default_allowlist_text())


def load_allowlist(env: dict[str, str] | None = None) -> tuple[str, ...]:
    source = env if env is not None else os.environ
    committed = committed_allowlist()
    raw = source.get("SECURITY_PLATFORM_ALLOWLIST", "").strip()
    if not raw:
        return committed
    requested = parse_allowlist(raw)
    committed_set = set(committed)
    for origin in requested:
        if origin not in committed_set:
            raise ConfigError(
                f"{origin} is not on the committed allowlist; "
                "SECURITY_PLATFORM_ALLOWLIST may only narrow it"
            )
    requested_set = set(requested)
    narrowed = tuple(origin for origin in committed if origin in requested_set)
    return narrowed if len(narrowed) < len(committed) else committed


def assert_target_allowed(target: str, allowlist: tuple[str, ...]) -> str:
    origin = origin_of(target)
    if origin in ALWAYS_DENIED:
        raise ConfigError(f"{origin} is production and is refused")
    if origin not in allowlist:
        raise ConfigError(f"{origin} is not on the allowlist")
    return origin
