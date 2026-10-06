"""Deterministic HTTP replayer against an allowlisted target."""

from __future__ import annotations

import json
import os
import uuid
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from http.cookiejar import CookieJar
from pathlib import Path
from typing import Any
from urllib import error, request

from security_platform.allowlist import ConfigError, assert_target_allowed, load_allowlist, platform_root

_MAX_BODY = 65536
# When the per-request rate is zero, the spend ceiling still limits requests.
_CEILING_UNIT = Decimal("0.001")


class _NoRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: ANN001
        return None


def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _money(raw: str, name: str) -> Decimal:
    try:
        value = Decimal(raw)
    except InvalidOperation as exc:
        raise ConfigError(f"{name} is not a decimal") from exc
    if not value.is_finite() or value < 0:
        raise ConfigError(f"{name} must be a non-negative finite decimal")
    return value


def _timeout(raw: str) -> float:
    try:
        return float(raw)
    except ValueError as exc:
        raise ConfigError("SECURITY_PLATFORM_TIMEOUT_SECONDS is not a number") from exc


def _ceiling_charge(per_request: Decimal) -> Decimal:
    return per_request if per_request > 0 else _CEILING_UNIT


def safe_path(path: str) -> str:
    if (
        not isinstance(path, str)
        or not path.startswith("/")
        or path.startswith("//")
        or "\\" in path
        or "://" in path
        or "\n" in path
        or "\r" in path
    ):
        raise ConfigError("case path must be a single-host absolute path")
    return path


def validate_case(case: dict[str, Any]) -> None:
    if not isinstance(case.get("id"), str) or not case["id"]:
        raise ConfigError("case id must be a non-empty string")
    seq = case.get("input_sequence")
    if not isinstance(seq, list) or not seq:
        raise ConfigError(f"{case['id']}: input_sequence must be a non-empty list")
    for step in seq:
        if not isinstance(step, dict):
            raise ConfigError(f"{case['id']}: each input_sequence step must be an object")
        safe_path(step.get("path", ""))
        body = step.get("body")
        if body is not None and not isinstance(body, str):
            raise ConfigError(f"{case['id']}: body must be a string")
        if body is not None and len(body.encode("utf-8")) > _MAX_BODY:
            raise ConfigError(f"{case['id']}: body exceeds {_MAX_BODY} bytes")


def load_cases(path: Path) -> list[dict[str, Any]]:
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        raise ConfigError(f"case file cannot be read: {type(exc).__name__}") from exc
    try:
        cases = json.loads(text)
    except json.JSONDecodeError as exc:
        raise ConfigError(f"case file is not JSON (line {exc.lineno})") from exc
    if not isinstance(cases, list) or not cases:
        raise ConfigError("case file is empty")
    for case in cases:
        if not isinstance(case, dict):
            raise ConfigError("each case must be an object")
        validate_case(case)
    return cases


def session_opener() -> request.OpenerDirector:
    """One per case: steps of a case share its cookies; cases never share them."""
    return request.build_opener(_NoRedirect, request.HTTPCookieProcessor(CookieJar()))


def send_request(
    url: str,
    method: str,
    headers: dict[str, str],
    body: str | None,
    timeout: float,
    opener: request.OpenerDirector | None = None,
) -> int | None:
    data = body.encode("utf-8") if body else None
    req = request.Request(url, data=data, method=method, headers=headers)
    try:
        with (opener or session_opener()).open(req, timeout=timeout) as resp:
            resp.read()
            return int(resp.status)
    except error.HTTPError as exc:
        exc.read()
        return int(exc.code)
    except (error.URLError, TimeoutError, OSError):
        return None


def _step_headers(step: dict[str, Any]) -> dict[str, str]:
    headers = {"User-Agent": "agentforge-security-platform/replayer", "Accept": "application/json"}
    extra = step.get("headers") or {}
    if isinstance(extra, dict):
        for key, value in extra.items():
            if key.lower() in {"host", "authorization", "cookie"}:
                continue
            headers[str(key)] = str(value)
    return headers


def _event(trace: list[dict[str, Any]], **fields: Any) -> None:
    event = {"ts": _now(), **fields}
    trace.append(event)
    print(json.dumps(event, separators=(",", ":")), flush=True)


def run_suite(
    *,
    target: str,
    allowlist: tuple[str, ...],
    cases: list[dict[str, Any]],
    ceiling: Decimal,
    per_request: Decimal,
    timeout: float,
    run_id: str | None = None,
) -> tuple[int, dict[str, Any]]:
    origin = assert_target_allowed(target, allowlist)
    charge = _ceiling_charge(per_request)
    run_id = run_id or str(uuid.uuid4())
    trace: list[dict[str, Any]] = []
    spent = Decimal("0")
    halted = False
    halt_reason = None
    filled: list[dict[str, Any]] = []
    _event(
        trace,
        run_id=run_id,
        agent="deterministic-replayer",
        event="start",
        target_origin=origin,
        cost_usd_total="0",
    )
    for case in cases:
        steps = case["input_sequence"]
        # A case is sent whole or not at all; every step counts against the ceiling.
        if halted or spent + charge * len(steps) > ceiling:
            halted = True
            halt_reason = "spend_ceiling"
            stopped = dict(case)
            stopped["observed"] = {"result": "not_run", "http_status": None, "note": "spend_ceiling", "steps": []}
            filled.append(stopped)
            _event(
                trace,
                run_id=run_id,
                agent="deterministic-replayer",
                event="halt",
                case_id=case["id"],
                cost_usd_delta="0",
                cost_usd_total=format(spent, "f"),
                result="not_run",
            )
            continue
        opener = session_opener()
        observed_steps: list[dict[str, Any]] = []
        status: int | None = None
        for index, step in enumerate(steps, start=1):
            status = send_request(
                origin + safe_path(step["path"]),
                str(step.get("method", "GET")).upper(),
                _step_headers(step),
                step.get("body"),
                timeout,
                opener,
            )
            spent += charge
            observed_steps.append({"step": index, "http_status": status})
            _event(
                trace,
                run_id=run_id,
                agent="deterministic-replayer",
                event="request",
                case_id=case["id"],
                step=index,
                steps=len(steps),
                http_status=status,
                cost_usd_delta=format(charge, "f"),
                cost_usd_total=format(spent, "f"),
            )
            if status is None:
                break
        expected = case.get("expected_statuses") or []
        # The expected-safe check applies to the final step; an unreachable step ends the case as partial.
        if status is None:
            result, note = "partial", "target_unreachable"
        elif status in set(expected):
            result, note = "pass", "defense_held"
        else:
            result, note = "fail", "defense_did_not_hold"
        filled_case = dict(case)
        filled_case["observed"] = {"result": result, "http_status": status, "note": note, "steps": observed_steps}
        filled.append(filled_case)
        _event(
            trace,
            run_id=run_id,
            agent="deterministic-replayer",
            event="case",
            case_id=case["id"],
            http_status=status,
            result=result,
            cost_usd_total=format(spent, "f"),
        )
    unreachable = [c for c in filled if c["observed"]["note"] == "target_unreachable"]
    executed = [c for c in filled if c["observed"]["result"] != "not_run"]
    if halted and not executed:
        code = 3
    elif executed and len(unreachable) == len(executed):
        code = 1
    elif halted:
        code = 3
    else:
        code = 0
    document = {
        "run_id": run_id,
        "agent": "deterministic-replayer",
        "target_origin": origin,
        "started_at": trace[0]["ts"],
        "finished_at": _now(),
        "halted": halted,
        "halt_reason": halt_reason,
        "cost_usd": format(spent, "f"),
        "spend_ceiling_usd": format(ceiling, "f"),
        "usd_per_request": format(per_request, "f"),
        "trace": trace,
        "cases": filled,
    }
    _event(
        trace,
        run_id=run_id,
        agent="deterministic-replayer",
        event="finish",
        cost_usd_total=format(spent, "f"),
        halted=halted,
        exit_code=code,
    )
    document["trace"] = trace
    document["finished_at"] = trace[-1]["ts"]
    return code, document


def main_run(env: dict[str, str] | None = None) -> int:
    source = env if env is not None else os.environ
    try:
        allowlist = load_allowlist(source)
        target = source.get("SECURITY_PLATFORM_TARGET_URL", "").strip()
        if not target:
            raise ConfigError("SECURITY_PLATFORM_TARGET_URL is unset")
        cases_path = source.get("SECURITY_PLATFORM_CASES_FILE", "").strip()
        if not cases_path:
            raise ConfigError("SECURITY_PLATFORM_CASES_FILE is unset")
        ceiling = _money(source.get("SECURITY_PLATFORM_SPEND_CEILING_USD", "1"), "SECURITY_PLATFORM_SPEND_CEILING_USD")
        per_request = _money(source.get("SECURITY_PLATFORM_USD_PER_REQUEST", "0.001"), "SECURITY_PLATFORM_USD_PER_REQUEST")
        cases = load_cases(Path(cases_path))
        code, document = run_suite(
            target=target,
            allowlist=allowlist,
            cases=cases,
            ceiling=ceiling,
            per_request=per_request,
            timeout=_timeout(source.get("SECURITY_PLATFORM_TIMEOUT_SECONDS", "20")),
        )
    except ConfigError as exc:
        print(json.dumps({"event": "refused", "agent": "deterministic-replayer", "reason": str(exc)}), flush=True)
        return 2
    out = source.get("SECURITY_PLATFORM_RESULTS", "").strip()
    if out:
        path = Path(out)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return code
