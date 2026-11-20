import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import type { AddressInfo } from "node:net";
import { defineRuntime, type AdapterSession } from "@botiverse/oar/browser";
import { createSessionKernel, sealSession } from "@botiverse/oar/kernel";
import { claudeRuntime } from "@botiverse/oar/runtimes/claude";
import { codexRuntime } from "@botiverse/oar/runtimes/codex";
import { scriptedRuntime } from "@botiverse/oar/testing";
import { OarBridgeServer, type BridgeTransport } from "../src/bridge.ts";
import { prepareMcpServers } from "../src/mcp-proxy.ts";

class MemoryTransport implements BridgeTransport {
  public readonly messages: Array<Record<string, unknown>> = [];

  public send(message: Record<string, unknown>): void {
    this.messages.push(message);
  }

  public responseFor(id: number): Record<string, unknown> {
    const found = this.messages.find((m) => m.id === id);
    assert.ok(found, `Expected JSON-RPC response for id=${id}`);
    return found;
  }

  public events(): Array<Record<string, unknown>> {
    return this.messages
      .filter((m) => m.method === "oar/event")
      .map((m) => m.params as Record<string, unknown>);
  }
}

test("prepareMcpServers uses unguessable route tokens, rejects predictable paths, and injects upstream headers", async () => {
  let receivedAuth: string | undefined;
  let receivedBody = "";
  let receivedPath = "";
  let upstreamHits = 0;

  const upstream = http.createServer((req, res) => {
    upstreamHits += 1;
    receivedAuth = req.headers.authorization;
    receivedPath = req.url ?? "";
    const chunks: Buffer[] = [];
    req.on("data", (chunk: Buffer) => chunks.push(chunk));
    req.on("end", () => {
      receivedBody = Buffer.concat(chunks).toString("utf8");
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ ok: true }));
    });
  });

  await new Promise<void>((resolve) => upstream.listen(0, "127.0.0.1", resolve));
  const upstreamPort = (upstream.address() as AddressInfo).port;

  const prepared = await prepareMcpServers(
    [
      {
        name: "perga",
        type: "http",
        url: `http://127.0.0.1:${upstreamPort}/mcp/kernel`,
        headers: { Authorization: "Bearer pg_secret_token" },
      },
    ],
    { stripHttpCredentials: true },
  );

  try {
    assert.equal(prepared.servers.length, 1);
    const proxied = prepared.servers[0]!;
    assert.equal("type" in proxied && proxied.type, "http");
    assert.equal("headers" in proxied ? proxied.headers : undefined, undefined);

    const proxiedUrl = new URL((proxied as { url: string }).url);
    assert.ok(proxiedUrl.pathname.length > 40, "Route token must be high-entropy");

    // Predictable route /mcp-proxy/s0 must be rejected without touching upstream
    const rejected = await fetch(`http://127.0.0.1:${proxiedUrl.port}/mcp-proxy/s0`, { method: "POST" });
    assert.equal(rejected.status, 404);
    assert.equal(upstreamHits, 0);

    // Valid capability URL succeeds and injects Authorization header upstream
    const response = await fetch(proxiedUrl, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "ping" }),
    });
    assert.equal(response.status, 200);
    assert.equal(upstreamHits, 1);
    assert.equal(receivedAuth, "Bearer pg_secret_token");
    assert.equal(receivedPath, "/mcp/kernel");
    assert.match(receivedBody, /"ping"/);
  } finally {
    await prepared.close();
    await new Promise<void>((resolve) => upstream.close(() => resolve()));
  }
});

test("prepareMcpServers tears down upstream SSE connections on client disconnect and proxy close", async () => {
  let upstreamClosedCount = 0;
  let resolveDisconnect: (() => void) | null = null;

  const upstream = http.createServer((_req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
    res.write("data: hello\n\n");
    res.on("close", () => {
      upstreamClosedCount += 1;
      resolveDisconnect?.();
    });
  });

  await new Promise<void>((resolve) => upstream.listen(0, "127.0.0.1", resolve));
  const upstreamPort = (upstream.address() as AddressInfo).port;

  const prepared = await prepareMcpServers(
    [
      {
        name: "sse",
        type: "http",
        url: `http://127.0.0.1:${upstreamPort}/mcp/sse`,
        headers: { Authorization: "Bearer pg_sse" },
      },
    ],
    { stripHttpCredentials: true },
  );

  try {
    const url = (prepared.servers[0] as { url: string }).url;

    // 1. Downstream client aborts after reading first SSE chunk -> upstream must close
    const closed1 = new Promise<void>((resolve) => { resolveDisconnect = resolve; });
    const ac1 = new AbortController();
    const res1 = await fetch(url, { signal: ac1.signal });
    const reader1 = res1.body!.getReader();
    await reader1.read();
    ac1.abort();
    await Promise.race([
      closed1,
      new Promise<never>((_, rej) => setTimeout(() => rej(new Error("Upstream SSE did not close on client disconnect")), 2000)),
    ]);
    assert.equal(upstreamClosedCount, 1);

    // 2. Proxy close() while SSE stream is open -> upstream must close
    const closed2 = new Promise<void>((resolve) => { resolveDisconnect = resolve; });
    const res2 = await fetch(url);
    const reader2 = res2.body!.getReader();
    await reader2.read();
    await prepared.close();
    await Promise.race([
      closed2,
      new Promise<never>((_, rej) => setTimeout(() => rej(new Error("Upstream SSE did not close on proxy.close()")), 2000)),
    ]);
    assert.equal(upstreamClosedCount, 2);
  } finally {
    await prepared.close();
    await new Promise<void>((resolve) => upstream.close(() => resolve()));
  }
});

test("OarBridgeServer streams prompt events and completes turns with scriptedRuntime", async () => {
  const runtime = scriptedRuntime({
    turn: async ({ input, say, think, tool }) => {
      think("considering request");
      await tool("echo_tool", JSON.stringify({ value: input }), () => "tool-ok");
      say(`Echo: ${input}`);
    },
  });

  const transport = new MemoryTransport();
  const bridge = new OarBridgeServer(transport, async () => runtime);

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 1,
    method: "session/open",
    params: { runtime: "codex", cwd: process.cwd() },
  });
  const opened = transport.responseFor(1).result as Record<string, unknown>;
  assert.ok(typeof opened.sessionId === "string" && opened.sessionId.length > 0);
  assert.equal(opened.supportsNativeSteer, true);

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 2,
    method: "session/prompt",
    params: { prompt: "hello world", correlationId: "trn_000001_non_uuid" },
  });
  const turn = transport.responseFor(2).result as Record<string, unknown>;
  assert.equal(turn.status, "completed");

  const events = transport.events();
  assert.equal(events.filter((e) => e.kind === "text_delta").map((e) => e.delta).join(""), "Echo: hello world");
  assert.equal(events.filter((e) => e.kind === "thinking_delta").map((e) => e.delta).join(""), "considering request");
  assert.equal(events.filter((e) => e.kind === "tool_call_started").length, 1);
  assert.equal(events.filter((e) => e.kind === "tool_call_completed").length, 1);

  await bridge.handleMessage({ jsonrpc: "2.0", id: 3, method: "session/close", params: {} });
  assert.deepEqual(transport.responseFor(3).result, { closed: true });
});

test("OarBridgeServer returns workerConfiguration from listModels and session readback when configureWorker is true", async () => {
  const baseRuntime = scriptedRuntime({
    turn: async ({ say }) => { say("ok"); },
  });
  const catalogRuntime = {
    ...baseRuntime,
    listModels: async () => ({
      kind: "ok" as const,
      models: [
        {
          id: "gpt-5.4",
          displayName: "GPT 5.4",
          default: true as const,
          effortLevels: ["low", "high"],
          defaultEffort: "low",
        },
        {
          id: "gpt-5.4-mini",
          displayName: "GPT 5.4 Mini",
          effortLevels: ["low"],
          defaultEffort: "low",
        },
      ],
    }),
  };

  const transport = new MemoryTransport();
  const bridge = new OarBridgeServer(transport, async () => catalogRuntime);

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 1,
    method: "session/open",
    params: { runtime: "codex", cwd: process.cwd(), configureWorker: true },
  });

  const opened = transport.responseFor(1).result as Record<string, unknown>;
  const workerConfig = opened.workerConfiguration as Record<string, unknown>;
  assert.ok(workerConfig, "Expected workerConfiguration when configureWorker=true");
  assert.equal(workerConfig.defaultModel, "gpt-5.4");
  assert.equal(workerConfig.defaultReasoningEffort, "low");
  assert.deepEqual(workerConfig.models, [
    { id: "gpt-5.4", name: "GPT 5.4", reasoningEfforts: ["low", "high"], defaultReasoningEffort: "low" },
    { id: "gpt-5.4-mini", name: "GPT 5.4 Mini", reasoningEfforts: ["low"], defaultReasoningEffort: "low" },
  ]);

  await bridge.dispose();

  // Explicit model override on reopen must preserve catalog defaultModel ("gpt-5.4").
  const overrideTransport = new MemoryTransport();
  const overrideBridge = new OarBridgeServer(overrideTransport, async () => catalogRuntime);
  await overrideBridge.handleMessage({
    jsonrpc: "2.0",
    id: 2,
    method: "session/open",
    params: { runtime: "codex", cwd: process.cwd(), model: "gpt-5.4-mini", configureWorker: true },
  });
  const overrideOpened = overrideTransport.responseFor(2).result as Record<string, unknown>;
  const overrideConfig = overrideOpened.workerConfiguration as Record<string, unknown>;
  assert.equal(overrideConfig.defaultModel, "gpt-5.4");
  assert.equal(overrideConfig.defaultReasoningEffort, "low");
  await overrideBridge.dispose();

  // Initial open where session readback reports native effort ("high") different from catalog default ("low")
  // must preserve the session's proven runtime default ("high").
  const nativeEffortRuntime = {
    ...catalogRuntime,
    session: async (installation: Parameters<typeof catalogRuntime.session>[0], options: Parameters<typeof catalogRuntime.session>[1]) => {
      const inner = await catalogRuntime.session(installation, options);
      return {
        ...inner,
        model: () => ({ ...inner.model(), value: options.model ?? "gpt-5.4" }),
        effort: () => ({ ...inner.effort(), value: options.effort ?? "high" }),
      };
    },
  };
  const nativeTransport = new MemoryTransport();
  const nativeBridge = new OarBridgeServer(nativeTransport, async () => nativeEffortRuntime);
  await nativeBridge.handleMessage({
    jsonrpc: "2.0",
    id: 3,
    method: "session/open",
    params: { runtime: "codex", cwd: process.cwd(), configureWorker: true },
  });
  const nativeOpened = nativeTransport.responseFor(3).result as Record<string, unknown>;
  const nativeConfig = nativeOpened.workerConfiguration as Record<string, unknown>;
  assert.equal(nativeOpened.effort, "high");
  assert.equal(nativeConfig.defaultModel, "gpt-5.4");
  assert.equal(nativeConfig.defaultReasoningEffort, "high");
  await nativeBridge.dispose();
});

test("OarBridgeServer supports Antigravity slash /compact and enforces timeoutMs", async () => {
  const promptsSeen: string[] = [];
  let stallNextPrompt = false;
  let abortedCount = 0;

  const fakeAgyRuntime = defineRuntime(
    {
      id: "antigravity",
      installation: async () => ({ kind: "available", via: "executable", command: "agy", version: "1.5.10" }),
      session: async () => {
        const kernel = createSessionKernel("agy-test-sess-1");
        kernel.frame({
          type: "available_commands_update",
          native: {
            update: {
              sessionUpdate: "available_commands_update",
              availableCommands: [{ name: "plan" }, { name: "compact" }],
            },
          },
          events: [],
        });

        const adapter: AdapterSession = {
          id: kernel.sessionId,
          capabilities: { queue: { durable: false }, attribution: "opaque", images: false },
          prompt: async (input, options) => {
            return await kernel.control(
              { kind: "prompt", input, ...(options?.inputId ? { inputId: options.inputId } : {}) },
              async () => {
                promptsSeen.push(input);
                if (!stallNextPrompt) {
                  queueMicrotask(() => {
                    kernel.frame({
                      type: "agent_message_chunk",
                      native: {},
                      events: [{ kind: "text_delta", text: "Compacted summary" }],
                    });
                    kernel.frame({
                      type: "session/prompt",
                      native: {},
                      events: [{ kind: "turn_ended", outcome: { kind: "completed" } }],
                    });
                  });
                }
                return { kind: "accepted" };
              },
            );
          },
          queue: async (input, options) =>
            await kernel.control(
              { kind: "queue", input, ...(options?.inputId ? { inputId: options.inputId } : {}) },
              () => ({ kind: "rejected", code: "unsupported", reason: "not needed" }),
            ),
          abort: async () => {
            abortedCount += 1;
            return await kernel.control({ kind: "abort" }, () => ({ kind: "accepted" }));
          },
          rawEvents: (observer, cursor) => kernel.rawEvents(observer, cursor),
          records: () => kernel.records(),
          graph: () => kernel.graph(),
          dispose: async () => {
            await kernel.dispose(() => {});
          },
        };

        return sealSession(adapter);
      },
    },
    { execution: "process" },
  );

  const transport = new MemoryTransport();
  const bridge = new OarBridgeServer(transport, async () => fakeAgyRuntime);

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 1,
    method: "session/open",
    params: { runtime: "antigravity", cwd: process.cwd() },
  });

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 2,
    method: "session/compact_support",
    params: { graceTimeoutMs: 500 },
  });
  assert.deepEqual(transport.responseFor(2).result, { supported: true, mode: "slash_command" });

  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 3,
    method: "session/compact",
    params: { timeoutMs: 5000 },
  });
  const compacted = transport.responseFor(3).result as Record<string, unknown>;
  assert.equal(compacted.completed, true);
  assert.equal(compacted.mode, "slash_command");
  assert.equal(compacted.summary, "Compacted summary");
  assert.deepEqual(promptsSeen, ["/compact"]);

  // Stalled slash /compact must time out cleanly and invoke abort()
  stallNextPrompt = true;
  await bridge.handleMessage({
    jsonrpc: "2.0",
    id: 4,
    method: "session/compact",
    params: { timeoutMs: 80 },
  });
  const timedOut = transport.responseFor(4).error as Record<string, unknown>;
  assert.equal(timedOut.code, -32011);
  assert.ok(abortedCount >= 1);

  await bridge.dispose();
});

test("OarBridgeServer propagates OAR_CODEX_SANDBOX to real codexRuntime process.env and launchArgs to real claudeRuntime", async () => {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "perga-oar-perm-"));
  const stubPath = path.join(tmpDir, "argv-stub.sh");
  const argvLogPath = path.join(tmpDir, "argv.txt");
  fs.writeFileSync(
    stubPath,
    `#!/bin/sh\nprintf '%s\\n' "$@" > "${argvLogPath}"\nexit 1\n`,
    { mode: 0o755 },
  );

  const readCapturedArgs = (): string[] =>
    fs.readFileSync(argvLogPath, "utf8").split(/\r?\n/u).filter((line) => line.length > 0);

  const originalCodexSandbox = process.env.OAR_CODEX_SANDBOX;
  process.env.OAR_CODEX_SANDBOX = "sentinel-before";

  try {
    // 1. Real codexRuntime must receive sandbox_mode="workspace-write" (not danger-full-access)
    const controlledCodex = {
      ...codexRuntime,
      installation: async () => ({
        kind: "available" as const,
        via: "executable" as const,
        command: stubPath,
        version: "0.171.0",
      }),
    };
    const codexTransport1 = new MemoryTransport();
    const codexBridge1 = new OarBridgeServer(codexTransport1, async () => controlledCodex);
    await codexBridge1.handleMessage({
      jsonrpc: "2.0",
      id: 1,
      method: "session/open",
      params: {
        runtime: "codex",
        cwd: tmpDir,
        env: { OAR_CODEX_SANDBOX: "workspace-write" },
      },
    });
    assert.ok(codexTransport1.responseFor(1).error, "Stub exits 1 after recording spawn argv");
    const codexWorkspaceArgs = readCapturedArgs();
    assert.ok(
      codexWorkspaceArgs.includes('sandbox_mode="workspace-write"'),
      `Expected workspace-write sandbox arg, got: ${JSON.stringify(codexWorkspaceArgs)}`,
    );
    assert.equal(process.env.OAR_CODEX_SANDBOX, "sentinel-before");
    await codexBridge1.dispose();

    // 2. Real codexRuntime with danger-full-access override
    fs.rmSync(argvLogPath, { force: true });
    const codexTransport2 = new MemoryTransport();
    const codexBridge2 = new OarBridgeServer(codexTransport2, async () => controlledCodex);
    await codexBridge2.handleMessage({
      jsonrpc: "2.0",
      id: 2,
      method: "session/open",
      params: {
        runtime: "codex",
        cwd: tmpDir,
        env: { OAR_CODEX_SANDBOX: "danger-full-access" },
      },
    });
    const codexFullArgs = readCapturedArgs();
    assert.ok(
      codexFullArgs.includes('sandbox_mode="danger-full-access"'),
      `Expected danger-full-access sandbox arg, got: ${JSON.stringify(codexFullArgs)}`,
    );
    assert.equal(process.env.OAR_CODEX_SANDBOX, "sentinel-before");
    await codexBridge2.dispose();

    // 3. Real claudeRuntime with launchArgs=["--permission-mode", "dontAsk"] omits --dangerously-skip-permissions
    fs.rmSync(argvLogPath, { force: true });
    const controlledClaude = {
      ...claudeRuntime,
      installation: async () => ({
        kind: "available" as const,
        via: "executable" as const,
        command: stubPath,
        version: "2.1.280",
      }),
    };
    const claudeTransport1 = new MemoryTransport();
    const claudeBridge1 = new OarBridgeServer(claudeTransport1, async () => controlledClaude);
    await claudeBridge1.handleMessage({
      jsonrpc: "2.0",
      id: 3,
      method: "session/open",
      params: {
        runtime: "claude",
        cwd: tmpDir,
        launchArgs: ["--permission-mode", "dontAsk"],
      },
    });
    const claudeDontAskArgs = readCapturedArgs();
    assert.ok(claudeDontAskArgs.includes("--permission-mode"), `Missing --permission-mode: ${JSON.stringify(claudeDontAskArgs)}`);
    assert.ok(claudeDontAskArgs.includes("dontAsk"), `Missing dontAsk: ${JSON.stringify(claudeDontAskArgs)}`);
    assert.ok(claudeDontAskArgs.includes("--permission-prompts"), `Missing --permission-prompts: ${JSON.stringify(claudeDontAskArgs)}`);
    assert.ok(!claudeDontAskArgs.includes("--dangerously-skip-permissions"), `Unexpected bypass flag: ${JSON.stringify(claudeDontAskArgs)}`);
    await claudeBridge1.dispose();

    // 4. Real claudeRuntime without permission launchArgs uses --dangerously-skip-permissions
    fs.rmSync(argvLogPath, { force: true });
    const claudeTransport2 = new MemoryTransport();
    const claudeBridge2 = new OarBridgeServer(claudeTransport2, async () => controlledClaude);
    await claudeBridge2.handleMessage({
      jsonrpc: "2.0",
      id: 4,
      method: "session/open",
      params: {
        runtime: "claude",
        cwd: tmpDir,
      },
    });
    const claudeBypassArgs = readCapturedArgs();
    assert.ok(
      claudeBypassArgs.includes("--dangerously-skip-permissions"),
      `Expected --dangerously-skip-permissions, got: ${JSON.stringify(claudeBypassArgs)}`,
    );
    await claudeBridge2.dispose();
  } finally {
    if (originalCodexSandbox === undefined) {
      delete process.env.OAR_CODEX_SANDBOX;
    } else {
      process.env.OAR_CODEX_SANDBOX = originalCodexSandbox;
    }
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
});
