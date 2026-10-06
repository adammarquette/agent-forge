"""Idle process for the Railway service. It does not run the suite."""

from __future__ import annotations

import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def dispatch(method: str, path: str) -> tuple[int, bytes]:
    clean = path.split("?", 1)[0]
    if method == "GET" and clean == "/health":
        return 200, b'{"status":"idle","role":"deterministic-replayer"}'
    return 404, b""


class _Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802
        self._reply()

    def do_POST(self) -> None:  # noqa: N802
        self._reply()

    def _reply(self) -> None:
        status, body = dispatch(self.command, self.path)
        self.send_response(status)
        if body:
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if body:
            self.wfile.write(body)

    def log_message(self, fmt: str, *args: object) -> None:
        return


def main_serve() -> int:
    port = int(os.environ.get("PORT", "8080"))
    server = ThreadingHTTPServer(("0.0.0.0", port), _Handler)
    server.serve_forever()
    return 0
