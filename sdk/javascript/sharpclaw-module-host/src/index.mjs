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
  inlineToolExecute: '/.sharpclaw/inline-tools/execute'
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
  jobCancel: '/.sharpclaw/host/job/cancel'
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
        inlineTools: normalized.inlineTools.map(toInlineToolDescriptor)
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
      call(hostCapabilityPaths.jobCancel, { jobId, message })
  };
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
