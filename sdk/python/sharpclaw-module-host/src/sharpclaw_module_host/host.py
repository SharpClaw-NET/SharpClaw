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
from urllib.error import HTTPError
from urllib.parse import parse_qs, urljoin, urlparse
from urllib.request import Request as UrlRequest
from urllib.request import urlopen

PROTOCOL_VERSION = 1
TOKEN_HEADER_NAME = "X-SharpClaw-Control-Token"

ENV = {
    "module_directory": "SHARPCLAW_MODULE_DIR",
    "module_data_directory": "SHARPCLAW_MODULE_DATA_DIR",
    "control_address": "SHARPCLAW_CONTROL_ADDRESS",
    "control_token": "SHARPCLAW_CONTROL_TOKEN",
    "module_id": "SHARPCLAW_MODULE_ID",
    "module_runtime": "SHARPCLAW_MODULE_RUNTIME",
    "host_capabilities_address": "SHARPCLAW_HOST_CAPABILITIES_ADDRESS",
    "host_capabilities_token": "SHARPCLAW_HOST_CAPABILITIES_TOKEN",
}

CONTROL_PATHS = {
    "handshake": "/.sharpclaw/handshake",
    "discovery": "/.sharpclaw/discovery",
    "health": "/.sharpclaw/health",
    "initialize": "/.sharpclaw/initialize",
    "shutdown": "/.sharpclaw/shutdown",
    "tool_execute": "/.sharpclaw/tools/execute",
    "tool_stream": "/.sharpclaw/tools/stream",
    "inline_tool_execute": "/.sharpclaw/inline-tools/execute",
}

HOST_CAPABILITY_PATHS = {
    "config_get": "/.sharpclaw/host/config/get",
    "config_set": "/.sharpclaw/host/config/set",
    "config_all": "/.sharpclaw/host/config/all",
    "log": "/.sharpclaw/host/log",
    "job_log": "/.sharpclaw/host/job/log",
    "job_complete": "/.sharpclaw/host/job/complete",
    "job_fail": "/.sharpclaw/host/job/fail",
    "job_cancel": "/.sharpclaw/host/job/cancel",
}

Handler = Callable[["RequestContext"], Any | Awaitable[Any]]
ToolHandler = Callable[["ToolContext"], Any | Awaitable[Any]]
InlineToolHandler = Callable[["InlineToolExecutionContext"], Any | Awaitable[Any]]


class HostCapabilitiesClient:
    def __init__(self, *, address: str, token: str) -> None:
        self.address = address.rstrip("/") + "/"
        self.token = token

    def get_config(self, key: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["config_get"], {"key": key}).get("value")

    def set_config(self, key: str, value: str | None) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["config_set"], {"key": key, "value": value})

    def get_all_config(self) -> dict[str, str]:
        return self._post_json(HOST_CAPABILITY_PATHS["config_all"], {}).get("values", {})

    def log(self, message: str, level: str = "Info") -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["log"], {"message": message, "level": level})

    def add_job_log(self, job_id: str, message: str, level: str = "Info") -> dict[str, Any]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_log"],
            {"jobId": job_id, "message": message, "level": level},
        )

    def complete_job(
        self,
        job_id: str,
        result_data: str | None = None,
        message: str | None = None,
    ) -> dict[str, Any]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_complete"],
            {"jobId": job_id, "resultData": result_data, "message": message},
        )

    def fail_job(
        self,
        job_id: str,
        message: str,
        details: str | None = None,
    ) -> dict[str, Any]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_fail"],
            {"jobId": job_id, "message": message, "details": details},
        )

    def cancel_job(self, job_id: str, message: str | None = None) -> dict[str, Any]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_cancel"],
            {"jobId": job_id, "message": message},
        )

    def _post_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        request = UrlRequest(
            urljoin(self.address, path.lstrip("/")),
            data=body,
            method="POST",
            headers={
                "Content-Type": "application/json",
                TOKEN_HEADER_NAME: self.token,
            },
        )

        try:
            with urlopen(request) as response:
                raw = response.read()
        except HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="replace")
            raise RuntimeError(
                f"SharpClaw host capability call failed: {ex.code} {detail}"
            ) from ex

        return json.loads(raw.decode("utf-8") or "{}")


def create_host_capabilities_client(
    *,
    address: str | None = None,
    token: str | None = None,
) -> HostCapabilitiesClient | None:
    resolved_address = address or os.getenv(ENV["host_capabilities_address"])
    resolved_token = token or os.getenv(ENV["host_capabilities_token"])
    if not resolved_address or not resolved_token:
        return None

    return HostCapabilitiesClient(address=resolved_address, token=resolved_token)


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
    host_capabilities: HostCapabilitiesClient | None = None

    def read_text(self) -> str:
        return self.body.decode("utf-8")

    def read_json(self) -> Any:
        return json.loads(self.read_text() or "null")


@dataclass(slots=True)
class ToolContext:
    tool_name: str
    parameters: dict[str, Any]
    job: dict[str, Any]
    host_capabilities: HostCapabilitiesClient | None = None


@dataclass(slots=True)
class InlineToolExecutionContext:
    tool_name: str
    parameters: dict[str, Any]
    context: dict[str, Any]
    host_capabilities: HostCapabilitiesClient | None = None


class SharpClawHost:
    def __init__(
        self,
        *,
        module_id: str,
        tool_prefix: str,
        endpoints: list[dict[str, Any]] | None = None,
        tools: list[dict[str, Any]] | None = None,
        inline_tools: list[dict[str, Any]] | None = None,
        initialize: Handler | None = None,
        shutdown: Handler | None = None,
        health: Handler | None = None,
        asgi_app: Callable[..., Awaitable[None]] | None = None,
        capabilities: list[str] | None = None,
        host_capabilities: HostCapabilitiesClient | None = None,
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
        self.tools = [_normalize_tool(tool) for tool in tools or []]
        self.inline_tools = [_normalize_tool(tool) for tool in inline_tools or []]
        self.initialize = initialize or _noop
        self.shutdown = shutdown or _noop
        self.health = health or (lambda _: {"isHealthy": True, "message": "ready"})
        self.asgi_app = asgi_app
        self.capabilities = capabilities or ["endpoints", "lifecycleHooks"]
        self.host_capabilities = host_capabilities or create_host_capabilities_client()
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
                    ],
                    "tools": [
                        _tool_descriptor(tool)
                        for tool in self.tools
                    ],
                    "inlineTools": [
                        _tool_descriptor(tool)
                        for tool in self.inline_tools
                    ],
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

        if method == "POST" and path == CONTROL_PATHS["tool_execute"]:
            return self._execute_tool(body, inline=False)

        if method == "POST" and path == CONTROL_PATHS["inline_tool_execute"]:
            return self._execute_tool(body, inline=True)

        if method == "POST" and path == CONTROL_PATHS["tool_stream"]:
            return self._stream_tool(body)

        return json_response({"error": "Unknown SharpClaw control route"}, status=404)

    def _execute_tool(self, body: bytes, *, inline: bool) -> Response:
        payload = json.loads(body.decode("utf-8") or "{}")
        tool_name = payload.get("toolName")
        candidates = self.inline_tools if inline else self.tools
        tool = next((candidate for candidate in candidates if candidate["name"] == tool_name), None)
        if tool is None:
            return json_response({"error": f"Tool '{tool_name}' not found"}, status=404)

        handler = tool.get("handler")
        if handler is None:
            return json_response({"error": f"Tool '{tool_name}' has no handler"}, status=500)

        context = (
            InlineToolExecutionContext(
                tool_name=tool_name,
                parameters=payload.get("parameters") or {},
                context=payload.get("context") or {},
                host_capabilities=self.host_capabilities,
            )
            if inline
            else ToolContext(
                tool_name=tool_name,
                parameters=payload.get("parameters") or {},
                job=payload.get("job") or {},
                host_capabilities=self.host_capabilities,
            )
        )
        result = _run_handler(handler, context)
        if isinstance(result, dict) and "result" in result:
            return json_response(result)

        return json_response(
            {
                "result": "" if result is None else str(result),
                "completionBehavior": tool.get("completionBehavior"),
            }
        )

    def _stream_tool(self, body: bytes) -> Response:
        payload = json.loads(body.decode("utf-8") or "{}")
        tool_name = payload.get("toolName")
        tool = next((candidate for candidate in self.tools if candidate["name"] == tool_name), None)
        if tool is None:
            return json_response({"error": f"Tool '{tool_name}' not found"}, status=404)

        if not tool.get("supportsStreaming"):
            return json_response({"error": f"Tool '{tool_name}' is not streaming"}, status=404)

        handler = tool.get("handler")
        if handler is None:
            return json_response({"error": f"Tool '{tool_name}' has no handler"}, status=500)

        context = ToolContext(
            tool_name=tool_name,
            parameters=payload.get("parameters") or {},
            job=payload.get("job") or {},
            host_capabilities=self.host_capabilities,
        )
        result = _run_handler(handler, context)
        chunks = _collect_stream_chunks(result)
        lines = "".join(json.dumps({"delta": str(chunk)}) + "\n" for chunk in chunks)
        lines += json.dumps({"isFinal": True}) + "\n"
        return Response(
            body=lines,
            status=200,
            headers={"Content-Type": "application/x-ndjson; charset=utf-8"},
        )

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
            host_capabilities=self.host_capabilities,
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


def _normalize_tool(tool: dict[str, Any]) -> dict[str, Any]:
    name = tool.get("name")
    if not name:
        raise ValueError("SharpClaw tool descriptors must include name.")

    handler = tool.get("handler")
    if handler is not None and not callable(handler):
        raise ValueError(f"SharpClaw tool '{name}' handler is not callable.")

    normalized = dict(tool)
    normalized["name"] = str(name)
    normalized["description"] = str(tool.get("description") or "")
    normalized["parametersSchema"] = (
        tool.get("parametersSchema")
        or tool.get("parameters_schema")
        or {"type": "object", "properties": {}}
    )
    normalized["completionBehavior"] = (
        tool.get("completionBehavior")
        or tool.get("completion_behavior")
        or "CompleteWhenExecutionReturns"
    )
    normalized["supportsStreaming"] = bool(
        tool.get("supportsStreaming")
        or tool.get("supports_streaming")
        or False
    )
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


def _tool_descriptor(tool: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": tool["name"],
        "description": tool["description"],
        "parametersSchema": tool["parametersSchema"],
        "permission": tool.get("permission"),
        "timeoutSeconds": tool.get("timeoutSeconds") or tool.get("timeout_seconds"),
        "aliases": tool.get("aliases"),
        "supportsStreaming": tool.get("supportsStreaming", False),
        "completionBehavior": tool.get("completionBehavior"),
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


def _collect_stream_chunks(value: Any) -> list[Any]:
    if value is None:
        return []

    if inspect.isasyncgen(value):
        async def collect() -> list[Any]:
            return [item async for item in value]

        return asyncio.run(collect())

    if isinstance(value, str | bytes):
        return [value]

    try:
        return list(value)
    except TypeError:
        return [value]


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
