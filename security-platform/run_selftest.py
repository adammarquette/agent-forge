#!/usr/bin/env python3
"""Self-test for the deterministic replayer. No network except a local listener."""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
import threading
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from security_platform.allowlist import (  # noqa: E402
    PRODUCTION_ORIGIN,
    STAGING_ORIGIN,
    ConfigError,
    assert_target_allowed,
    load_allowlist,
    normalize_hostname,
    origin_of,
    parse_allowlist,
)
from security_platform.runner import load_cases, main_run, run_suite, safe_path  # noqa: E402
from security_platform.serve import dispatch  # noqa: E402

SECRET = "PATIENT-SECRET-do-not-record"
SESSION = "synthetic-session-do-not-record"
OPEN, CHECK, DEFAULT = "/session/open", "/session/check", "/agentforge/evidence/ask"
DROP = "/session/drop"
passed = 0
failed = 0


def check(cond: bool, label: str) -> None:
    global passed, failed
    if cond:
        passed += 1
        print(f"ok {label}")
    else:
        failed += 1
        print(f"FAIL {label}")


def accepts(fn, expected: object, label: str) -> None:
    try:
        check(fn() == expected, label)
    except ConfigError:
        check(False, label)


def raises(fn, label: str) -> None:
    try:
        fn()
    except ConfigError:
        check(True, label)
        return
    check(False, label)


class _Handler(BaseHTTPRequestHandler):
    # OPEN starts a session, CHECK answers 204 only inside it, DROP closes the socket, anything else is 401.
    def _hit(self) -> None:
        self.server.hits += 1  # type: ignore[attr-defined]
        self.server.paths.append(self.path)  # type: ignore[attr-defined]
        n = int(self.headers.get("Content-Length", "0") or 0)
        if n:
            self.rfile.read(n)
        if self.path == OPEN:
            self.send_response(200)
            self.send_header("Set-Cookie", f"sp_session={SESSION}; Path=/")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        if self.path == DROP:
            # No response: the client sees the connection close, as for an unreachable step.
            self.close_connection = True
            return
        if self.path == CHECK:
            inside = f"sp_session={SESSION}" in (self.headers.get("Cookie") or "")
            self.send_response(204 if inside else 403)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        body = SECRET.encode()
        self.send_response(401)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        self._hit()

    def do_POST(self) -> None:  # noqa: N802
        self._hit()

    def log_message(self, fmt: str, *args: object) -> None:
        return


def _cases() -> list[dict]:
    def one(n: int) -> dict:
        return {
            "id": f"t{n}",
            "input_sequence": [{"method": "POST", "path": "/agentforge/evidence/ask", "body": "{}"}],
            "expected_statuses": [401],
        }

    return [one(1), one(2)]


def _step(path: str) -> dict:
    return {"method": "POST", "path": path, "body": "{}"}


def _case(case_id: str, paths: list[str], expected: int) -> dict:
    return {"id": case_id, "input_sequence": [_step(p) for p in paths], "expected_statuses": [expected]}


def _reset(server: ThreadingHTTPServer) -> None:
    server.hits = 0  # type: ignore[attr-defined]
    server.paths = []  # type: ignore[attr-defined]


def _write_cases(path: Path, cases: list[dict]) -> None:
    path.write_text(json.dumps(cases), encoding="utf-8")


def _serve() -> tuple[ThreadingHTTPServer, str]:
    server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
    _reset(server)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    origin = f"http://127.0.0.1:{server.server_address[1]}"
    return server, origin


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected", type=int, required=True)
    args = parser.parse_args()

    raises(lambda: parse_allowlist(PRODUCTION_ORIGIN), "production cannot be added to the allowlist")
    raises(
        lambda: assert_target_allowed(PRODUCTION_ORIGIN + "/", (PRODUCTION_ORIGIN, STAGING_ORIGIN)),
        "production is refused even when handed to the checker",
    )
    raises(
        lambda: assert_target_allowed(PRODUCTION_ORIGIN + "./", (STAGING_ORIGIN,)),
        "production trailing-dot spelling is refused",
    )
    # Normalisation decides these: without it the spelling is not the constant it names.
    upper_staging = STAGING_ORIGIN.replace("reverse-proxy-staging-5c25.up.railway.app", "REVERSE-Proxy-Staging-5C25.UP.railway.APP")
    upper_production = PRODUCTION_ORIGIN.replace("reverse-proxy-production-395f.up.railway.app", "REVERSE-Proxy-Production-395F.UP.railway.APP")
    check(
        normalize_hostname("REVERSE-Proxy-Staging-5C25.UP.railway.app.") == "reverse-proxy-staging-5c25.up.railway.app",
        "normalize_hostname lower-cases and strips the trailing dot",
    )
    accepts(
        lambda: assert_target_allowed(STAGING_ORIGIN + "./agentforge/", (STAGING_ORIGIN,)),
        STAGING_ORIGIN,
        "a trailing-dot staging host is accepted as the staging origin",
    )
    accepts(
        lambda: assert_target_allowed(upper_staging + "/agentforge/", (STAGING_ORIGIN,)),
        STAGING_ORIGIN,
        "an upper-case staging host is accepted as the staging origin",
    )
    accepts(
        lambda: load_allowlist({"SECURITY_PLATFORM_ALLOWLIST": upper_staging + "./"}),
        (STAGING_ORIGIN,),
        "env may name the committed staging origin in another spelling",
    )
    raises(lambda: parse_allowlist(PRODUCTION_ORIGIN + "./"), "production with a trailing dot cannot be added to the allowlist")
    raises(lambda: parse_allowlist(upper_production + "/"), "upper-case production cannot be added to the allowlist")
    raises(
        lambda: assert_target_allowed(STAGING_ORIGIN + ".evil.example/", (STAGING_ORIGIN,)),
        "a host that extends the staging host is refused",
    )
    raises(
        lambda: assert_target_allowed("https://example.org/", (STAGING_ORIGIN,)),
        "a host outside the allowlist is refused",
    )
    raises(
        lambda: load_allowlist({"SECURITY_PLATFORM_ALLOWLIST": "https://example.org/"}),
        "env cannot replace the committed allowlist with a third party",
    )
    raises(lambda: origin_of("http://example.com/"), "non-loopback http is refused")
    raises(lambda: origin_of("https://user:pw@example.com/"), "userinfo is refused")
    raises(lambda: safe_path("https://evil.example/x"), "a case path that is a URL is refused")
    raises(lambda: safe_path("relative"), "a relative case path is refused")

    allow = json.loads((ROOT / "allowlist.json").read_text(encoding="utf-8"))
    check(allow == [STAGING_ORIGIN + "/"], "committed allowlist is only the staging front door")
    check(PRODUCTION_ORIGIN not in json.dumps(allow), "committed allowlist does not name production")
    check(
        assert_target_allowed(STAGING_ORIGIN + "/agentforge/", (STAGING_ORIGIN,)) == STAGING_ORIGIN,
        "the staging origin is allowlisted",
    )
    check(
        load_allowlist({"SECURITY_PLATFORM_ALLOWLIST": STAGING_ORIGIN + "/"}) == (STAGING_ORIGIN,),
        "env may repeat the committed staging origin",
    )

    status, body = dispatch("GET", "/health")
    check(status == 200 and b"deterministic-replayer" in body, "serve /health is idle")
    status, _ = dispatch("POST", "/run")
    check(status == 404, "serve does not expose a run trigger")

    with tempfile.TemporaryDirectory() as tmp:
        good = Path(tmp) / "cases.json"
        bad = Path(tmp) / "bad.json"
        _write_cases(good, _cases())
        _write_cases(bad, [{"id": "bad", "input_sequence": ["not-an-object"]}])
        loaded = load_cases(good)
        check(len(loaded) == 2, "cases load from SECURITY_PLATFORM_CASES_FILE path")
        try:
            load_cases(bad)
            check(False, "invalid case file is refused on its own")
        except ConfigError:
            check(True, "invalid case file is refused on its own")
        not_json = Path(tmp) / "not-json.json"
        not_json.write_text("not json", encoding="utf-8")
        body_not_string = Path(tmp) / "body.json"
        _write_cases(body_not_string, [{"id": "b", "input_sequence": [{"path": "/x", "body": 7}]}])
        for label, case_file in (
            ("main refuses a case file that is not JSON with exit 2", not_json),
            ("main refuses a missing case file with exit 2", Path(tmp) / "missing.json"),
            ("main refuses a non-string body with exit 2", body_not_string),
        ):
            try:
                rc = main_run(
                    {
                        "SECURITY_PLATFORM_TARGET_URL": STAGING_ORIGIN + "/",
                        "SECURITY_PLATFORM_CASES_FILE": str(case_file),
                        # Zero ceiling: even a mutant that loads the file sends nothing.
                        "SECURITY_PLATFORM_SPEND_CEILING_USD": "0",
                    }
                )
            except Exception:  # noqa: BLE001
                rc = None
            check(rc == 2, label)

    server, origin = _serve()
    try:
        code, doc = run_suite(
            target=origin + "/",
            allowlist=(origin,),
            cases=_cases(),
            ceiling=Decimal("0"),
            per_request=Decimal("0.001"),
            timeout=5,
        )
        check(server.hits == 0, "a zero ceiling sends no request")  # type: ignore[attr-defined]
        check(code == 3 and doc["halted"] is True and doc["halt_reason"] == "spend_ceiling", "a zero ceiling halts the run")
        check(doc["cost_usd"] == "0", "a halted-before-send run records zero cost")
        check(all(c["observed"]["result"] == "not_run" for c in doc["cases"]), "cases skipped by the ceiling are not_run")

        _reset(server)
        code, doc = run_suite(
            target=origin + "/",
            allowlist=(origin,),
            cases=_cases(),
            ceiling=Decimal("0.002"),
            per_request=Decimal("0.001"),
            timeout=5,
        )
        blob = json.dumps(doc)
        check(server.hits == 2, "a fitting ceiling sends every case")  # type: ignore[attr-defined]
        check(code == 0 and doc["halted"] is False, "a completed run exits 0")
        check(doc["cost_usd"] == "0.002", "cost is requests times the per-request rate")
        check(
            all(c["observed"]["result"] == "pass" and c["observed"]["http_status"] == 401 for c in doc["cases"]),
            "observed result is recorded on each case",
        )
        check(SECRET not in blob, "the response body is not in the result or the trace")
        check(all("body" not in event for event in doc["trace"]), "trace events have no body field")
        check(
            any(event["event"] == "request" and "cost_usd_total" in event for event in doc["trace"]),
            "the trace records cost as the run proceeds",
        )

        _reset(server)
        code, doc = run_suite(
            target=origin + "/",
            allowlist=(origin,),
            cases=_cases(),
            ceiling=Decimal("0.001"),
            per_request=Decimal("0.001"),
            timeout=5,
        )
        check(server.hits == 1, "a ceiling that fits one request stops before the second")  # type: ignore[attr-defined]
        check(code == 3 and doc["halted"] is True, "stopping at the ceiling is a halt, not a success")
        check(doc["cases"][1]["observed"]["result"] == "not_run", "the case that would exceed the ceiling is not sent")

        _reset(server)
        code, doc = run_suite(
            target=origin + "/",
            allowlist=(origin,),
            cases=_cases(),
            ceiling=Decimal("0.001"),
            per_request=Decimal("0"),
            timeout=5,
        )
        check(server.hits == 1, "a zero per-request rate still counts against the spend ceiling")  # type: ignore[attr-defined]
        check(code == 3 and doc["halted"] is True, "a zero per-request rate still halts at the ceiling")

        # Multi-step cases: every step, in order, in one session, each step paid for.
        def suite(cases: list[dict], ceiling: str) -> tuple[int, dict]:
            _reset(server)
            return run_suite(
                target=origin + "/",
                allowlist=(origin,),
                cases=cases,
                ceiling=Decimal(ceiling),
                per_request=Decimal("0.001"),
                timeout=5,
            )

        code, doc = suite([_case("two-step", [OPEN, CHECK], 204)], "1")
        check(server.paths == [OPEN, CHECK], "a two-step case sends both steps, in order")  # type: ignore[attr-defined]
        observed = doc["cases"][0]["observed"]
        check(
            code == 0 and observed["result"] == "pass" and [s["http_status"] for s in observed["steps"]] == [200, 204],
            "the second step runs in the session the first step opened",
        )
        check(
            [e["step"] for e in doc["trace"] if e["event"] == "request"] == [1, 2] and doc["cost_usd"] == "0.002",
            "the trace records and charges every step",
        )
        check(SESSION not in json.dumps(doc), "the session cookie is not in the result or the trace")

        code, doc = suite([_case("late-check", [DEFAULT, OPEN], 401)], "1")
        observed = doc["cases"][0]["observed"]
        check(
            server.paths == [DEFAULT, OPEN] and observed["result"] == "fail" and observed["http_status"] == 200,  # type: ignore[attr-defined]
            "a case cannot pass on its first step when the check applies at a later step",
        )

        code, doc = suite([_case("opens", [OPEN, CHECK], 204), _case("fresh", [CHECK], 204)], "1")
        check(
            [c["observed"]["result"] for c in doc["cases"]] == ["pass", "fail"]
            and doc["cases"][1]["observed"]["http_status"] == 403,
            "cases do not share a session",
        )

        code, doc = suite([_case("a", [OPEN, CHECK], 204), _case("b", [OPEN, CHECK], 204)], "0.003")
        check(
            server.paths == [OPEN, CHECK] and doc["cost_usd"] == "0.002",  # type: ignore[attr-defined]
            "each step counts against the spend ceiling",
        )
        check(
            code == 3 and doc["cases"][1]["observed"]["result"] == "not_run",
            "a case whose steps would pass the ceiling is not started",
        )

        code, doc = suite([_case("c", [OPEN, CHECK], 204)], "0.001")
        check(server.paths == [] and code == 3, "a ceiling that fits one step of a two-step case sends nothing")  # type: ignore[attr-defined]

        code, doc = suite([_case("drop", [DROP, OPEN], 200)], "1")
        observed = doc["cases"][0]["observed"]
        check(
            server.paths == [DROP] and code == 1 and observed["result"] == "partial" and len(observed["steps"]) == 1,  # type: ignore[attr-defined]
            "an unreachable step ends its case as partial and sends no later step",
        )

        forged = _case("forged", [CHECK], 204)
        forged["input_sequence"][0]["headers"] = {"Cookie": f"sp_session={SESSION}"}
        code, doc = suite([forged], "1")
        check(
            server.paths == [CHECK] and doc["cases"][0]["observed"]["http_status"] == 403,  # type: ignore[attr-defined]
            "a Cookie header written into a case is not sent",
        )

        code, doc = suite([_case("big", [OPEN, CHECK], 204), _case("small", [CHECK], 204)], "0.001")
        check(
            server.paths == [] and [c["observed"]["result"] for c in doc["cases"]] == ["not_run", "not_run"],  # type: ignore[attr-defined]
            "once the ceiling halts a run, no later case is sent",
        )
    finally:
        server.shutdown()

    with tempfile.TemporaryDirectory() as tmp:
        cases_path = Path(tmp) / "cases.json"
        _write_cases(cases_path, _cases())
        refused = main_run(
            {
                "SECURITY_PLATFORM_TARGET_URL": PRODUCTION_ORIGIN + "/",
                "SECURITY_PLATFORM_CASES_FILE": str(cases_path),
                "SECURITY_PLATFORM_SPEND_CEILING_USD": "1",
            }
        )
        check(refused == 2, "main refuses the production front door before a run")
        refused = main_run(
            {
                "SECURITY_PLATFORM_TARGET_URL": STAGING_ORIGIN + "/",
                "SECURITY_PLATFORM_CASES_FILE": str(cases_path),
                "SECURITY_PLATFORM_TIMEOUT_SECONDS": "not-a-number",
                "SECURITY_PLATFORM_SPEND_CEILING_USD": "0",
            }
        )
        check(refused == 2, "main refuses a non-numeric timeout before a run")

    print(
        f"SELF-TEST PASSED - {passed} of {passed + failed} assertions" if failed == 0
        else f"SELF-TEST FAILED - {failed} failed, {passed} passed"
    )
    if failed:
        return 1
    if passed != args.expected:
        print(f"FAIL assertion count {passed} != expected {args.expected}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
