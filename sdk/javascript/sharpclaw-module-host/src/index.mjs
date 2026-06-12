import http from 'node:http';

export const protocolVersion = 1;

export const env = {
  moduleDirectory: 'SHARPCLAW_MODULE_DIR',
  moduleDataDirectory: 'SHARPCLAW_MODULE_DATA_DIR',
  controlAddress: 'SHARPCLAW_CONTROL_ADDRESS',
  controlToken: 'SHARPCLAW_CONTROL_TOKEN',
  moduleId: 'SHARPCLAW_MODULE_ID',
  moduleRuntime: 'SHARPCLAW_MODULE_RUNTIME',
  hostCapabilitiesAddress: 'SHARPCLAW_HOST_CAPABILITIES_ADDRESS',
  hostCapabilitiesToken: 'SHARPCLAW_HOST_CAPABILITIES_TOKEN'
};

export const controlPaths = {
  handshake: '/.sharpclaw/handshake',
  discovery: '/.sharpclaw/discovery',
  health: '/.sharpclaw/health',
  initialize: '/.sharpclaw/initialize',
  shutdown: '/.sharpclaw/shutdown',
  toolExecute: '/.sharpclaw/tools/execute',
  toolStream: '/.sharpclaw/tools/stream',
  inlineToolExecute: '/.sharpclaw/inline-tools/execute',
  contractInvoke: '/.sharpclaw/contracts/invoke'
};

export const tokenHeaderName = 'X-SharpClaw-Control-Token';

export const hostCapabilityPaths = {
  configGet: '/.sharpclaw/host/config/get',
  configSet: '/.sharpclaw/host/config/set',
  configAll: '/.sharpclaw/host/config/all',
  log: '/.sharpclaw/host/log',
  jobLog: '/.sharpclaw/host/job/log',
  jobComplete: '/.sharpclaw/host/job/complete',
  jobFail: '/.sharpclaw/host/job/fail',
  jobCancel: '/.sharpclaw/host/job/cancel',
  jobCancelStaleByActionPrefix: '/.sharpclaw/host/job/cancel-stale-by-action-prefix',
  jobGet: '/.sharpclaw/host/job/get',
  jobListByActionPrefix: '/.sharpclaw/host/job/list-by-action-prefix',
  jobListSummariesByActionPrefix: '/.sharpclaw/host/job/list-summaries-by-action-prefix',
  jobExistsWithActionPrefix: '/.sharpclaw/host/job/exists-with-action-prefix',
  contractsList: '/.sharpclaw/host/contracts/list',
  contractInvoke: '/.sharpclaw/host/contracts/invoke',
  taskValidate: '/.sharpclaw/host/tasks/validate',
  taskCreate: '/.sharpclaw/host/tasks/create',
  taskGet: '/.sharpclaw/host/tasks/get',
  taskList: '/.sharpclaw/host/tasks/list',
  taskUpdate: '/.sharpclaw/host/tasks/update',
  taskDelete: '/.sharpclaw/host/tasks/delete',
  taskLaunch: '/.sharpclaw/host/tasks/launch',
  taskContextExecuteSteps: '/.sharpclaw/host/tasks/context/execute-steps',
  taskContextExecuteEventHandler: '/.sharpclaw/host/tasks/context/event-handler/execute',
  coreAgentIds: '/.sharpclaw/host/core/agents/ids',
  coreChannelIds: '/.sharpclaw/host/core/channels/ids',
  coreAgentLookup: '/.sharpclaw/host/core/agents/lookup',
  coreChannelLookup: '/.sharpclaw/host/core/channels/lookup',
  contextAccessibleThreads: '/.sharpclaw/host/context/threads/accessible',
  contextThreadMessages: '/.sharpclaw/host/context/threads/messages',
  conversationSteer: '/.sharpclaw/host/conversation/steer',
  conversationSteeringList: '/.sharpclaw/host/conversation/steering/list',
  queueMetrics: '/.sharpclaw/host/metrics/queue',
  hostAgentChat: '/.sharpclaw/host/agent-bridge/chat',
  hostAgentChatStream: '/.sharpclaw/host/agent-bridge/chat-stream',
  hostAgentChatToThread: '/.sharpclaw/host/agent-bridge/chat-to-thread',
  hostAgentParseStructuredResponse: '/.sharpclaw/host/agent-bridge/parse-structured-response',
  hostAgentFindModel: '/.sharpclaw/host/agent-bridge/find-model',
  hostAgentFindProvider: '/.sharpclaw/host/agent-bridge/find-provider',
  hostAgentFindAgent: '/.sharpclaw/host/agent-bridge/find-agent',
  hostAgentFindRole: '/.sharpclaw/host/agent-bridge/find-role',
  hostAgentFindChannel: '/.sharpclaw/host/agent-bridge/find-channel',
  hostAgentCreateAgent: '/.sharpclaw/host/agent-bridge/create-agent',
  hostAgentCreateThread: '/.sharpclaw/host/agent-bridge/create-thread',
  hostAgentCreateRole: '/.sharpclaw/host/agent-bridge/create-role',
  hostAgentSetRolePermissions: '/.sharpclaw/host/agent-bridge/set-role-permissions',
  hostAgentAssignRole: '/.sharpclaw/host/agent-bridge/assign-role',
  hostAgentCreateChannel: '/.sharpclaw/host/agent-bridge/create-channel',
  hostAgentAddAllowedAgent: '/.sharpclaw/host/agent-bridge/add-allowed-agent',
  agentCreateSubAgent: '/.sharpclaw/host/agents/create-sub-agent',
  agentUpdate: '/.sharpclaw/host/agents/update',
  agentSetHeader: '/.sharpclaw/host/agents/set-header',
  channelSetHeader: '/.sharpclaw/host/channels/set-header',
  modelEnsureProvider: '/.sharpclaw/host/models/ensure-provider',
  modelEnsureModel: '/.sharpclaw/host/models/ensure-model',
  modelProviderInfo: '/.sharpclaw/host/models/provider-info',
  modelLocalFilePath: '/.sharpclaw/host/models/local-file-path',
  modelMetadata: '/.sharpclaw/host/models/metadata',
  modelDelete: '/.sharpclaw/host/models/delete',
  modulesExternalRoot: '/.sharpclaw/host/modules/external-root',
  modulesInfoList: '/.sharpclaw/host/modules/info/list',
  moduleRegistered: '/.sharpclaw/host/modules/registered',
  moduleToolPrefixRegistered: '/.sharpclaw/host/modules/tool-prefix-registered',
  moduleLoad: '/.sharpclaw/host/modules/load',
  moduleUnload: '/.sharpclaw/host/modules/unload',
  moduleReload: '/.sharpclaw/host/modules/reload',
  moduleToolInvoke: '/.sharpclaw/host/modules/tools/invoke',
  moduleStorageList: '/.sharpclaw/host/modules/storage/list',
  moduleStorageInvoke: '/.sharpclaw/host/modules/storage/invoke'
};

export const storageOperations = {
  get: 'get',
  upsert: 'upsert',
  batchUpsert: 'batchUpsert',
  delete: 'delete',
  batchDelete: 'batchDelete',
  list: 'list',
  query: 'query',
  claim: 'claim'
};

export const storageComparisonOperators = {
  equalTo: 'equals',
  lessThanOrEqual: 'lessThanOrEqual',
  greaterThanOrEqual: 'greaterThanOrEqual'
};

export const storageSortDirections = {
  ascending: 'asc',
  descending: 'desc'
};

export function createSharpClawHost(definition, options = {}) {
  const normalized = normalizeDefinition(definition);
  const runtime = options.runtime ?? 'node';
  const runtimeVersion = options.runtimeVersion ?? process.version;
  const controlAddress = new URL(options.controlAddress ?? readRequiredEnv(env.controlAddress));
  const controlToken = options.controlToken ?? readRequiredEnv(env.controlToken);
  const moduleId = options.moduleId ?? process.env[env.moduleId] ?? normalized.moduleId;
  const hostCapabilities =
    options.hostCapabilities === undefined
      ? createHostCapabilitiesClient()
      : options.hostCapabilities;
  const compiledEndpoints = normalized.endpoints.map(compileEndpoint);
  const compiledTools = normalized.tools.map(compileTool);
  const compiledInlineTools = normalized.inlineTools.map(compileInlineTool);
  const compiledProtocolContracts =
    normalized.protocolContracts.map(compileProtocolContract);
  let server;

  async function start() {
    if (server) {
      return { address: controlAddress.toString() };
    }

    server = http.createServer((request, response) => {
      handleRequest(request, response).catch(error => {
        writeJson(response, 500, {
          error: error?.message ?? 'Unhandled SharpClaw module host error'
        });
      });
    });

    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(Number(controlAddress.port), controlAddress.hostname, () => {
        server?.off('error', reject);
        resolve();
      });
    });

    return { address: controlAddress.toString() };
  }

  async function stop() {
    if (!server) {
      return;
    }

    const current = server;
    server = undefined;
    await new Promise(resolve => current.close(resolve));
  }

  async function handleRequest(request, response) {
    const path = new URL(request.url ?? '/', controlAddress).pathname;
    if (!hasExpectedToken(request, controlToken)) {
      writeJson(response, 401, { error: 'Unauthorized' });
      return;
    }

    if (path.startsWith('/.sharpclaw/')) {
      await handleControlRequest(request, response, path);
      return;
    }

    await handleModuleEndpointRequest(request, response, path);
  }

  async function handleControlRequest(request, response, path) {
    if (request.method === 'POST' && path === controlPaths.handshake) {
      writeJson(response, 200, {
        protocolVersion,
        moduleId,
        toolPrefix: normalized.toolPrefix,
        runtime,
        runtimeVersion,
        capabilities: normalized.capabilities
      });
      return;
    }

    if (request.method === 'GET' && path === controlPaths.discovery) {
      writeJson(response, 200, {
        endpoints: normalized.endpoints.map(toEndpointDescriptor),
        tools: normalized.tools.map(toToolDescriptor),
        inlineTools: normalized.inlineTools.map(toInlineToolDescriptor),
        protocolContracts:
          normalized.protocolContracts.map(toProtocolContractDescriptor),
        requiredProtocolContracts: normalized.requiredProtocolContracts,
        storageContracts: normalized.storageContracts
      });
      return;
    }

    if (request.method === 'GET' && path === controlPaths.health) {
      const health = await normalized.health(createContext(request, path, {}, hostCapabilities));
      writeJson(response, 200, health ?? { isHealthy: true });
      return;
    }

    if (request.method === 'POST' && path === controlPaths.initialize) {
      const message = await normalized.initialize(createContext(request, path, {}, hostCapabilities));
      writeJson(response, 200, {
        accepted: true,
        message: typeof message === 'string' ? message : undefined
      });
      return;
    }

    if (request.method === 'POST' && path === controlPaths.shutdown) {
      const message = await normalized.shutdown(createContext(request, path, {}, hostCapabilities));
      writeJson(response, 200, {
        accepted: true,
        message: typeof message === 'string' ? message : undefined
      });
      setImmediate(() => {
        stop().catch(error => {
          console.error('SharpClaw shutdown failed:', error);
          process.exitCode = 1;
        });
      });
      return;
    }

    if (request.method === 'POST' && path === controlPaths.toolExecute) {
      await executeToolRequest(
        request,
        response,
        compiledTools,
        body => createToolContext(request, path, body, hostCapabilities));
      return;
    }

    if (request.method === 'POST' && path === controlPaths.inlineToolExecute) {
      await executeToolRequest(
        request,
        response,
        compiledInlineTools,
        body => createInlineToolContext(request, path, body, hostCapabilities));
      return;
    }

    if (request.method === 'POST' && path === controlPaths.toolStream) {
      await streamToolRequest(
        request,
        response,
        compiledTools,
        body => createToolContext(request, path, body, hostCapabilities));
      return;
    }

    if (request.method === 'POST' && path === controlPaths.contractInvoke) {
      await executeProtocolContractRequest(
        request,
        response,
        compiledProtocolContracts,
        hostCapabilities);
      return;
    }

    writeJson(response, 404, { error: 'Unknown SharpClaw control route' });
  }

  async function handleModuleEndpointRequest(request, response, path) {
    const method = request.method?.toUpperCase() ?? 'GET';
    const endpoint = compiledEndpoints.find(candidate =>
      candidate.method === method && candidate.match(path));

    if (!endpoint) {
      writeJson(response, 404, { error: 'Endpoint not found' });
      return;
    }

    const params = endpoint.match(path) ?? {};
    const context = createContext(request, path, params, hostCapabilities);
    const result = await endpoint.handler(context);
    await writeEndpointResult(response, result);
  }

  async function executeToolRequest(request, response, tools, createToolContext) {
    const body = JSON.parse(await readText(request) || '{}');
    const tool = tools.find(candidate => candidate.name === body.toolName);
    if (!tool) {
      writeJson(response, 404, { error: `Tool '${body.toolName}' not found` });
      return;
    }

    const result = await tool.handler(createToolContext(body));
    if (result && typeof result === 'object' && 'result' in result) {
      writeJson(response, 200, result);
      return;
    }

    writeJson(response, 200, {
      result: result === undefined || result === null ? '' : String(result),
      completionBehavior: tool.completionBehavior
    });
  }

  async function streamToolRequest(request, response, tools, createToolContext) {
    const body = JSON.parse(await readText(request) || '{}');
    const tool = tools.find(candidate => candidate.name === body.toolName);
    if (!tool) {
      writeJson(response, 404, { error: `Tool '${body.toolName}' not found` });
      return;
    }

    if (!tool.supportsStreaming) {
      writeJson(response, 404, { error: `Tool '${body.toolName}' is not streaming` });
      return;
    }

    response.writeHead(200, {
      'Content-Type': 'application/x-ndjson; charset=utf-8'
    });
    const result = await tool.handler(createToolContext(body));
    if (isAsyncIterable(result) || isIterable(result)) {
      for await (const chunk of result) {
        response.write(`${JSON.stringify({ delta: String(chunk) })}\n`);
      }
    } else if (result !== undefined && result !== null) {
      response.write(`${JSON.stringify({ delta: String(result) })}\n`);
    }

    response.write(`${JSON.stringify({ isFinal: true })}\n`);
    response.end();
  }

  async function executeProtocolContractRequest(request, response, contracts, hostCapabilities) {
    const body = JSON.parse(await readText(request) || '{}');
    const contract = contracts.find(candidate =>
      candidate.contractName === body.contractName);
    if (!contract) {
      writeJson(response, 404, { error: `Contract '${body.contractName}' not found` });
      return;
    }

    const handler = contract.handlers?.[body.operation];
    if (typeof handler !== 'function') {
      writeJson(response, 404, {
        error: `Contract '${body.contractName}' operation '${body.operation}' not found`
      });
      return;
    }

    const result = await handler({
      contractName: body.contractName,
      operation: body.operation,
      parameters: body.parameters ?? {},
      hostCapabilities
    });
    writeJson(response, 200, { result: result ?? null });
  }

  return { start, stop };
}

export function json(value, status = 200, headers = {}) {
  return {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      ...headers
    },
    body: JSON.stringify(value)
  };
}

export function text(value, status = 200, headers = {}) {
  return {
    status,
    headers: {
      'Content-Type': 'text/plain; charset=utf-8',
      ...headers
    },
    body: value
  };
}

export function createHostCapabilitiesClient(options = {}) {
  const address = options.address ?? process.env[env.hostCapabilitiesAddress];
  const token = options.token ?? process.env[env.hostCapabilitiesToken];
  if (!address || !token) {
    return null;
  }

  async function call(path, body = {}) {
    const response = await fetch(new URL(path, address), {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        [tokenHeaderName]: token
      },
      body: JSON.stringify(body ?? {})
    });

    if (!response.ok) {
      const detail = await response.text();
      throw new Error(
        `SharpClaw host capability call failed: ${response.status} ${detail}`);
    }

    if (response.status === 204) {
      return undefined;
    }

    return response.json();
  }

  return {
    invoke: call,
    getConfig: async key => (await call(hostCapabilityPaths.configGet, { key }))?.value,
    setConfig: (key, value) => call(hostCapabilityPaths.configSet, { key, value }),
    getAllConfig: async () => (await call(hostCapabilityPaths.configAll))?.values ?? {},
    log: (message, level = 'Info') => call(hostCapabilityPaths.log, { message, level }),
    addJobLog: (jobId, message, level = 'Info') =>
      call(hostCapabilityPaths.jobLog, { jobId, message, level }),
    completeJob: (jobId, resultData = null, message = null) =>
      call(hostCapabilityPaths.jobComplete, { jobId, resultData, message }),
    failJob: (jobId, message, details = null) =>
      call(hostCapabilityPaths.jobFail, { jobId, message, details }),
    cancelJob: (jobId, message = null) =>
      call(hostCapabilityPaths.jobCancel, { jobId, message }),
    cancelStaleJobsByActionPrefix: (actionKeyPrefix, resourceId = null) =>
      call(hostCapabilityPaths.jobCancelStaleByActionPrefix, { actionKeyPrefix, resourceId }),
    getJob: async jobId => (await call(hostCapabilityPaths.jobGet, { id: jobId }))?.job,
    listJobsByActionPrefix: async (actionKeyPrefix, resourceId = null) =>
      (await call(hostCapabilityPaths.jobListByActionPrefix, { actionKeyPrefix, resourceId }))?.jobs ?? [],
    listJobSummariesByActionPrefix: async (actionKeyPrefix, resourceId = null) =>
      (await call(hostCapabilityPaths.jobListSummariesByActionPrefix, { actionKeyPrefix, resourceId }))?.jobs ?? [],
    jobExistsWithActionPrefix: async (jobId, actionKeyPrefix) =>
      (await call(hostCapabilityPaths.jobExistsWithActionPrefix, { jobId, actionKeyPrefix }))?.value ?? false,
    listProtocolContracts: async () =>
      (await call(hostCapabilityPaths.contractsList))?.contracts ?? [],
    invokeProtocolContract: async (contractName, operation, parameters = {}) =>
      (await call(hostCapabilityPaths.contractInvoke, {
        contractName,
        operation,
        parameters
      }))?.result,
    validateTask: sourceText => call(hostCapabilityPaths.taskValidate, { sourceText }),
    createTask: async sourceText =>
      (await call(hostCapabilityPaths.taskCreate, { sourceText }))?.definition,
    getTask: async id => (await call(hostCapabilityPaths.taskGet, { id }))?.definition,
    listTasks: async () => (await call(hostCapabilityPaths.taskList))?.definitions ?? [],
    updateTask: async (id, values = {}) =>
      (await call(hostCapabilityPaths.taskUpdate, { id, ...values }))?.definition,
    deleteTask: async id => (await call(hostCapabilityPaths.taskDelete, { id }))?.deleted ?? false,
    launchTask: async (taskDefinitionId, values = {}) =>
      (await call(hostCapabilityPaths.taskLaunch, { taskDefinitionId, ...values }))?.instanceId,
    executeTaskContextSteps: async parameters =>
      await call(hostCapabilityPaths.taskContextExecuteSteps, parameters),
    executeTaskContextEventHandler: async parameters =>
      await call(hostCapabilityPaths.taskContextExecuteEventHandler, parameters),
    getAgentIds: async () => (await call(hostCapabilityPaths.coreAgentIds))?.ids ?? [],
    getChannelIds: async () => (await call(hostCapabilityPaths.coreChannelIds))?.ids ?? [],
    getAgentLookupItems: async () => (await call(hostCapabilityPaths.coreAgentLookup))?.items ?? [],
    getChannelLookupItems: async () => (await call(hostCapabilityPaths.coreChannelLookup))?.items ?? [],
    getAccessibleThreads: async parameters =>
      (await call(hostCapabilityPaths.contextAccessibleThreads, parameters))?.threads ?? [],
    getThreadMessages: async parameters =>
      (await call(hostCapabilityPaths.contextThreadMessages, parameters))?.messages ?? [],
    addConversationSteering: async parameters =>
      (await call(hostCapabilityPaths.conversationSteer, parameters))?.steering,
    listConversationSteering: async (channelId, threadId = null, limit = 20) =>
      (await call(hostCapabilityPaths.conversationSteeringList, { channelId, threadId, limit }))?.steering ?? [],
    getQueueMetrics: () => call(hostCapabilityPaths.queueMetrics),
    hostAgentChat: async parameters =>
      (await call(hostCapabilityPaths.hostAgentChat, parameters))?.text,
    hostAgentChatStream: async parameters =>
      (await call(hostCapabilityPaths.hostAgentChatStream, parameters))?.text,
    hostAgentChatToThread: async parameters =>
      (await call(hostCapabilityPaths.hostAgentChatToThread, parameters))?.text,
    parseStructuredResponse: async parameters =>
      (await call(hostCapabilityPaths.hostAgentParseStructuredResponse, parameters))?.text,
    findModel: async search =>
      (await call(hostCapabilityPaths.hostAgentFindModel, { search }))?.id,
    findProvider: async search =>
      (await call(hostCapabilityPaths.hostAgentFindProvider, { search }))?.id,
    findAgent: async search =>
      (await call(hostCapabilityPaths.hostAgentFindAgent, { search }))?.id,
    findRole: async search =>
      (await call(hostCapabilityPaths.hostAgentFindRole, { search }))?.id,
    findChannel: async search =>
      (await call(hostCapabilityPaths.hostAgentFindChannel, { search }))?.id,
    createAgent: async parameters =>
      (await call(hostCapabilityPaths.hostAgentCreateAgent, parameters))?.id,
    createThread: async parameters =>
      (await call(hostCapabilityPaths.hostAgentCreateThread, parameters))?.id,
    createRole: async roleName =>
      (await call(hostCapabilityPaths.hostAgentCreateRole, { roleName }))?.id,
    setRolePermissions: parameters =>
      call(hostCapabilityPaths.hostAgentSetRolePermissions, parameters),
    assignRole: parameters =>
      call(hostCapabilityPaths.hostAgentAssignRole, parameters),
    createChannel: async parameters =>
      (await call(hostCapabilityPaths.hostAgentCreateChannel, parameters))?.id,
    addAllowedAgent: parameters =>
      call(hostCapabilityPaths.hostAgentAddAllowedAgent, parameters),
    createSubAgent: async parameters =>
      await call(hostCapabilityPaths.agentCreateSubAgent, parameters),
    updateAgent: async parameters =>
      await call(hostCapabilityPaths.agentUpdate, parameters),
    setAgentHeader: (id, header) =>
      call(hostCapabilityPaths.agentSetHeader, { id, header }),
    setChannelHeader: (id, header) =>
      call(hostCapabilityPaths.channelSetHeader, { id, header }),
    ensureProvider: async parameters =>
      (await call(hostCapabilityPaths.modelEnsureProvider, parameters))?.id,
    ensureModel: async parameters =>
      (await call(hostCapabilityPaths.modelEnsureModel, parameters))?.id,
    getModelProviderInfo: async modelId =>
      (await call(hostCapabilityPaths.modelProviderInfo, { modelId }))?.info,
    getLocalModelFilePath: async modelId =>
      (await call(hostCapabilityPaths.modelLocalFilePath, { modelId }))?.path,
    getModelMetadata: async modelId =>
      (await call(hostCapabilityPaths.modelMetadata, { modelId }))?.metadata,
    deleteModel: async modelId =>
      (await call(hostCapabilityPaths.modelDelete, { modelId }))?.value ?? false,
    getExternalModulesRoot: async () =>
      (await call(hostCapabilityPaths.modulesExternalRoot))?.directory,
    listModules: async () =>
      (await call(hostCapabilityPaths.modulesInfoList))?.modules ?? [],
    isModuleRegistered: async moduleId =>
      (await call(hostCapabilityPaths.moduleRegistered, { moduleId }))?.isRegistered ?? false,
    isToolPrefixRegistered: async toolPrefix =>
      (await call(hostCapabilityPaths.moduleToolPrefixRegistered, { toolPrefix }))?.isRegistered ?? false,
    loadModule: async moduleDir =>
      (await call(hostCapabilityPaths.moduleLoad, { moduleDir }))?.state,
    unloadModule: moduleId =>
      call(hostCapabilityPaths.moduleUnload, { moduleId }),
    reloadModule: async moduleId =>
      (await call(hostCapabilityPaths.moduleReload, { moduleId }))?.state,
    invokeModuleTool: async (toolName, parameters = {}, timeoutSeconds = null) =>
      (await call(hostCapabilityPaths.moduleToolInvoke, { toolName, parameters, timeoutSeconds }))?.result,
    listStorageContracts: async () =>
      (await call(hostCapabilityPaths.moduleStorageList))?.contracts ?? [],
    invokeStorage: async (storageName, operation, parameters = {}) =>
      (await call(hostCapabilityPaths.moduleStorageInvoke, {
        storageName,
        operation,
        parameters
      }))?.result
  };
}

export function createDocumentStore(hostCapabilities, storageName) {
  if (!hostCapabilities || typeof hostCapabilities.invokeStorage !== 'function') {
    throw new TypeError('SharpClaw document stores require host storage capabilities.');
  }

  const invoke = (operation, parameters = {}) =>
    hostCapabilities.invokeStorage(storageName, operation, parameters);

  const records = response =>
    Array.isArray(response?.records)
      ? response.records
          .filter(record => record && Object.hasOwn(record, 'value'))
          .map(record => record.value)
      : [];

  return {
    get: async key => {
      const response = await invoke(storageOperations.get, { key });
      return response?.found === true ? response.value : null;
    },
    list: async options => records(await invoke(storageOperations.list, options ?? {})),
    upsert: async (key, value, indexes = null) => {
      const payload = { key, value };
      if (indexes !== null && indexes !== undefined) payload.indexes = indexes;
      return await invoke(storageOperations.upsert, payload);
    },
    upsertMany: async writes => {
      const response = await invoke(storageOperations.batchUpsert, { records: Array.from(writes ?? []) });
      return response?.saved ?? 0;
    },
    delete: async key => {
      const response = await invoke(storageOperations.delete, { key });
      return response?.deleted === true;
    },
    deleteMany: async keys => {
      const response = await invoke(storageOperations.batchDelete, { keys: Array.from(keys ?? []) });
      return response?.deleted ?? 0;
    },
    query: () => createStorageQueryBuilder(async payload =>
      records(await invoke(storageOperations.query, payload))),
    claim: () => createStorageClaimBuilder(async payload =>
      records(await invoke(storageOperations.claim, payload)))
  };
}

function createStorageQueryBuilder(execute) {
  const payload = {
    filters: [],
    orderBy: null,
    limit: null
  };

  const builder = {
    whereIndex: indexName => ({
      equalTo: value => addFilter(builder, payload, indexName, storageComparisonOperators.equalTo, value),
      lessThanOrEqual: value =>
        addFilter(builder, payload, indexName, storageComparisonOperators.lessThanOrEqual, value),
      greaterThanOrEqual: value =>
        addFilter(builder, payload, indexName, storageComparisonOperators.greaterThanOrEqual, value)
    }),
    orderByIndex: indexName => setOrder(builder, payload, indexName, storageSortDirections.ascending),
    orderByIndexDescending: indexName =>
      setOrder(builder, payload, indexName, storageSortDirections.descending),
    take: limit => {
      payload.limit = limit;
      return builder;
    },
    toList: () => execute(cleanPayload(payload)),
    toListAsync: () => execute(cleanPayload(payload))
  };

  return builder;
}

function createStorageClaimBuilder(execute) {
  const payload = {
    filters: [],
    orderBy: null,
    limit: null,
    patch: null,
    indexes: null
  };

  const builder = {
    whereIndex: indexName => ({
      equalTo: value => addFilter(builder, payload, indexName, storageComparisonOperators.equalTo, value),
      lessThanOrEqual: value =>
        addFilter(builder, payload, indexName, storageComparisonOperators.lessThanOrEqual, value),
      greaterThanOrEqual: value =>
        addFilter(builder, payload, indexName, storageComparisonOperators.greaterThanOrEqual, value)
    }),
    orderByIndex: indexName => setOrder(builder, payload, indexName, storageSortDirections.ascending),
    orderByIndexDescending: indexName =>
      setOrder(builder, payload, indexName, storageSortDirections.descending),
    take: limit => {
      payload.limit = limit;
      return builder;
    },
    patch: (patch, indexes = null) => {
      payload.patch = patch;
      payload.indexes = indexes;
      return builder;
    },
    toList: () => {
      if (payload.patch === null || payload.patch === undefined) {
        throw new Error('SharpClaw storage claim requires a patch before execution.');
      }

      return execute(cleanPayload(payload));
    },
    toListAsync: () => {
      if (payload.patch === null || payload.patch === undefined) {
        throw new Error('SharpClaw storage claim requires a patch before execution.');
      }

      return execute(cleanPayload(payload));
    }
  };

  return builder;
}

function addFilter(builder, payload, indexName, operator, value) {
  payload.filters.push({ indexName, operator, value });
  return builder;
}

function setOrder(builder, payload, indexName, direction) {
  payload.orderBy = { indexName, direction };
  return builder;
}

function cleanPayload(payload) {
  return Object.fromEntries(
    Object.entries(payload)
      .filter(([, value]) => value !== null && value !== undefined));
}

function normalizeDefinition(definition) {
  if (!definition || typeof definition !== 'object') {
    throw new TypeError('SharpClaw module definition is required.');
  }

  if (!definition.moduleId) {
    throw new TypeError('SharpClaw module definition must include moduleId.');
  }

  if (!definition.toolPrefix) {
    throw new TypeError('SharpClaw module definition must include toolPrefix.');
  }

  const endpoints = Array.isArray(definition.endpoints) ? definition.endpoints : [];
  return {
    moduleId: definition.moduleId,
    toolPrefix: definition.toolPrefix,
    capabilities: definition.capabilities ?? ['endpoints', 'lifecycleHooks'],
    endpoints,
    tools: Array.isArray(definition.tools) ? definition.tools : [],
    inlineTools: Array.isArray(definition.inlineTools) ? definition.inlineTools : [],
    protocolContracts: Array.isArray(definition.protocolContracts)
      ? definition.protocolContracts
      : [],
    requiredProtocolContracts: Array.isArray(definition.requiredProtocolContracts)
      ? definition.requiredProtocolContracts
      : [],
    storageContracts: Array.isArray(definition.storageContracts)
      ? definition.storageContracts
      : [],
    initialize: definition.initialize ?? noop,
    shutdown: definition.shutdown ?? noop,
    health: definition.health ?? (() => ({ isHealthy: true, message: 'ready' }))
  };
}

function compileEndpoint(endpoint) {
  if (!endpoint || typeof endpoint !== 'object') {
    throw new TypeError('Endpoint descriptors must be objects.');
  }

  if (typeof endpoint.handler !== 'function') {
    throw new TypeError(`Endpoint ${endpoint.routePattern} is missing a handler.`);
  }

  return {
    ...endpoint,
    method: (endpoint.method ?? 'GET').toUpperCase(),
    responseMode: endpoint.responseMode ?? 'json',
    match: compileRoutePattern(endpoint.routePattern),
    handler: endpoint.handler
  };
}

function compileTool(tool) {
  if (!tool || typeof tool !== 'object') {
    throw new TypeError('Tool descriptors must be objects.');
  }

  if (typeof tool.handler !== 'function') {
    throw new TypeError(`Tool ${tool.name} is missing a handler.`);
  }

  return {
    ...tool,
    supportsStreaming: tool.supportsStreaming ?? false,
    completionBehavior: tool.completionBehavior ?? 'CompleteWhenExecutionReturns'
  };
}

function compileInlineTool(tool) {
  if (!tool || typeof tool !== 'object') {
    throw new TypeError('Inline tool descriptors must be objects.');
  }

  if (typeof tool.handler !== 'function') {
    throw new TypeError(`Inline tool ${tool.name} is missing a handler.`);
  }

  return { ...tool };
}

function compileProtocolContract(contract) {
  if (!contract || typeof contract !== 'object') {
    throw new TypeError('Protocol contract descriptors must be objects.');
  }

  if (!contract.contractName) {
    throw new TypeError('Protocol contract descriptors must include contractName.');
  }

  return {
    ...contract,
    schema: contract.schema ?? emptyObjectSchema(),
    operations: Array.isArray(contract.operations) ? contract.operations : [],
    handlers: contract.handlers ?? {}
  };
}

function toEndpointDescriptor(endpoint) {
  const {
    method,
    routePattern,
    responseMode,
    authPolicy,
    permission,
    contributionId,
    metadata
  } = endpoint;

  return {
    method: (method ?? 'GET').toUpperCase(),
    routePattern,
    responseMode: responseMode ?? 'json',
    authPolicy,
    permission,
    contributionId,
    metadata
  };
}

function toToolDescriptor(tool) {
  const {
    name,
    description,
    parametersSchema,
    permission,
    timeoutSeconds,
    aliases,
    supportsStreaming,
    completionBehavior
  } = tool;

  return {
    name,
    description,
    parametersSchema: parametersSchema ?? emptyObjectSchema(),
    permission,
    timeoutSeconds,
    aliases,
    supportsStreaming: supportsStreaming ?? false,
    completionBehavior: completionBehavior ?? 'CompleteWhenExecutionReturns'
  };
}

function toInlineToolDescriptor(tool) {
  const {
    name,
    description,
    parametersSchema,
    permission,
    aliases
  } = tool;

  return {
    name,
    description,
    parametersSchema: parametersSchema ?? emptyObjectSchema(),
    permission,
    aliases
  };
}

function toProtocolContractDescriptor(contract) {
  const {
    contractName,
    schema,
    operations,
    description
  } = contract;

  return {
    contractName,
    schema: schema ?? emptyObjectSchema(),
    operations: operations ?? [],
    description
  };
}

function compileRoutePattern(routePattern) {
  if (!routePattern || !routePattern.startsWith('/')) {
    throw new TypeError(`Invalid SharpClaw route pattern '${routePattern}'.`);
  }

  const patternSegments = routePattern.split('/').filter(Boolean);
  return path => {
    const pathSegments = path.split('/').filter(Boolean);
    const params = {};

    for (let i = 0; i < patternSegments.length; i += 1) {
      const pattern = patternSegments[i];
      const value = pathSegments[i];

      if (pattern.startsWith('{**') && pattern.endsWith('}')) {
        params[pattern.slice(3, -1)] = pathSegments.slice(i).join('/');
        return params;
      }

      if (value === undefined) {
        return null;
      }

      if (pattern.startsWith('{') && pattern.endsWith('}')) {
        params[pattern.slice(1, -1)] = decodeURIComponent(value);
        continue;
      }

      if (pattern !== value) {
        return null;
      }
    }

    return pathSegments.length === patternSegments.length ? params : null;
  };
}

function createContext(request, path, params, hostCapabilities) {
  const parsed = new URL(request.url ?? '/', 'http://127.0.0.1');
  return {
    request,
    method: request.method ?? 'GET',
    path,
    params,
    query: parsed.searchParams,
    headers: request.headers,
    env: {
      moduleDirectory: process.env[env.moduleDirectory],
      moduleDataDirectory: process.env[env.moduleDataDirectory],
      moduleId: process.env[env.moduleId],
      runtime: process.env[env.moduleRuntime]
    },
    hostCapabilities,
    readText: () => readText(request),
    readJson: async () => JSON.parse(await readText(request))
  };
}

function createToolContext(request, path, body, hostCapabilities) {
  return {
    request,
    path,
    toolName: body.toolName,
    parameters: body.parameters ?? {},
    job: body.job,
    hostCapabilities
  };
}

function createInlineToolContext(request, path, body, hostCapabilities) {
  return {
    request,
    path,
    toolName: body.toolName,
    parameters: body.parameters ?? {},
    context: body.context,
    hostCapabilities
  };
}

async function writeEndpointResult(response, result) {
  if (result === undefined || result === null) {
    response.writeHead(204);
    response.end();
    return;
  }

  if (typeof result === 'string' || Buffer.isBuffer(result)) {
    response.writeHead(200);
    response.end(result);
    return;
  }

  if ('body' in result || 'status' in result || 'headers' in result) {
    const status = result.status ?? 200;
    response.writeHead(status, result.headers ?? {});
    response.end(result.body ?? '');
    return;
  }

  writeJson(response, 200, result);
}

function hasExpectedToken(request, controlToken) {
  const actual = request.headers[tokenHeaderName.toLowerCase()];
  if (Array.isArray(actual)) {
    return actual.includes(controlToken);
  }

  return actual === controlToken;
}

function writeJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body)
  });
  response.end(body);
}

function emptyObjectSchema() {
  return {
    type: 'object',
    properties: {}
  };
}

function isAsyncIterable(value) {
  return value && typeof value[Symbol.asyncIterator] === 'function';
}

function isIterable(value) {
  return value && typeof value !== 'string' && typeof value[Symbol.iterator] === 'function';
}

async function readText(request) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(Buffer.from(chunk));
  }

  return Buffer.concat(chunks).toString('utf8');
}

function readRequiredEnv(name) {
  const value = process.env[name];
  if (!value) {
    throw new Error(`Missing required environment variable '${name}'.`);
  }

  return value;
}

function noop() {
}
