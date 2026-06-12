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
    "contract_invoke": "/.sharpclaw/contracts/invoke",
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
    "job_cancel_stale_by_action_prefix": "/.sharpclaw/host/job/cancel-stale-by-action-prefix",
    "job_get": "/.sharpclaw/host/job/get",
    "job_list_by_action_prefix": "/.sharpclaw/host/job/list-by-action-prefix",
    "job_list_summaries_by_action_prefix": "/.sharpclaw/host/job/list-summaries-by-action-prefix",
    "job_exists_with_action_prefix": "/.sharpclaw/host/job/exists-with-action-prefix",
    "contracts_list": "/.sharpclaw/host/contracts/list",
    "contract_invoke": "/.sharpclaw/host/contracts/invoke",
    "task_validate": "/.sharpclaw/host/tasks/validate",
    "task_create": "/.sharpclaw/host/tasks/create",
    "task_get": "/.sharpclaw/host/tasks/get",
    "task_list": "/.sharpclaw/host/tasks/list",
    "task_update": "/.sharpclaw/host/tasks/update",
    "task_delete": "/.sharpclaw/host/tasks/delete",
    "task_launch": "/.sharpclaw/host/tasks/launch",
    "task_context_execute_steps": "/.sharpclaw/host/tasks/context/execute-steps",
    "task_context_execute_event_handler": "/.sharpclaw/host/tasks/context/event-handler/execute",
    "core_agent_ids": "/.sharpclaw/host/core/agents/ids",
    "core_channel_ids": "/.sharpclaw/host/core/channels/ids",
    "core_agent_lookup": "/.sharpclaw/host/core/agents/lookup",
    "core_channel_lookup": "/.sharpclaw/host/core/channels/lookup",
    "context_accessible_threads": "/.sharpclaw/host/context/threads/accessible",
    "context_thread_messages": "/.sharpclaw/host/context/threads/messages",
    "conversation_steer": "/.sharpclaw/host/conversation/steer",
    "conversation_steering_list": "/.sharpclaw/host/conversation/steering/list",
    "queue_metrics": "/.sharpclaw/host/metrics/queue",
    "host_agent_chat": "/.sharpclaw/host/agent-bridge/chat",
    "host_agent_chat_stream": "/.sharpclaw/host/agent-bridge/chat-stream",
    "host_agent_chat_to_thread": "/.sharpclaw/host/agent-bridge/chat-to-thread",
    "host_agent_parse_structured_response": "/.sharpclaw/host/agent-bridge/parse-structured-response",
    "host_agent_find_model": "/.sharpclaw/host/agent-bridge/find-model",
    "host_agent_find_provider": "/.sharpclaw/host/agent-bridge/find-provider",
    "host_agent_find_agent": "/.sharpclaw/host/agent-bridge/find-agent",
    "host_agent_find_role": "/.sharpclaw/host/agent-bridge/find-role",
    "host_agent_find_channel": "/.sharpclaw/host/agent-bridge/find-channel",
    "host_agent_create_agent": "/.sharpclaw/host/agent-bridge/create-agent",
    "host_agent_create_thread": "/.sharpclaw/host/agent-bridge/create-thread",
    "host_agent_create_role": "/.sharpclaw/host/agent-bridge/create-role",
    "host_agent_set_role_permissions": "/.sharpclaw/host/agent-bridge/set-role-permissions",
    "host_agent_assign_role": "/.sharpclaw/host/agent-bridge/assign-role",
    "host_agent_create_channel": "/.sharpclaw/host/agent-bridge/create-channel",
    "host_agent_add_allowed_agent": "/.sharpclaw/host/agent-bridge/add-allowed-agent",
    "agent_create_sub_agent": "/.sharpclaw/host/agents/create-sub-agent",
    "agent_update": "/.sharpclaw/host/agents/update",
    "agent_set_header": "/.sharpclaw/host/agents/set-header",
    "channel_set_header": "/.sharpclaw/host/channels/set-header",
    "model_ensure_provider": "/.sharpclaw/host/models/ensure-provider",
    "model_ensure_model": "/.sharpclaw/host/models/ensure-model",
    "model_provider_info": "/.sharpclaw/host/models/provider-info",
    "model_local_file_path": "/.sharpclaw/host/models/local-file-path",
    "model_metadata": "/.sharpclaw/host/models/metadata",
    "model_delete": "/.sharpclaw/host/models/delete",
    "modules_external_root": "/.sharpclaw/host/modules/external-root",
    "modules_info_list": "/.sharpclaw/host/modules/info/list",
    "module_registered": "/.sharpclaw/host/modules/registered",
    "module_tool_prefix_registered": "/.sharpclaw/host/modules/tool-prefix-registered",
    "module_load": "/.sharpclaw/host/modules/load",
    "module_unload": "/.sharpclaw/host/modules/unload",
    "module_reload": "/.sharpclaw/host/modules/reload",
    "module_tool_invoke": "/.sharpclaw/host/modules/tools/invoke",
    "module_storage_list": "/.sharpclaw/host/modules/storage/list",
    "module_storage_invoke": "/.sharpclaw/host/modules/storage/invoke",
}

STORAGE_OPERATIONS = {
    "get": "get",
    "upsert": "upsert",
    "batch_upsert": "batchUpsert",
    "delete": "delete",
    "batch_delete": "batchDelete",
    "list": "list",
    "query": "query",
    "claim": "claim",
}

STORAGE_COMPARISON_OPERATORS = {
    "equal_to": "equals",
    "less_than_or_equal": "lessThanOrEqual",
    "greater_than_or_equal": "greaterThanOrEqual",
}

STORAGE_SORT_DIRECTIONS = {
    "ascending": "asc",
    "descending": "desc",
}

Handler = Callable[["RequestContext"], Any | Awaitable[Any]]
ToolHandler = Callable[["ToolContext"], Any | Awaitable[Any]]
InlineToolHandler = Callable[["InlineToolExecutionContext"], Any | Awaitable[Any]]


class HostCapabilitiesClient:
    def __init__(self, *, address: str, token: str) -> None:
        self.address = address.rstrip("/") + "/"
        self.token = token

    def invoke(self, path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
        return self._post_json(path, payload or {})

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

    def cancel_stale_jobs_by_action_prefix(
        self,
        action_key_prefix: str,
        resource_id: str | None = None,
    ) -> dict[str, Any]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_cancel_stale_by_action_prefix"],
            {"actionKeyPrefix": action_key_prefix, "resourceId": resource_id},
        )

    def get_job(self, job_id: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["job_get"], {"id": job_id}).get("job")

    def list_jobs_by_action_prefix(
        self,
        action_key_prefix: str,
        resource_id: str | None = None,
    ) -> list[dict[str, Any]]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_list_by_action_prefix"],
            {"actionKeyPrefix": action_key_prefix, "resourceId": resource_id},
        ).get("jobs", [])

    def list_job_summaries_by_action_prefix(
        self,
        action_key_prefix: str,
        resource_id: str | None = None,
    ) -> list[dict[str, Any]]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["job_list_summaries_by_action_prefix"],
            {"actionKeyPrefix": action_key_prefix, "resourceId": resource_id},
        ).get("jobs", [])

    def job_exists_with_action_prefix(self, job_id: str, action_key_prefix: str) -> bool:
        return bool(
            self._post_json(
                HOST_CAPABILITY_PATHS["job_exists_with_action_prefix"],
                {"jobId": job_id, "actionKeyPrefix": action_key_prefix},
            ).get("value", False)
        )

    def list_protocol_contracts(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["contracts_list"], {}).get("contracts", [])

    def invoke_protocol_contract(
        self,
        contract_name: str,
        operation: str,
        parameters: dict[str, Any] | None = None,
    ) -> Any:
        return self._post_json(
            HOST_CAPABILITY_PATHS["contract_invoke"],
            {
                "contractName": contract_name,
                "operation": operation,
                "parameters": parameters or {},
            },
        ).get("result")

    def validate_task(self, source_text: str) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["task_validate"], {"sourceText": source_text})

    def create_task(self, source_text: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["task_create"], {"sourceText": source_text}).get("definition")

    def get_task(self, task_id: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["task_get"], {"id": task_id}).get("definition")

    def list_tasks(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["task_list"], {}).get("definitions", [])

    def update_task(self, task_id: str, **values: Any) -> dict[str, Any] | None:
        return self._post_json(
            HOST_CAPABILITY_PATHS["task_update"],
            {"id": task_id, **values},
        ).get("definition")

    def delete_task(self, task_id: str) -> bool:
        return bool(self._post_json(HOST_CAPABILITY_PATHS["task_delete"], {"id": task_id}).get("deleted", False))

    def launch_task(self, task_definition_id: str, **values: Any) -> str | None:
        return self._post_json(
            HOST_CAPABILITY_PATHS["task_launch"],
            {"taskDefinitionId": task_definition_id, **values},
        ).get("instanceId")

    def execute_task_context_steps(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["task_context_execute_steps"], parameters)

    def execute_task_context_event_handler(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["task_context_execute_event_handler"], parameters)

    def get_agent_ids(self) -> list[str]:
        return self._post_json(HOST_CAPABILITY_PATHS["core_agent_ids"], {}).get("ids", [])

    def get_channel_ids(self) -> list[str]:
        return self._post_json(HOST_CAPABILITY_PATHS["core_channel_ids"], {}).get("ids", [])

    def get_agent_lookup_items(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["core_agent_lookup"], {}).get("items", [])

    def get_channel_lookup_items(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["core_channel_lookup"], {}).get("items", [])

    def get_accessible_threads(self, parameters: dict[str, Any]) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["context_accessible_threads"], parameters).get("threads", [])

    def get_thread_messages(self, parameters: dict[str, Any]) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["context_thread_messages"], parameters).get("messages", [])

    def add_conversation_steering(
        self,
        channel_id: str,
        summary: str,
        thread_id: str | None = None,
        source: str | None = None,
        category: str | None = None,
        details: str | None = None,
        client_type: str | None = None,
    ) -> dict[str, Any] | None:
        return self._post_json(
            HOST_CAPABILITY_PATHS["conversation_steer"],
            {
                "channelId": channel_id,
                "threadId": thread_id,
                "summary": summary,
                "source": source,
                "category": category,
                "details": details,
                "clientType": client_type,
            },
        ).get("steering")

    def list_conversation_steering(
        self,
        channel_id: str,
        thread_id: str | None = None,
        limit: int = 20,
    ) -> list[dict[str, Any]]:
        return self._post_json(
            HOST_CAPABILITY_PATHS["conversation_steering_list"],
            {"channelId": channel_id, "threadId": thread_id, "limit": limit},
        ).get("steering", [])

    def get_queue_metrics(self) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["queue_metrics"], {})

    def host_agent_chat(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_chat"], parameters).get("text")

    def host_agent_chat_stream(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_chat_stream"], parameters).get("text")

    def host_agent_chat_to_thread(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_chat_to_thread"], parameters).get("text")

    def parse_structured_response(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(
            HOST_CAPABILITY_PATHS["host_agent_parse_structured_response"],
            parameters,
        ).get("text")

    def find_model(self, search: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_find_model"], {"search": search}).get("id")

    def find_provider(self, search: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_find_provider"], {"search": search}).get("id")

    def find_agent(self, search: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_find_agent"], {"search": search}).get("id")

    def find_role(self, search: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_find_role"], {"search": search}).get("id")

    def find_channel(self, search: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_find_channel"], {"search": search}).get("id")

    def create_agent(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_create_agent"], parameters).get("id")

    def create_thread(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_create_thread"], parameters).get("id")

    def create_role(self, role_name: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_create_role"], {"roleName": role_name}).get("id")

    def set_role_permissions(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_set_role_permissions"], parameters)

    def assign_role(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_assign_role"], parameters)

    def create_channel(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_create_channel"], parameters).get("id")

    def add_allowed_agent(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["host_agent_add_allowed_agent"], parameters)

    def create_sub_agent(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["agent_create_sub_agent"], parameters)

    def update_agent(self, parameters: dict[str, Any]) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["agent_update"], parameters)

    def set_agent_header(self, entity_id: str, header: str | None) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["agent_set_header"], {"id": entity_id, "header": header})

    def set_channel_header(self, entity_id: str, header: str | None) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["channel_set_header"], {"id": entity_id, "header": header})

    def ensure_provider(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["model_ensure_provider"], parameters).get("id")

    def ensure_model(self, parameters: dict[str, Any]) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["model_ensure_model"], parameters).get("id")

    def get_model_provider_info(self, model_id: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["model_provider_info"], {"modelId": model_id}).get("info")

    def get_local_model_file_path(self, model_id: str) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["model_local_file_path"], {"modelId": model_id}).get("path")

    def get_model_metadata(self, model_id: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["model_metadata"], {"modelId": model_id}).get("metadata")

    def delete_model(self, model_id: str) -> bool:
        return bool(self._post_json(HOST_CAPABILITY_PATHS["model_delete"], {"modelId": model_id}).get("value", False))

    def get_external_modules_root(self) -> str | None:
        return self._post_json(HOST_CAPABILITY_PATHS["modules_external_root"], {}).get("directory")

    def list_modules(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["modules_info_list"], {}).get("modules", [])

    def is_module_registered(self, module_id: str) -> bool:
        return bool(
            self._post_json(HOST_CAPABILITY_PATHS["module_registered"], {"moduleId": module_id})
            .get("isRegistered", False)
        )

    def is_tool_prefix_registered(self, tool_prefix: str) -> bool:
        return bool(
            self._post_json(HOST_CAPABILITY_PATHS["module_tool_prefix_registered"], {"toolPrefix": tool_prefix})
            .get("isRegistered", False)
        )

    def load_module(self, module_dir: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["module_load"], {"moduleDir": module_dir}).get("state")

    def unload_module(self, module_id: str) -> dict[str, Any]:
        return self._post_json(HOST_CAPABILITY_PATHS["module_unload"], {"moduleId": module_id})

    def reload_module(self, module_id: str) -> dict[str, Any] | None:
        return self._post_json(HOST_CAPABILITY_PATHS["module_reload"], {"moduleId": module_id}).get("state")

    def invoke_module_tool(
        self,
        tool_name: str,
        parameters: dict[str, Any] | None = None,
        timeout_seconds: int | None = None,
    ) -> str | None:
        return self._post_json(
            HOST_CAPABILITY_PATHS["module_tool_invoke"],
            {"toolName": tool_name, "parameters": parameters or {}, "timeoutSeconds": timeout_seconds},
        ).get("result")

    def list_storage_contracts(self) -> list[dict[str, Any]]:
        return self._post_json(HOST_CAPABILITY_PATHS["module_storage_list"], {}).get("contracts", [])

    def invoke_storage(
        self,
        storage_name: str,
        operation: str,
        parameters: dict[str, Any] | None = None,
    ) -> Any:
        return self._post_json(
            HOST_CAPABILITY_PATHS["module_storage_invoke"],
            {
                "storageName": storage_name,
                "operation": operation,
                "parameters": parameters or {},
            },
        ).get("result")

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


class ModuleDocumentStore:
    def __init__(self, host_capabilities: HostCapabilitiesClient, storage_name: str) -> None:
        if host_capabilities is None:
            raise ValueError("SharpClaw document stores require host storage capabilities.")

        self.host_capabilities = host_capabilities
        self.storage_name = storage_name

    def get(self, key: str) -> Any:
        response = self._invoke(STORAGE_OPERATIONS["get"], {"key": key})
        return response.get("value") if response.get("found") is True else None

    def list(self, **options: Any) -> list[Any]:
        return _record_values(self._invoke(STORAGE_OPERATIONS["list"], options))

    def upsert(self, key: str, value: Any, indexes: dict[str, Any] | None = None) -> dict[str, Any]:
        payload: dict[str, Any] = {"key": key, "value": value}
        if indexes is not None:
            payload["indexes"] = indexes
        return self._invoke(STORAGE_OPERATIONS["upsert"], payload)

    def upsert_many(self, records: list[dict[str, Any]]) -> int:
        response = self._invoke(STORAGE_OPERATIONS["batch_upsert"], {"records": records})
        return int(response.get("saved", 0))

    def delete(self, key: str) -> bool:
        response = self._invoke(STORAGE_OPERATIONS["delete"], {"key": key})
        return response.get("deleted") is True

    def delete_many(self, keys: list[str]) -> int:
        response = self._invoke(STORAGE_OPERATIONS["batch_delete"], {"keys": keys})
        return int(response.get("deleted", 0))

    def query(self) -> "ModuleDocumentQuery":
        return ModuleDocumentQuery(self)

    def claim(self) -> "ModuleDocumentClaim":
        return ModuleDocumentClaim(self)

    def _invoke(self, operation: str, parameters: dict[str, Any]) -> dict[str, Any]:
        result = self.host_capabilities.invoke_storage(
            self.storage_name,
            operation,
            parameters,
        )
        return result if isinstance(result, dict) else {}


class ModuleDocumentQuery:
    def __init__(self, store: ModuleDocumentStore) -> None:
        self.store = store
        self.filters: list[dict[str, Any]] = []
        self.order_by: dict[str, Any] | None = None
        self.limit: int | None = None

    def where_index(self, index_name: str) -> "ModuleDocumentIndexFilterBuilder":
        return ModuleDocumentIndexFilterBuilder(self, index_name)

    def order_by_index(self, index_name: str) -> "ModuleDocumentQuery":
        self.order_by = {"indexName": index_name, "direction": STORAGE_SORT_DIRECTIONS["ascending"]}
        return self

    def order_by_index_descending(self, index_name: str) -> "ModuleDocumentQuery":
        self.order_by = {"indexName": index_name, "direction": STORAGE_SORT_DIRECTIONS["descending"]}
        return self

    def take(self, limit: int) -> "ModuleDocumentQuery":
        self.limit = limit
        return self

    def to_list(self) -> list[Any]:
        return _record_values(self.store._invoke(STORAGE_OPERATIONS["query"], self._payload()))

    def _add_filter(self, index_name: str, operator: str, value: Any) -> "ModuleDocumentQuery":
        self.filters.append({"indexName": index_name, "operator": operator, "value": value})
        return self

    def _payload(self) -> dict[str, Any]:
        payload: dict[str, Any] = {"filters": self.filters}
        if self.order_by is not None:
            payload["orderBy"] = self.order_by
        if self.limit is not None:
            payload["limit"] = self.limit
        return payload


class ModuleDocumentClaim:
    def __init__(self, store: ModuleDocumentStore) -> None:
        self.store = store
        self.filters: list[dict[str, Any]] = []
        self.order_by: dict[str, Any] | None = None
        self.limit: int | None = None
        self.patch_value: dict[str, Any] | None = None
        self.indexes: dict[str, Any] | None = None

    def where_index(self, index_name: str) -> "ModuleDocumentIndexFilterBuilder":
        return ModuleDocumentIndexFilterBuilder(self, index_name)

    def order_by_index(self, index_name: str) -> "ModuleDocumentClaim":
        self.order_by = {"indexName": index_name, "direction": STORAGE_SORT_DIRECTIONS["ascending"]}
        return self

    def order_by_index_descending(self, index_name: str) -> "ModuleDocumentClaim":
        self.order_by = {"indexName": index_name, "direction": STORAGE_SORT_DIRECTIONS["descending"]}
        return self

    def take(self, limit: int) -> "ModuleDocumentClaim":
        self.limit = limit
        return self

    def patch(
        self,
        patch: dict[str, Any],
        indexes: dict[str, Any] | None = None,
    ) -> "ModuleDocumentClaim":
        self.patch_value = patch
        self.indexes = indexes
        return self

    def to_list(self) -> list[Any]:
        if self.patch_value is None:
            raise RuntimeError("SharpClaw storage claim requires a patch before execution.")

        payload: dict[str, Any] = {
            "filters": self.filters,
            "patch": self.patch_value,
        }
        if self.order_by is not None:
            payload["orderBy"] = self.order_by
        if self.limit is not None:
            payload["limit"] = self.limit
        if self.indexes is not None:
            payload["indexes"] = self.indexes
        return _record_values(self.store._invoke(STORAGE_OPERATIONS["claim"], payload))

    def _add_filter(self, index_name: str, operator: str, value: Any) -> "ModuleDocumentClaim":
        self.filters.append({"indexName": index_name, "operator": operator, "value": value})
        return self


class ModuleDocumentIndexFilterBuilder:
    def __init__(
        self,
        query: ModuleDocumentQuery | ModuleDocumentClaim,
        index_name: str,
    ) -> None:
        self.query = query
        self.index_name = index_name

    def equal_to(self, value: Any) -> ModuleDocumentQuery | ModuleDocumentClaim:
        return self.query._add_filter(
            self.index_name,
            STORAGE_COMPARISON_OPERATORS["equal_to"],
            value,
        )

    def less_than_or_equal(self, value: Any) -> ModuleDocumentQuery | ModuleDocumentClaim:
        return self.query._add_filter(
            self.index_name,
            STORAGE_COMPARISON_OPERATORS["less_than_or_equal"],
            value,
        )

    def greater_than_or_equal(self, value: Any) -> ModuleDocumentQuery | ModuleDocumentClaim:
        return self.query._add_filter(
            self.index_name,
            STORAGE_COMPARISON_OPERATORS["greater_than_or_equal"],
            value,
        )


def create_document_store(
    host_capabilities: HostCapabilitiesClient,
    storage_name: str,
) -> ModuleDocumentStore:
    return ModuleDocumentStore(host_capabilities, storage_name)


def _record_values(response: dict[str, Any]) -> list[Any]:
    return [
        record["value"]
        for record in response.get("records", [])
        if isinstance(record, dict) and "value" in record
    ]


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


@dataclass(slots=True)
class ProtocolContractContext:
    contract_name: str
    operation: str
    parameters: dict[str, Any]
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
        protocol_contracts: list[dict[str, Any]] | None = None,
        required_protocol_contracts: list[dict[str, Any]] | None = None,
        storage_contracts: list[dict[str, Any]] | None = None,
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
        self.protocol_contracts = [
            _normalize_protocol_contract(contract)
            for contract in protocol_contracts or []
        ]
        self.required_protocol_contracts = required_protocol_contracts or []
        self.storage_contracts = storage_contracts or []
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
                    "protocolContracts": [
                        _protocol_contract_descriptor(contract)
                        for contract in self.protocol_contracts
                    ],
                    "requiredProtocolContracts": self.required_protocol_contracts,
                    "storageContracts": self.storage_contracts,
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

        if method == "POST" and path == CONTROL_PATHS["contract_invoke"]:
            return self._invoke_protocol_contract(body)

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

    def _invoke_protocol_contract(self, body: bytes) -> Response:
        payload = json.loads(body.decode("utf-8") or "{}")
        contract_name = payload.get("contractName")
        operation = payload.get("operation")
        contract = next(
            (
                candidate
                for candidate in self.protocol_contracts
                if candidate["contractName"] == contract_name
            ),
            None,
        )
        if contract is None:
            return json_response({"error": f"Contract '{contract_name}' not found"}, status=404)

        handler = (contract.get("handlers") or {}).get(operation)
        if handler is None:
            return json_response(
                {"error": f"Contract '{contract_name}' operation '{operation}' not found"},
                status=404,
            )

        context = ProtocolContractContext(
            contract_name=contract_name,
            operation=operation,
            parameters=payload.get("parameters") or {},
            host_capabilities=self.host_capabilities,
        )
        return json_response({"result": _run_handler(handler, context)})

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


def _normalize_protocol_contract(contract: dict[str, Any]) -> dict[str, Any]:
    contract_name = contract.get("contractName") or contract.get("contract_name")
    if not contract_name:
        raise ValueError("SharpClaw protocol contracts must include contractName.")

    normalized = dict(contract)
    normalized["contractName"] = str(contract_name)
    normalized["schema"] = contract.get("schema") or {"type": "object", "properties": {}}
    normalized["operations"] = contract.get("operations") or []
    normalized["handlers"] = contract.get("handlers") or {}
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


def _protocol_contract_descriptor(contract: dict[str, Any]) -> dict[str, Any]:
    return {
        "contractName": contract["contractName"],
        "schema": contract["schema"],
        "operations": contract["operations"],
        "description": contract.get("description"),
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
