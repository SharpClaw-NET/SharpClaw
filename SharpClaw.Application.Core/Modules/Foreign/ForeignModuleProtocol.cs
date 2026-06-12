namespace SharpClaw.Application.Core.Modules.Foreign;

public static class ForeignModuleProtocol
{
    public const int Version = 1;
    public const string TokenHeaderName = "X-SharpClaw-Control-Token";

    public const string ModuleDirectoryEnv = "SHARPCLAW_MODULE_DIR";
    public const string ModuleDataDirectoryEnv = "SHARPCLAW_MODULE_DATA_DIR";
    public const string ControlAddressEnv = "SHARPCLAW_CONTROL_ADDRESS";
    public const string ControlTokenEnv = "SHARPCLAW_CONTROL_TOKEN";
    public const string ModuleIdEnv = "SHARPCLAW_MODULE_ID";
    public const string ModuleRuntimeEnv = "SHARPCLAW_MODULE_RUNTIME";

    public const string HandshakePath = "/.sharpclaw/handshake";
    public const string HealthPath = "/.sharpclaw/health";
    public const string InitializePath = "/.sharpclaw/initialize";
    public const string ShutdownPath = "/.sharpclaw/shutdown";
    public const string DiscoveryPath = "/.sharpclaw/discovery";
    public const string ToolExecutePath = "/.sharpclaw/tools/execute";
    public const string ToolCompletionBehaviorPath = "/.sharpclaw/tools/completion-behavior";
    public const string ToolStreamPath = "/.sharpclaw/tools/stream";
    public const string InlineToolExecutePath = "/.sharpclaw/inline-tools/execute";
    public const string ContractInvokePath = "/.sharpclaw/contracts/invoke";
    public const string HeaderTagResolvePath = "/.sharpclaw/header-tags/resolve";
    public const string ResourceIdsPath = "/.sharpclaw/resources/ids";
    public const string ResourceLookupPath = "/.sharpclaw/resources/lookup";
    public const string CliExecutePath = "/.sharpclaw/cli/execute";
    public const string TaskStepExecutePath = "/.sharpclaw/tasks/steps/execute";
    public const string TaskStepInvokePath = "/.sharpclaw/tasks/steps/invoke";
    public const string TaskTriggerAttributeHandlePath = "/.sharpclaw/tasks/triggers/attributes/handle";
    public const string TaskTriggerStartPath = "/.sharpclaw/tasks/triggers/start";
    public const string TaskTriggerStopPath = "/.sharpclaw/tasks/triggers/stop";
    public const string TaskTriggerBindingValuePath = "/.sharpclaw/tasks/triggers/binding-value";
    public const string TaskTriggerBindingFilterPath = "/.sharpclaw/tasks/triggers/binding-filter";
    public const string TaskTriggerSyncBindingsPath = "/.sharpclaw/tasks/triggers/sync-bindings";
    public const string TaskTriggerRemoveBindingsPath = "/.sharpclaw/tasks/triggers/remove-bindings";
    public const string TaskTriggerBindingCreatedPath = "/.sharpclaw/tasks/triggers/bindings/created";
    public const string TaskTriggerBindingRemovedPath = "/.sharpclaw/tasks/triggers/bindings/removed";
    public const string TaskMetricValuePath = "/.sharpclaw/tasks/metrics/value";
    public const string TaskEventSinkPath = "/.sharpclaw/tasks/events/sink";
    public const string ProviderModelsListPath = "/.sharpclaw/providers/models/list";
    public const string ProviderCapabilitiesResolvePath = "/.sharpclaw/providers/capabilities/resolve";
    public const string ProviderChatCompletionPath = "/.sharpclaw/providers/chat/complete";
    public const string ProviderChatCompletionWithToolsPath = "/.sharpclaw/providers/chat/complete-tools";
    public const string ProviderStreamChatCompletionWithToolsPath = "/.sharpclaw/providers/chat/stream-tools";
    public const string ProviderDeviceCodeStartPath = "/.sharpclaw/providers/device-code/start";
    public const string ProviderDeviceCodePollPath = "/.sharpclaw/providers/device-code/poll";
    public const string ProviderCostFeedPath = "/.sharpclaw/providers/costs";
    public const string ProviderAgentIdentifierSuffixPath = "/.sharpclaw/providers/agent-identifier-suffix";
}

internal static class ForeignModuleCapability
{
    public const string Endpoints = "endpoints";
    public const string JobTools = "jobTools";
    public const string InlineTools = "inlineTools";
    public const string StreamingTools = "streamingTools";
    public const string FrontendContributions = "frontendContributions";
    public const string ModuleContributionDescriptors = "moduleContributionDescriptors";
    public const string LifecycleHooks = "lifecycleHooks";
    public const string HostCapabilities = "hostCapabilities";
    public const string TaskRuntime = "taskRuntime";
    public const string ProviderPlugins = "providerPlugins";
}

public static class ForeignModuleEndpointResponseMode
{
    public const string Json = "json";
    public const string Stream = "stream";
    public const string Static = "static";
    public const string Raw = "raw";
    public const string WebSocket = "websocket";
}

public static class ForeignModuleHostCapabilityProtocol
{
    public const string AddressEnv = "SHARPCLAW_HOST_CAPABILITIES_ADDRESS";
    public const string TokenEnv = "SHARPCLAW_HOST_CAPABILITIES_TOKEN";

    public const string ConfigGetPath = "/.sharpclaw/host/config/get";
    public const string ConfigSetPath = "/.sharpclaw/host/config/set";
    public const string ConfigAllPath = "/.sharpclaw/host/config/all";
    public const string LogPath = "/.sharpclaw/host/log";
    public const string JobLogPath = "/.sharpclaw/host/job/log";
    public const string JobCompletePath = "/.sharpclaw/host/job/complete";
    public const string JobFailPath = "/.sharpclaw/host/job/fail";
    public const string JobCancelPath = "/.sharpclaw/host/job/cancel";
    public const string JobCancelStaleByActionPrefixPath = "/.sharpclaw/host/job/cancel-stale-by-action-prefix";
    public const string JobGetPath = "/.sharpclaw/host/job/get";
    public const string JobListByActionPrefixPath = "/.sharpclaw/host/job/list-by-action-prefix";
    public const string JobListSummariesByActionPrefixPath = "/.sharpclaw/host/job/list-summaries-by-action-prefix";
    public const string JobExistsWithActionPrefixPath = "/.sharpclaw/host/job/exists-with-action-prefix";
    public const string ProtocolContractsListPath = "/.sharpclaw/host/contracts/list";
    public const string ProtocolContractInvokePath = "/.sharpclaw/host/contracts/invoke";
    public const string TaskValidatePath = "/.sharpclaw/host/tasks/validate";
    public const string TaskCreatePath = "/.sharpclaw/host/tasks/create";
    public const string TaskGetPath = "/.sharpclaw/host/tasks/get";
    public const string TaskListPath = "/.sharpclaw/host/tasks/list";
    public const string TaskUpdatePath = "/.sharpclaw/host/tasks/update";
    public const string TaskDeletePath = "/.sharpclaw/host/tasks/delete";
    public const string TaskLaunchPath = "/.sharpclaw/host/tasks/launch";
    public const string TaskContextExecuteStepsPath = "/.sharpclaw/host/tasks/context/execute-steps";
    public const string TaskContextExecuteEventHandlerPath = "/.sharpclaw/host/tasks/context/event-handler/execute";
    public const string CoreAgentIdsPath = "/.sharpclaw/host/core/agents/ids";
    public const string CoreChannelIdsPath = "/.sharpclaw/host/core/channels/ids";
    public const string CoreAgentLookupPath = "/.sharpclaw/host/core/agents/lookup";
    public const string CoreChannelLookupPath = "/.sharpclaw/host/core/channels/lookup";
    public const string ContextAccessibleThreadsPath = "/.sharpclaw/host/context/threads/accessible";
    public const string ContextThreadMessagesPath = "/.sharpclaw/host/context/threads/messages";
    public const string ConversationSteerPath = "/.sharpclaw/host/conversation/steer";
    public const string ConversationSteeringListPath = "/.sharpclaw/host/conversation/steering/list";
    public const string QueueMetricsPath = "/.sharpclaw/host/metrics/queue";
    public const string HostAgentChatPath = "/.sharpclaw/host/agent-bridge/chat";
    public const string HostAgentChatStreamPath = "/.sharpclaw/host/agent-bridge/chat-stream";
    public const string HostAgentChatToThreadPath = "/.sharpclaw/host/agent-bridge/chat-to-thread";
    public const string HostAgentParseStructuredResponsePath = "/.sharpclaw/host/agent-bridge/parse-structured-response";
    public const string HostAgentFindModelPath = "/.sharpclaw/host/agent-bridge/find-model";
    public const string HostAgentFindProviderPath = "/.sharpclaw/host/agent-bridge/find-provider";
    public const string HostAgentFindAgentPath = "/.sharpclaw/host/agent-bridge/find-agent";
    public const string HostAgentFindRolePath = "/.sharpclaw/host/agent-bridge/find-role";
    public const string HostAgentFindChannelPath = "/.sharpclaw/host/agent-bridge/find-channel";
    public const string HostAgentCreateAgentPath = "/.sharpclaw/host/agent-bridge/create-agent";
    public const string HostAgentCreateThreadPath = "/.sharpclaw/host/agent-bridge/create-thread";
    public const string HostAgentCreateRolePath = "/.sharpclaw/host/agent-bridge/create-role";
    public const string HostAgentSetRolePermissionsPath = "/.sharpclaw/host/agent-bridge/set-role-permissions";
    public const string HostAgentAssignRolePath = "/.sharpclaw/host/agent-bridge/assign-role";
    public const string HostAgentCreateChannelPath = "/.sharpclaw/host/agent-bridge/create-channel";
    public const string HostAgentAddAllowedAgentPath = "/.sharpclaw/host/agent-bridge/add-allowed-agent";
    public const string AgentCreateSubAgentPath = "/.sharpclaw/host/agents/create-sub-agent";
    public const string AgentUpdatePath = "/.sharpclaw/host/agents/update";
    public const string AgentSetHeaderPath = "/.sharpclaw/host/agents/set-header";
    public const string ChannelSetHeaderPath = "/.sharpclaw/host/channels/set-header";
    public const string ModelEnsureProviderPath = "/.sharpclaw/host/models/ensure-provider";
    public const string ModelEnsureModelPath = "/.sharpclaw/host/models/ensure-model";
    public const string ModelProviderInfoPath = "/.sharpclaw/host/models/provider-info";
    public const string ModelLocalFilePathPath = "/.sharpclaw/host/models/local-file-path";
    public const string ModelMetadataPath = "/.sharpclaw/host/models/metadata";
    public const string ModelDeletePath = "/.sharpclaw/host/models/delete";
    public const string ModulesExternalRootPath = "/.sharpclaw/host/modules/external-root";
    public const string ModulesInfoListPath = "/.sharpclaw/host/modules/info/list";
    public const string ModuleRegisteredPath = "/.sharpclaw/host/modules/registered";
    public const string ModuleToolPrefixRegisteredPath = "/.sharpclaw/host/modules/tool-prefix-registered";
    public const string ModuleLoadPath = "/.sharpclaw/host/modules/load";
    public const string ModuleUnloadPath = "/.sharpclaw/host/modules/unload";
    public const string ModuleReloadPath = "/.sharpclaw/host/modules/reload";
    public const string ModuleToolInvokePath = "/.sharpclaw/host/modules/tools/invoke";
    public const string ModuleStorageListPath = "/.sharpclaw/host/modules/storage/list";
    public const string ModuleStorageInvokePath = "/.sharpclaw/host/modules/storage/invoke";
}
