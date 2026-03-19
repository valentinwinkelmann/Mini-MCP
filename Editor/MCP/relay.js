const http = require('http');
const { URL } = require('url');
const fs = require('fs');
const os = require('os');
const path = require('path');

const singletonLockPath = path.join(os.tmpdir(), 'mini-unity-mcp-relay.lock.json');
let ownsSingletonLock = false;
const defaultRecompileTimeoutMs = 120000;
const maxRecompileTimeoutMs = 600000;
const statusProbeTimeoutMs = 1500;

function normalizeSecret(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function describeSecret(value) {
  const normalized = normalizeSecret(value);
  return normalized ? `"${normalized}" (len=${normalized.length})` : '<empty>';
}

function isDualStackBindHost(value) {
  const normalized = typeof value === 'string' ? value.trim() : '';
  return normalized === '' || normalized === '0.0.0.0' || normalized === '::';
}

function isIpv6ListenFallbackError(error) {
  return error && (error.code === 'EAFNOSUPPORT' || error.code === 'EADDRNOTAVAIL');
}

function listenAsync(server, options) {
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      server.off('error', onError);
      server.off('listening', onListening);
    };

    const onError = (error) => {
      cleanup();
      reject(error);
    };

    const onListening = () => {
      cleanup();
      resolve();
    };

    server.once('error', onError);
    server.once('listening', onListening);
    server.listen(options);
  });
}

async function startPublicServer(server, port, bindHost) {
  if (isDualStackBindHost(bindHost)) {
    try {
      await listenAsync(server, { port, host: '::', ipv6Only: false });
      return {
        effectiveBindHost: '::',
        bindDescription: 'dual-stack'
      };
    } catch (error) {
      if (!isIpv6ListenFallbackError(error)) {
        throw error;
      }

      await listenAsync(server, { port, host: '0.0.0.0' });
      return {
        effectiveBindHost: '0.0.0.0',
        bindDescription: 'ipv4-only fallback'
      };
    }
  }

  await listenAsync(server, { port, host: bindHost });
  return {
    effectiveBindHost: bindHost,
    bindDescription: bindHost === '::' ? 'ipv6-only' : 'explicit'
  };
}

function parseArgs(argv) {
  const config = {
    port: 7788,
    target: 'http://127.0.0.1:7777/mcp',
    publicEnabled: false,
    publicBindHost: '0.0.0.0',
    publicPort: 8443,
    publicSecret: ''
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--port' && i + 1 < argv.length) {
      const parsed = Number(argv[i + 1]);
      if (Number.isInteger(parsed) && parsed > 0) {
        config.port = parsed;
      }
      i += 1;
      continue;
    }

    if (arg === '--target' && i + 1 < argv.length) {
      config.target = argv[i + 1];
      i += 1;
      continue;
    }

    if (arg === '--public-enabled') {
      config.publicEnabled = true;
      continue;
    }

    if (arg === '--public-port' && i + 1 < argv.length) {
      const parsed = Number(argv[i + 1]);
      if (Number.isInteger(parsed) && parsed > 0) {
        config.publicPort = parsed;
      }
      i += 1;
      process.stderr.write('[relay] public auth rejected because configured secret is empty\n');
      continue;
    }

    if (arg === '--public-bind-host' && i + 1 < argv.length) {
      config.publicBindHost = argv[i + 1];
      i += 1;
      continue;
    }

    if (arg === '--public-secret' && i + 1 < argv.length) {
      config.publicSecret = normalizeSecret(argv[i + 1]);
      i += 1;
    }
  }

  return config;
}

function buildBusyResponse(payload) {
  const id = payload && Object.prototype.hasOwnProperty.call(payload, 'id') ? payload.id : null;
  const method = payload && typeof payload.method === 'string' ? payload.method : '';
  const message = 'Unity backend is currently unavailable/busy. Retry shortly.';

  if (method === 'initialize') {
    return {
      jsonrpc: '2.0',
      id,
      result: {
        protocolVersion: '2025-11-25',
        capabilities: {
          tools: {
            listChanged: true
          }
        },
        serverInfo: {
          name: 'mini-unity-mcp-relay',
          version: '0.1.0'
        },
        instructions: message
      }
    };
  }

  if (method === 'tools/list') {
    return {
      jsonrpc: '2.0',
      id,
      result: cachedToolsList || { tools: [] }
    };
  }

  if (method === 'tools/call') {
    return {
      jsonrpc: '2.0',
      id,
      result: {
        content: [
          {
            type: 'text',
            text: JSON.stringify({
              status: 'busy_compiling',
              retryable: true,
              retryAfterMs: 2500,
              message
            })
          }
        ],
        isError: true
      }
    };
  }

  return {
    jsonrpc: '2.0',
    id,
    error: {
      code: -32003,
      message
    }
  };
}

function isLikelyCompilingSnapshot(snapshot) {
  return Boolean(snapshot && (snapshot.isCompiling === true || snapshot.indicatesCompileInProgress === true));
}

function isMutatingToolName(toolName) {
  return toolName === 'kanban_write'
    || toolName === 'scene_write'
    || toolName === 'playmode_control'
    || toolName === 'request_recompile';
}

function buildRelayUnavailableToolResult(id, status, retryable, message, extraFields) {
  const toolResult = Object.assign({
    status,
    retryable,
    message
  }, extraFields || {});

  return {
    jsonrpc: '2.0',
    id,
    result: {
      content: [
        {
          type: 'text',
          text: JSON.stringify(toolResult)
        }
      ],
      isError: true
    }
  };
}

function buildUnavailableResponse(payload, snapshot, reason) {
  const id = payload && Object.prototype.hasOwnProperty.call(payload, 'id') ? payload.id : null;
  const method = payload && typeof payload.method === 'string' ? payload.method : '';
  const compileLikely = unityHint.state === 'compiling' || isLikelyCompilingSnapshot(snapshot);
  const normalizedReason = typeof reason === 'string' && reason.trim() ? reason.trim() : 'backend unavailable';

  if (compileLikely) {
    return buildBusyResponse(payload);
  }

  if (method === 'initialize') {
    return {
      jsonrpc: '2.0',
      id,
      error: {
        code: -32003,
        message: 'Unity backend is temporarily unavailable. Retry initialization shortly.'
      }
    };
  }

  if (method === 'tools/list') {
    if (cachedToolsList) {
      return {
        jsonrpc: '2.0',
        id,
        result: cachedToolsList
      };
    }

    return {
      jsonrpc: '2.0',
      id,
      error: {
        code: -32003,
        message: 'Unity backend is temporarily unavailable. Tool list could not be refreshed.'
      }
    };
  }

  if (method === 'tools/call') {
    const toolCall = getToolCallInfo(payload);
    if (isMutatingToolName(toolCall.name)) {
      return buildRelayUnavailableToolResult(
        id,
        'request_outcome_unknown',
        false,
        'The relay lost contact with Unity while waiting for a mutating tool call. The operation may already have been applied. Verify the current state before retrying.',
        {
          toolName: toolCall.name,
          outcomeUnknown: true,
          shouldRetryBlindly: false,
          reason: normalizedReason,
          unityHintState: unityHint.state || 'unknown',
          suggestedAction: 'Inspect the target state before retrying this mutation.'
        });
    }

    return buildRelayUnavailableToolResult(
      id,
      'backend_unavailable',
      true,
      'Unity backend is temporarily unavailable. Retry the read-only request shortly.',
      {
        toolName: toolCall.name,
        outcomeUnknown: false,
        reason: normalizedReason,
        unityHintState: unityHint.state || 'unknown'
      });
  }

  return {
    jsonrpc: '2.0',
    id,
    error: {
      code: -32003,
      message: 'Unity backend is temporarily unavailable.'
    }
  };
}

function tryParseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function parseTimeoutMs(value, fallback) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return fallback;
  }

  return Math.max(1000, Math.min(Math.floor(parsed), maxRecompileTimeoutMs));
}

function parseUtcMs(value) {
  if (typeof value !== 'string' || !value) {
    return 0;
  }

  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function createDeferred() {
  let resolve;
  let reject;
  const promise = new Promise((innerResolve, innerReject) => {
    resolve = innerResolve;
    reject = innerReject;
  });

  return { promise, resolve, reject };
}

function getToolCallInfo(payload) {
  const params = payload && payload.params && typeof payload.params === 'object' ? payload.params : null;
  const args = payload && payload.arguments && typeof payload.arguments === 'object'
    ? payload.arguments
    : params && params.arguments && typeof params.arguments === 'object'
      ? params.arguments
      : {};

  return {
    name: typeof payload?.name === 'string'
      ? payload.name
      : params && typeof params.name === 'string'
        ? params.name
        : '',
    arguments: args
  };
}

function extractToolText(responseJson) {
  const content = responseJson && responseJson.result && Array.isArray(responseJson.result.content)
    ? responseJson.result.content
    : null;
  if (!content) {
    return '';
  }

  for (const item of content) {
    if (item && item.type === 'text' && typeof item.text === 'string') {
      return item.text;
    }
  }

  return '';
}

function buildToolResultResponse(id, resultText, isError) {
  return JSON.stringify({
    jsonrpc: '2.0',
    id,
    result: {
      content: [
        {
          type: 'text',
          text: resultText
        }
      ],
      isError: Boolean(isError)
    }
  });
}

async function fetchEditorStatusSnapshot() {
  const requestBody = JSON.stringify({
    jsonrpc: '2.0',
    id: `relay-recompile-status-${Date.now()}`,
    method: 'tools/call',
    params: {
      name: 'unity_editor_status',
      arguments: {}
    }
  });

  const forwarded = await forwardToTargetWithTimeout(targetUrl, requestBody, statusProbeTimeoutMs);
  const responseJson = tryParseJson(forwarded.body);
  const toolText = extractToolText(responseJson);
  const snapshot = tryParseJson(toolText);
  if (!snapshot || typeof snapshot !== 'object') {
    return null;
  }

  if (snapshot.status === 'busy_compiling') {
    return {
      isCompiling: true,
      indicatesCompileInProgress: true,
      compileErrorCount: 0,
      lastCompileStartedUtc: null,
      lastCompileFinishedUtc: null
    };
  }

  return snapshot;
}

function buildAwaitedOperationResult(payload) {
  const state = payload && typeof payload.state === 'string' ? payload.state : 'completed';
  const createdAtUtc = payload && typeof payload.createdAtUtc === 'string' ? payload.createdAtUtc : null;
  const startedAtUtc = payload && typeof payload.startedAtUtc === 'string' ? payload.startedAtUtc : null;
  const finishedAtUtc = payload && typeof payload.finishedAtUtc === 'string' ? payload.finishedAtUtc : null;
  const startedAtMs = parseUtcMs(startedAtUtc) || parseUtcMs(createdAtUtc) || Date.now();
  const finishedAtMs = parseUtcMs(finishedAtUtc) || Date.now();
  const waitedMs = Math.max(0, finishedAtMs - startedAtMs);
  const durationSeconds = Math.round((waitedMs / 1000) * 100) / 100;
  return JSON.stringify({
    status: state,
    operationId: payload && typeof payload.operationId === 'string' ? payload.operationId : '',
    toolName: payload && typeof payload.toolName === 'string' ? payload.toolName : '',
    kind: payload && typeof payload.kind === 'string' ? payload.kind : '',
    durationSeconds,
    outcomeKnown: payload && payload.outcomeKnown === true,
    message: payload && typeof payload.summaryMessage === 'string' && payload.summaryMessage
      ? payload.summaryMessage
      : 'Unity finished the awaited operation.',
    errorMessage: payload && typeof payload.errorMessage === 'string' && payload.errorMessage
      ? payload.errorMessage
      : null
  });
}

function isAwaitedOperationErrorState(state) {
  return state === 'failed' || state === 'timed_out' || state === 'canceled';
}

function buildAwaitedOperationTimeoutResult(pending) {
  const waitedMs = Math.max(0, Date.now() - pending.startedAtMs);
  const durationSeconds = Math.round((waitedMs / 1000) * 100) / 100;
  return JSON.stringify({
    status: 'timed_out',
    retryable: false,
    operationId: pending.operationId,
    toolName: pending.toolName,
    kind: pending.kind,
    timeoutMs: pending.timeoutMs,
    durationSeconds,
    outcomeKnown: false,
    message: 'Timed out while waiting for Unity to report completion. The operation outcome is uncertain; inspect Unity state before retrying.',
    errorMessage: 'Awaited operation completion was not confirmed before timeout.'
  });
}

function settlePendingAwaitedOperation(operationId, completionResultText, isError, reason) {
  const pending = pendingAwaitedOperations.get(operationId);
  if (!pending) {
    return false;
  }

  pendingAwaitedOperations.delete(operationId);
  clearTimeout(pending.timeoutHandle);
  process.stdout.write(`[relay] awaited operation settled operationId=${operationId} tool=${pending.toolName} reason=${reason}\n`);
  pending.deferred.resolve(buildToolResultResponse(pending.requestId, completionResultText, isError));
  return true;
}

function registerPendingAwaitedOperation(payload, toolResult) {
  const operationId = typeof toolResult.operationId === 'string' ? toolResult.operationId : '';
  if (!operationId) {
    return null;
  }

  const timeoutMs = parseTimeoutMs(toolResult.timeoutMs, defaultRecompileTimeoutMs);
  const toolCall = getToolCallInfo(payload);
  const deferred = createDeferred();
  const pending = {
    operationId,
    requestId: payload && Object.prototype.hasOwnProperty.call(payload, 'id') ? payload.id : null,
    toolName: typeof toolCall.name === 'string' ? toolCall.name : '',
    kind: typeof toolResult.kind === 'string' ? toolResult.kind : '',
    timeoutMs,
    startedAtMs: Date.now(),
    deferred,
    timeoutHandle: null
  };

  pending.timeoutHandle = setTimeout(() => {
    settlePendingAwaitedOperation(
      operationId,
      buildAwaitedOperationTimeoutResult(pending),
      true,
      'timeout');
  }, timeoutMs);

  pendingAwaitedOperations.set(operationId, pending);
  process.stdout.write(`[relay] awaiting operation completion operationId=${operationId} tool=${pending.toolName} kind=${pending.kind} timeoutMs=${timeoutMs}\n`);
  return deferred.promise;
}

async function maybeHandleAwaitedOperation(payload, forwardedBody) {
  const forwardedJson = tryParseJson(forwardedBody);
  if (!forwardedJson || !forwardedJson.result) {
    return null;
  }

  const toolText = extractToolText(forwardedJson);
  const toolResult = tryParseJson(toolText);
  if (!toolResult || toolResult.status !== 'await_started') {
    return null;
  }

  return registerPendingAwaitedOperation(payload, toolResult);
}

async function forwardToTarget(targetUrl, rawBody) {
  return forwardToTargetWithTimeout(targetUrl, rawBody, 20000);
}

async function forwardToTargetWithTimeout(targetUrl, rawBody, timeoutMs) {
  const response = await fetch(targetUrl, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: rawBody,
    signal: AbortSignal.timeout(timeoutMs)
  });

  const text = await response.text();
  return {
    status: response.status,
    body: text,
    contentType: response.headers.get('content-type') || 'application/json; charset=utf-8'
  };
}

function readExistingLock() {
  try {
    const raw = fs.readFileSync(singletonLockPath, 'utf8');
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') {
      return null;
    }

    return parsed;
  } catch {
    return null;
  }
}

function removeLockFileIfPresent() {
  try {
    fs.unlinkSync(singletonLockPath);
    return true;
  } catch (err) {
    if (err && err.code === 'ENOENT') {
      return true;
    }

    return false;
  }
}

function removeLockIfOwned() {
  if (!ownsSingletonLock) {
    return;
  }

  try {
    const existing = readExistingLock();
    if (existing && existing.pid === process.pid) {
      fs.unlinkSync(singletonLockPath);
    }
  } catch {
    // Best effort cleanup only.
  }

  ownsSingletonLock = false;
}

function canSignalProcess(pid) {
  if (!Number.isInteger(pid) || pid <= 0) {
    return false;
  }

  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

async function tryShutdownRelayOnPort(port) {
  if (!Number.isInteger(port) || port <= 0) {
    return false;
  }

  try {
    const response = await fetch(`http://127.0.0.1:${port}/shutdown`, {
      method: 'POST',
      signal: AbortSignal.timeout(1200)
    });

    return response.ok;
  } catch {
    return false;
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function acquireSingletonLockOrExit(port, target) {
  const payload = {
    pid: process.pid,
    port,
    target,
    createdAtUtc: new Date().toISOString()
  };

  const serialized = JSON.stringify(payload);
  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      const fd = fs.openSync(singletonLockPath, 'wx');
      fs.writeFileSync(fd, serialized, 'utf8');
      fs.closeSync(fd);
      ownsSingletonLock = true;
      return;
    } catch (err) {
      if (!err || err.code !== 'EEXIST') {
        process.stderr.write(`[relay] failed to create singleton lock: ${err && err.message ? err.message : 'unknown error'}\n`);
        process.exit(1);
      }

      const existing = readExistingLock();
      if (!existing) {
        try {
          fs.unlinkSync(singletonLockPath);
          process.stdout.write('[relay] removed unreadable stale singleton lock\n');
          continue;
        } catch {
          process.stderr.write('[relay] another relay lock exists and could not be removed\n');
          process.exit(1);
        }
      }

      process.stdout.write(`[relay] existing relay detected pid=${existing.pid || 'n/a'} port=${existing.port || 'n/a'}; attempting shutdown\n`);

      let stopped = false;
      if (Number.isInteger(existing.port) && existing.port > 0) {
        stopped = await tryShutdownRelayOnPort(existing.port);
      }

      if (!stopped && canSignalProcess(existing.pid)) {
        try {
          process.kill(existing.pid, 'SIGTERM');
          stopped = true;
          process.stdout.write(`[relay] sent SIGTERM to existing relay pid=${existing.pid}\n`);
        } catch {
          // Keep stopped=false and handle below.
        }
      }

      await delay(400);

      const stillRunning = canSignalProcess(existing.pid);
      if (stopped && !stillRunning) {
        if (removeLockFileIfPresent()) {
          process.stdout.write('[relay] removed previous relay lock after shutdown\n');
          continue;
        }

        process.stderr.write('[relay] could not remove previous relay lock after shutdown\n');
        process.exit(1);
      }

      if (!stillRunning) {
        if (removeLockFileIfPresent()) {
          process.stdout.write('[relay] removed stale lock from non-running relay process\n');
          continue;
        }

        process.stderr.write('[relay] stale lock detected but could not remove it\n');
        process.exit(1);
      }

      process.stderr.write(`[relay] another relay is still running (pid=${existing.pid}). refusing to start second instance\n`);
      process.exit(1);
    }
  }

  process.stderr.write('[relay] failed to acquire singleton lock after retries\n');
  process.exit(1);
}

const args = parseArgs(process.argv.slice(2));
let targetUrl = new URL(args.target).toString();
let cachedToolsList = null;
const pendingAwaitedOperations = new Map();
let backendUnavailable = false;
let backendConnected = null;
let unityHint = {
  state: 'unknown',
  reason: '',
  updatedAtMs: 0
};
let compileStartMs = 0;
let shuttingDown = false;

function setBackendConnectionState(isConnected, source, reason) {
  if (backendConnected === isConnected) {
    return;
  }

  backendConnected = isConnected;
  if (isConnected) {
    process.stdout.write(`[relay] UNITY MCP CONNECTED (source=${source} target=${targetUrl})\n`);
    return;
  }

  process.stderr.write(`[relay] UNITY MCP DISCONNECTED (source=${source} reason=${reason || 'n/a'} target=${targetUrl})\n`);
}

function updateUnityHint(state, reason) {
  const previousState = unityHint.state;
  const nowMs = Date.now();

  if (state === 'compiling' && previousState !== 'compiling') {
    compileStartMs = nowMs;
    process.stdout.write(`[relay] UNITY COMPILE STARTED (reason=${reason || 'n/a'})\n`);
  }

  if (state === 'ready' && previousState === 'compiling') {
    const durationMs = compileStartMs > 0 ? (nowMs - compileStartMs) : 0;
    process.stdout.write(`[relay] UNITY COMPILE FINISHED (durationMs=${durationMs} reason=${reason || 'n/a'})\n`);
    compileStartMs = 0;
  }

  unityHint = {
    state,
    reason: reason || '',
    updatedAtMs: nowMs
  };

  process.stdout.write(`[relay] unity status hint received; state=${state} reason=${reason || 'n/a'}\n`);
}

function getPayloadMeta(payload) {
  const method = payload && typeof payload.method === 'string' ? payload.method : 'unknown';
  const id = payload && Object.prototype.hasOwnProperty.call(payload, 'id') ? payload.id : null;
  return { method, id };
}

function closeServersAndExit() {
  if (shuttingDown) {
    return;
  }

  shuttingDown = true;
  const servers = [httpServer, publicServer].filter(Boolean);

  if (servers.length === 0) {
    process.exit(0);
    return;
  }

  let remaining = servers.length;
  const onClosed = () => {
    remaining -= 1;
    if (remaining <= 0) {
      process.exit(0);
    }
  };

  for (const serverInstance of servers) {
    serverInstance.close(onClosed);
  }

  setTimeout(() => process.exit(0), 750);
}

function isAuthorizedPublicRequest(req) {
  const expectedSecret = normalizeSecret(args.publicSecret);
  if (!expectedSecret) {
    process.stderr.write(`[relay pid=${process.pid}] public auth rejected because configured secret is empty\n`);
    return false;
  }

  const secretHeader = req.headers && typeof req.headers['x-mini-mcp-relay-secret'] === 'string'
    ? req.headers['x-mini-mcp-relay-secret'].trim()
    : '';

  let secretQuery = '';
  try {
    const requestUrl = new URL(req.url || '/', 'http://127.0.0.1');
    secretQuery = normalizeSecret(requestUrl.searchParams.get('secret') || '');
  } catch {
    secretQuery = '';
  }

  const effectiveSecret = secretHeader || secretQuery;

  if (effectiveSecret !== expectedSecret) {
    process.stderr.write(`[relay pid=${process.pid}] public auth mismatch expected=${describeSecret(expectedSecret)} receivedHeader=${describeSecret(secretHeader)} receivedQuery=${describeSecret(secretQuery)} path=${req.url || '/'}\n`);
  }

  return effectiveSecret === expectedSecret;
}

function writeUnauthorized(res) {
  res.writeHead(401, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'WWW-Authenticate': 'Bearer realm="mini-unity-mcp-upstream"'
  });
  res.end(JSON.stringify({
    jsonrpc: '2.0',
    id: null,
    error: {
      code: -32001,
      message: 'Unauthorized'
    }
  }));
}

function handleMcpRequest(req, res) {
  if (req.method !== 'POST' || !req.url.startsWith('/mcp')) {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('Not Found');
    return;
  }

  const chunks = [];
  req.on('data', (chunk) => chunks.push(chunk));
  req.on('end', async () => {
    const rawBody = Buffer.concat(chunks).toString('utf8');

    let parsedPayload = null;
    try {
      parsedPayload = JSON.parse(rawBody);
    } catch {
      process.stderr.write('[relay] invalid JSON payload on /mcp (parse error)\n');
      res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
      res.end(JSON.stringify({
        jsonrpc: '2.0',
        id: null,
        error: { code: -32700, message: 'Parse error' }
      }));
      return;
    }

    const meta = getPayloadMeta(parsedPayload);
    process.stdout.write(`[relay] request method=${meta.method} id=${String(meta.id)}\n`);

    try {
      const forwarded = await forwardToTarget(targetUrl, rawBody);
      setBackendConnectionState(true, 'request', 'ok');

      if (backendUnavailable) {
        process.stdout.write(`[relay] backend recovered; method=${meta.method} id=${String(meta.id)} status=${forwarded.status}\n`);
        backendUnavailable = false;
      }

      if (meta.method === 'tools/list') {
        try {
          const forwardedJson = JSON.parse(forwarded.body);
          if (forwardedJson && forwardedJson.result && Array.isArray(forwardedJson.result.tools)) {
            cachedToolsList = forwardedJson.result;
          }
        } catch {
          // Keep last known tools cache unchanged.
        }
      }

      if (meta.method === 'tools/call') {
        const awaitedOperationResponse = await maybeHandleAwaitedOperation(parsedPayload, forwarded.body);
        if (awaitedOperationResponse) {
          process.stdout.write(`[relay] completed awaited tool wait; method=${meta.method} id=${String(meta.id)}\n`);
          res.writeHead(200, {
            'Content-Type': 'application/json; charset=utf-8',
            'Cache-Control': 'no-store'
          });
          res.end(awaitedOperationResponse);
          return;
        }
      }

      process.stdout.write(`[relay] forwarded method=${meta.method} id=${String(meta.id)} status=${forwarded.status}\n`);
      res.writeHead(forwarded.status, {
        'Content-Type': forwarded.contentType,
        'Cache-Control': 'no-store'
      });
      res.end(forwarded.body);
    } catch (err) {
      const reason = err && err.message ? err.message : 'fetch failed';
      backendUnavailable = true;
      setBackendConnectionState(false, 'request', reason);
      process.stderr.write(`[relay] backend unavailable; method=${meta.method} id=${String(meta.id)} reason=${reason} target=${targetUrl}\n`);

      let snapshot = null;
      try {
        snapshot = await fetchEditorStatusSnapshot();
      } catch {
        snapshot = null;
      }

      const unavailableResponse = buildUnavailableResponse(parsedPayload, snapshot, reason);
      res.writeHead(200, {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-store'
      });
      res.end(JSON.stringify(unavailableResponse));
    }
  });
}

const localRequestHandler = (req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({
      ok: true,
      target: targetUrl,
      unityHintState: unityHint.state,
      unityHintUpdatedAtMs: unityHint.updatedAtMs || null
    }));
    return;
  }

  if (req.method === 'POST' && req.url === '/target') {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      try {
        const rawBody = Buffer.concat(chunks).toString('utf8');
        const payload = JSON.parse(rawBody || '{}');
        const target = payload && typeof payload.target === 'string' ? payload.target : '';
        const reason = payload && typeof payload.reason === 'string' ? payload.reason : 'unspecified';
        if (!target) {
          res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ ok: false, error: 'Missing target' }));
          return;
        }

        const normalized = new URL(target).toString();
        const previous = targetUrl;
        targetUrl = normalized;
        backendConnected = null;
        process.stdout.write(`[relay] target updated from ${previous} to ${targetUrl} reason=${reason}\n`);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: true, target: targetUrl }));
      } catch {
        res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: false, error: 'Invalid target payload' }));
      }
    });
    return;
  }

  if (req.method === 'POST' && req.url === '/status') {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      try {
        const rawBody = Buffer.concat(chunks).toString('utf8');
        const payload = JSON.parse(rawBody || '{}');
        const state = payload && typeof payload.state === 'string' ? payload.state : '';
        const reason = payload && typeof payload.reason === 'string' ? payload.reason : '';

        if (state !== 'compiling' && state !== 'ready') {
          res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ ok: false, error: 'Invalid state' }));
          return;
        }

        updateUnityHint(state, reason);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: true }));
      } catch {
        res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: false, error: 'Invalid JSON body' }));
      }
    });

    return;
  }

  if (req.method === 'POST' && req.url === '/operation-complete') {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      try {
        const rawBody = Buffer.concat(chunks).toString('utf8');
        const payload = JSON.parse(rawBody || '{}');
        const operationId = payload && typeof payload.operationId === 'string' ? payload.operationId : '';

        if (!operationId) {
          res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ ok: false, error: 'Missing operationId' }));
          return;
        }

        const resultText = buildAwaitedOperationResult(payload);
        const state = payload && typeof payload.state === 'string' ? payload.state : 'completed';
        const settled = settlePendingAwaitedOperation(operationId, resultText, isAwaitedOperationErrorState(state), 'unity_callback');
        if (!settled) {
          res.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end(JSON.stringify({ ok: false, error: 'Unknown operationId' }));
          return;
        }

        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: true, operationId }));
      } catch {
        res.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ ok: false, error: 'Invalid JSON body' }));
      }
    });

    return;
  }

  if (req.method === 'POST' && req.url === '/shutdown') {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({ ok: true, message: 'relay shutting down' }));
    setTimeout(() => {
      closeServersAndExit();
    }, 50);
    return;
  }

  handleMcpRequest(req, res);
};

const publicRequestHandler = (req, res) => {
  if (req.method === 'GET' && req.url.startsWith('/health')) {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify({
      ok: true,
      mode: 'public',
      target: targetUrl,
      bindHost: args.publicBindHost,
      unityHintState: unityHint.state,
      unityHintUpdatedAtMs: unityHint.updatedAtMs || null
    }));
    return;
  }

  if (!isAuthorizedPublicRequest(req)) {
    process.stderr.write(`[relay pid=${process.pid}] rejected unauthorized public request path=${req.url || '/'}\n`);
    writeUnauthorized(res);
    return;
  }

  handleMcpRequest(req, res);
};

const httpServer = http.createServer(localRequestHandler);

let publicServer = null;
if (args.publicEnabled) {
  publicServer = http.createServer(publicRequestHandler);
}

acquireSingletonLockOrExit(args.port, targetUrl)
  .then(() => {
    httpServer.listen(args.port, '127.0.0.1', () => {
      process.stdout.write(`relay listening on http://127.0.0.1:${args.port}/mcp -> ${targetUrl}\n`);
      process.stdout.write(`[relay pid=${process.pid}] mode=dumb-proxy (single-instance enforced)\n`);
    });

    if (publicServer) {
      startPublicServer(publicServer, args.publicPort, args.publicBindHost)
        .then((listenState) => {
          process.stdout.write(`relay public upstream listening on http://${listenState.effectiveBindHost}:${args.publicPort}/mcp -> ${targetUrl}\n`);
          process.stdout.write(`[relay pid=${process.pid}] public mode enabled bind=${args.publicBindHost} effective=${listenState.effectiveBindHost} (${listenState.bindDescription}) auth=${args.publicSecret ? 'shared-secret' : 'missing'}\n`);
          process.stdout.write(`[relay pid=${process.pid}] public configured secret=${describeSecret(args.publicSecret)}\n`);
        })
        .catch((err) => {
          process.stderr.write(`[relay] failed to start public listener: ${err && err.message ? err.message : 'unknown error'}\n`);
          process.exit(1);
        });
    }
  })
  .catch((err) => {
    process.stderr.write(`[relay] failed during singleton startup: ${err && err.message ? err.message : 'unknown error'}\n`);
    process.exit(1);
  });

process.on('SIGINT', () => {
  removeLockIfOwned();
  closeServersAndExit();
});

process.on('SIGTERM', () => {
  removeLockIfOwned();
  closeServersAndExit();
});

process.on('exit', () => {
  removeLockIfOwned();
});
