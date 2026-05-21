from __future__ import annotations

import asyncio
import inspect
import json
import os
import sys
import threading
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Awaitable, Callable
from urllib.parse import parse_qs, urlparse

PROTOCOL_VERSION = 1
TOKEN_HEADER_NAME = "X-SharpClaw-Control-Token"

ENV = {
    "module_directory": "SHARPCLAW_MODULE_DIR",
    "module_data_directory": "SHARPCLAW_MODULE_DATA_DIR",
    "control_address": "SHARPCLAW_CONTROL_ADDRESS",
    "control_token": "SHARPCLAW_CONTROL_TOKEN",
    "module_id": "SHARPCLAW_MODULE_ID",
    "module_runtime": "SHARPCLAW_MODULE_RUNTIME",
}

CONTROL_PATHS = {
    "handshake": "/.sharpclaw/handshake",
    "discovery": "/.sharpclaw/discovery",
    "health": "/.sharpclaw/health",
    "initialize": "/.sharpclaw/initialize",
    "shutdown": "/.sharpclaw/shutdown",
}

Handler = Callable[["RequestContext"], Any | Awaitable[Any]]


@dataclass(slots=True)
class Response:
    body: bytes | str = b""
    status: int = 200
    headers: dict[str, str] = field(default_factory=dict)


@dataclass(slots=True)
class RequestContext:
    method: str
    path: str
    query: dict[str, list[str]]
    headers: dict[str, str]
    params: dict[str, str]
    body: bytes
    environ: dict[str, str | None]

    def read_text(self) -> str:
        return self.body.decode("utf-8")

    def read_json(self) -> Any:
        return json.loads(self.read_text() or "null")


class SharpClawHost:
    def __init__(
        self,
        *,
        module_id: str,
        tool_prefix: str,
        endpoints: list[dict[str, Any]] | None = None,
        initialize: Handler | None = None,
        shutdown: Handler | None = None,
        health: Handler | None = None,
        asgi_app: Callable[..., Awaitable[None]] | None = None,
        capabilities: list[str] | None = None,
        runtime: str = "python",
        runtime_version: str | None = None,
        control_address: str | None = None,
        control_token: str | None = None,
    ) -> None:
        if not module_id:
            raise ValueError("SharpClaw module_id is required.")

        if not tool_prefix:
            raise ValueError("SharpClaw tool_prefix is required.")

        self.module_id = os.getenv(ENV["module_id"], module_id)
        self.tool_prefix = tool_prefix
        self.endpoints = [_normalize_endpoint(endpoint) for endpoint in endpoints or []]
        self.initialize = initialize or _noop
        self.shutdown = shutdown or _noop
        self.health = health or (lambda _: {"isHealthy": True, "message": "ready"})
        self.asgi_app = asgi_app
        self.capabilities = capabilities or ["endpoints", "lifecycleHooks"]
        self.runtime = runtime
        self.runtime_version = runtime_version or sys.version.split()[0]
        self.control_address = control_address or _read_required_env(ENV["control_address"])
        self.control_token = control_token or _read_required_env(ENV["control_token"])
        self._server: ThreadingHTTPServer | None = None

    def serve(self) -> None:
        parsed = urlparse(self.control_address)
        host = parsed.hostname or "127.0.0.1"
        port = parsed.port or 0

        class Handler(SharpClawRequestHandler):
            sharpclaw_host = self

        self._server = ThreadingHTTPServer((host, port), Handler)
        self._server.serve_forever()

    def stop(self) -> None:
        if self._server is not None:
            self._server.shutdown()

    def handle(
        self,
        request: BaseHTTPRequestHandler,
        method: str,
        path: str,
        query: dict[str, list[str]],
        body: bytes,
    ) -> Response:
        headers = {key: value for key, value in request.headers.items()}
        if request.headers.get(TOKEN_HEADER_NAME) != self.control_token:
            return json_response({"error": "Unauthorized"}, status=401)

        if path.startswith("/.sharpclaw/"):
            return self._handle_control(method, path, headers, query, body)

        return self._handle_endpoint(method, path, headers, query, body)

    def _handle_control(
        self,
        method: str,
        path: str,
        headers: dict[str, str],
        query: dict[str, list[str]],
        body: bytes,
    ) -> Response:
        context = self._context(method, path, headers, query, {}, body)

        if method == "POST" and path == CONTROL_PATHS["handshake"]:
            return json_response(
                {
                    "protocolVersion": PROTOCOL_VERSION,
                    "moduleId": self.module_id,
                    "toolPrefix": self.tool_prefix,
                    "runtime": self.runtime,
                    "runtimeVersion": self.runtime_version,
                    "capabilities": self.capabilities,
                }
            )

        if method == "GET" and path == CONTROL_PATHS["discovery"]:
            return json_response(
                {
                    "endpoints": [
                        _endpoint_descriptor(endpoint)
                        for endpoint in self.endpoints
                    ]
                }
            )

        if method == "GET" and path == CONTROL_PATHS["health"]:
            result = _run_handler(self.health, context)
            return json_response(result or {"isHealthy": True, "message": "ready"})

        if method == "POST" and path == CONTROL_PATHS["initialize"]:
            message = _run_handler(self.initialize, context)
            return json_response(
                {
                    "accepted": True,
                    "message": message if isinstance(message, str) else None,
                }
            )

        if method == "POST" and path == CONTROL_PATHS["shutdown"]:
            message = _run_handler(self.shutdown, context)
            threading.Thread(target=self.stop, daemon=True).start()
            return json_response(
                {
                    "accepted": True,
                    "message": message if isinstance(message, str) else None,
                }
            )

        return json_response({"error": "Unknown SharpClaw control route"}, status=404)

    def _handle_endpoint(
        self,
        method: str,
        path: str,
        headers: dict[str, str],
        query: dict[str, list[str]],
        body: bytes,
    ) -> Response:
        for endpoint in self.endpoints:
            if endpoint["method"] != method:
                continue

            params = _match_route(endpoint["routePattern"], path)
            if params is None:
                continue

            context = self._context(method, path, headers, query, params, body)
            handler = endpoint.get("handler")
            if handler is not None:
                return _coerce_response(_run_handler(handler, context))

            if self.asgi_app is not None:
                return _run_asgi_app(self.asgi_app, context)

            return json_response({"error": "Endpoint has no handler"}, status=500)

        return json_response({"error": "Endpoint not found"}, status=404)

    def _context(
        self,
        method: str,
        path: str,
        headers: dict[str, str],
        query: dict[str, list[str]],
        params: dict[str, str],
        body: bytes,
    ) -> RequestContext:
        return RequestContext(
            method=method,
            path=path,
            query=query,
            headers=headers,
            params=params,
            body=body,
            environ={
                "module_directory": os.getenv(ENV["module_directory"]),
                "module_data_directory": os.getenv(ENV["module_data_directory"]),
                "module_id": os.getenv(ENV["module_id"]),
                "runtime": os.getenv(ENV["module_runtime"]),
            },
        )


class SharpClawRequestHandler(BaseHTTPRequestHandler):
    sharpclaw_host: SharpClawHost

    def do_GET(self) -> None:
        self._handle()

    def do_POST(self) -> None:
        self._handle()

    def do_PUT(self) -> None:
        self._handle()

    def do_PATCH(self) -> None:
        self._handle()

    def do_DELETE(self) -> None:
        self._handle()

    def log_message(self, format: str, *args: Any) -> None:
        return

    def _handle(self) -> None:
        parsed = urlparse(self.path)
        body = self.rfile.read(int(self.headers.get("Content-Length", "0") or "0"))
        response = self.sharpclaw_host.handle(
            self,
            self.command.upper(),
            parsed.path,
            parse_qs(parsed.query),
            body,
        )
        self._write(response)

    def _write(self, response: Response) -> None:
        body = response.body.encode("utf-8") if isinstance(response.body, str) else response.body
        self.send_response(response.status)
        for key, value in response.headers.items():
            self.send_header(key, value)

        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def create_sharpclaw_host(**kwargs: Any) -> SharpClawHost:
    return SharpClawHost(**kwargs)


def json_response(value: Any, status: int = 200, headers: dict[str, str] | None = None) -> Response:
    body = json.dumps(value, separators=(",", ":"))
    return Response(
        body=body,
        status=status,
        headers={
            "Content-Type": "application/json; charset=utf-8",
            **(headers or {}),
        },
    )


def text_response(value: str, status: int = 200, headers: dict[str, str] | None = None) -> Response:
    return Response(
        body=value,
        status=status,
        headers={
            "Content-Type": "text/plain; charset=utf-8",
            **(headers or {}),
        },
    )


def _normalize_endpoint(endpoint: dict[str, Any]) -> dict[str, Any]:
    route_pattern = endpoint.get("routePattern") or endpoint.get("route_pattern")
    if not route_pattern or not str(route_pattern).startswith("/"):
        raise ValueError(f"Invalid SharpClaw route pattern '{route_pattern}'.")

    response_mode = endpoint.get("responseMode") or endpoint.get("response_mode") or "json"
    normalized = dict(endpoint)
    normalized["method"] = str(endpoint.get("method") or "GET").upper()
    normalized["routePattern"] = str(route_pattern)
    normalized["responseMode"] = str(response_mode)
    return normalized


def _endpoint_descriptor(endpoint: dict[str, Any]) -> dict[str, Any]:
    return {
        "method": endpoint["method"],
        "routePattern": endpoint["routePattern"],
        "responseMode": endpoint["responseMode"],
        "authPolicy": endpoint.get("authPolicy") or endpoint.get("auth_policy"),
        "permission": endpoint.get("permission"),
        "contributionId": endpoint.get("contributionId") or endpoint.get("contribution_id"),
        "metadata": endpoint.get("metadata"),
    }


def _match_route(route_pattern: str, path: str) -> dict[str, str] | None:
    pattern_segments = [part for part in route_pattern.split("/") if part]
    path_segments = [part for part in path.split("/") if part]
    params: dict[str, str] = {}

    for index, pattern in enumerate(pattern_segments):
        if pattern.startswith("{**") and pattern.endswith("}"):
            params[pattern[3:-1]] = "/".join(path_segments[index:])
            return params

        if index >= len(path_segments):
            return None

        value = path_segments[index]
        if pattern.startswith("{") and pattern.endswith("}"):
            params[pattern[1:-1]] = value
            continue

        if pattern != value:
            return None

    return params if len(path_segments) == len(pattern_segments) else None


def _run_handler(handler: Handler, context: RequestContext) -> Any:
    result = handler(context)
    if inspect.isawaitable(result):
        return asyncio.run(result)

    return result


def _coerce_response(value: Any) -> Response:
    if isinstance(value, Response):
        return value

    if value is None:
        return Response(status=204)

    if isinstance(value, bytes | str):
        return Response(value)

    return json_response(value)


def _run_asgi_app(app: Callable[..., Awaitable[None]], context: RequestContext) -> Response:
    async def receive() -> dict[str, Any]:
        return {
            "type": "http.request",
            "body": context.body,
            "more_body": False,
        }

    messages: list[dict[str, Any]] = []

    async def send(message: dict[str, Any]) -> None:
        messages.append(message)

    scope = {
        "type": "http",
        "asgi": {"version": "3.0", "spec_version": "2.3"},
        "http_version": "1.1",
        "method": context.method,
        "scheme": "http",
        "path": context.path,
        "raw_path": context.path.encode("utf-8"),
        "query_string": b"",
        "headers": [
            (key.lower().encode("latin-1"), value.encode("latin-1"))
            for key, value in context.headers.items()
        ],
        "client": None,
        "server": None,
    }

    asyncio.run(app(scope, receive, send))
    status = 200
    headers: dict[str, str] = {}
    body = b""

    for message in messages:
        if message["type"] == "http.response.start":
            status = message.get("status", 200)
            headers = {
                key.decode("latin-1"): value.decode("latin-1")
                for key, value in message.get("headers", [])
            }
        elif message["type"] == "http.response.body":
            body += message.get("body", b"")

    return Response(body=body, status=status, headers=headers)


def _read_required_env(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"Missing required environment variable '{name}'.")

    return value


def _noop(_: RequestContext) -> None:
    return None
