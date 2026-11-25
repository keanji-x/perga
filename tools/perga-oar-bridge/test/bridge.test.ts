import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { setTimeout as delay } from "node:timers/promises";
import { pathToFileURL } from "node:url";
import type { AddressInfo } from "node:net";
import { defineRuntime, type AdapterSession } from "@botiverse/oar/browser";
import { createSessionKernel, sealSession } from "@botiverse/oar/kernel";
import { antigravityRuntime } from "@botiverse/oar/runtimes/antigravity";
import { claudeRuntime } from "@botiverse/oar/runtimes/claude";
import { codexRuntime } from "@botiverse/oar/runtimes/codex";
import { opencodeRuntime } from "@botiverse/oar/runtimes/opencode";
import { scriptedRuntime } from "@botiverse/oar/testing";
import { OarBridgeServer, type BridgeTransport } from "../src/bridge.ts";
import { prepareMcpServers } from "../src/mcp-proxy.ts";
import { preparePromptImages } from "../src/prompt-images.ts";

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

test("OarBridgeServer returns sessionConfiguration from listModels and session readback for every session", async () => {
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
    params: { runtime: "codex", cwd: process.cwd() },
  });

  const opened = transport.responseFor(1).result as Record<string, unknown>;
  const workerConfig = opened.sessionConfiguration as Record<string, unknown>;
  assert.ok(workerConfig, "Expected sessionConfiguration for every session");
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
    params: { runtime: "codex", cwd: process.cwd(), model: "gpt-5.4-mini" },
  });
  const overrideOpened = overrideTransport.responseFor(2).result as Record<string, unknown>;
  const overrideConfig = overrideOpened.sessionConfiguration as Record<string, unknown>;
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
    params: { runtime: "codex", cwd: process.cwd() },
  });
  const nativeOpened = nativeTransport.responseFor(3).result as Record<string, unknown>;
  const nativeConfig = nativeOpened.sessionConfiguration as Record<string, unknown>;
  assert.equal(nativeOpened.effort, "high");
  assert.equal(nativeConfig.defaultModel, "gpt-5.4");
  assert.equal(nativeConfig.defaultReasoningEffort, "high");
  await nativeBridge.dispose();
});

test("OarBridgeServer supports Antigravity slash /compact and enforces timeoutMs", async () => {
  const promptsSeen: string[] = [];
  let stallNextPrompt = false;
  let abortedCount = 0;
  let emitBackgroundTools: (() => void) | undefined;

  const fakeAgyRuntime = defineRuntime(
    {
      id: "antigravity",
      installation: async () => ({ kind: "available", via: "executable", command: "agy", version: "1.5.10" }),
      session: async () => {
        const kernel = createSessionKernel("agy-test-sess-1");
        emitBackgroundTools = () => kernel.frame({
          type: "tool_call_update",
          native: {},
          events: [
            { kind: "tool_call_input", callId: "background", input: "{}" },
            { kind: "tool_call_input", callId: "background", input: "{}" },
            { kind: "tool_call_ended", callId: "background", result: "ok" },
            { kind: "tool_call_input", callId: "background", input: "{}" },
          ],
        });
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

  emitBackgroundTools?.();
  assert.deepEqual(transport.events().filter((event) => event.callId === "background").map((event) => event.kind),
    ["tool_call_started", "tool_call_completed", "tool_call_started"]);

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
    `#!/bin/sh
printf '%s\\n' "$@" > "${argvLogPath}.tmp"
mv "${argvLogPath}.tmp" "${argvLogPath}"
exit 1
`,
    { mode: 0o755 },
  );

  const readCapturedArgs = async (): Promise<string[]> => {
    // Opening a session can resolve as soon as the OS spawns the stub.
    const deadline = Date.now() + 5000;
    while (!fs.existsSync(argvLogPath) && Date.now() < deadline) await delay(10);
    return fs.readFileSync(argvLogPath, "utf8").trimEnd().split(/\r?\n/u);
  };

  const originalCodexSandbox = process.env.OAR_CODEX_SANDBOX;
  process.env.OAR_CODEX_SANDBOX = "sentinel-before";

  try {
    // 1. Real codexRuntime must receive sandbox_mode="workspace-write" (not danger-full-access)
    const controlledCodex = {
      ...codexRuntime,
      // Only session launches should write to the argv capture shared by these cases.
      listModels: undefined,
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
    const codexWorkspaceArgs = await readCapturedArgs();
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
    const codexFullArgs = await readCapturedArgs();
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
      listModels: undefined,
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
    const claudeDontAskArgs = await readCapturedArgs();
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
    const claudeBypassArgs = await readCapturedArgs();
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

test("OarBridgeServer delivers image-only prompts and steers to the correlated runtime turn", async () => {
  const payload = { mediaType: "image/png", data: Buffer.from([1, 2, 3, 4]).toString("base64") };
  let releasePrompt!: () => void;
  const hold = new Promise<void>(resolve => { releasePrompt = resolve; });
  let promptStarted!: (value: string) => void;
  const started = new Promise<string>(resolve => { promptStarted = resolve; });
  let steerPath: string | undefined;
  const runtime = scriptedRuntime({
    turn: async ({ input, images }) => {
      assert.equal(input, "");
      assert.equal(images.length, 1);
      assert.equal(images[0]?.mediaType, "image/png");
      assert.deepEqual(fs.readFileSync(images[0]!.path), Buffer.from(payload.data, "base64"));
      promptStarted(images[0]!.path);
      await hold;
    },
  });
  const imageRuntime = {
    ...runtime,
    session: async (...args: Parameters<NonNullable<typeof runtime.session>>) => {
      const inner = await runtime.session!(...args);
      return {
        ...inner,
        capabilities: { ...inner.capabilities, images: true },
        steer: async (...args: Parameters<NonNullable<typeof inner.steer>>) => {
          steerPath = args[1]?.images?.[0]?.path;
          assert.ok(steerPath);
          assert.deepEqual(fs.readFileSync(steerPath), Buffer.from(payload.data, "base64"));
          return await inner.steer!(...args);
        },
      };
    },
  };
  const transport = new MemoryTransport();
  const bridge = new OarBridgeServer(transport, async () => imageRuntime);
  const call = (id: number, method: string, params: Record<string, unknown>) =>
    bridge.handleMessage({ id, method, params });
  try {
    await call(1, "session/open", { runtime: "codex", cwd: process.cwd() });
    assert.equal((transport.responseFor(1).result as Record<string, unknown>).supportsImages, true);
    const prompt = call(2, "session/prompt", { prompt: "", correlationId: "current", images: [payload] });
    const promptPath = await started;
    assert.ok(fs.existsSync(promptPath));
    if (process.platform !== "win32") assert.equal(fs.statSync(promptPath).mode & 0o777, 0o600);
    await call(3, "session/steer", { message: "", correlationId: "stale", images: [payload] });
    assert.deepEqual(transport.responseFor(3).result, { delivery: "declined" });
    assert.equal(steerPath, undefined);
    await call(4, "session/steer", { message: "", correlationId: "current", images: [payload] });
    assert.deepEqual(transport.responseFor(4).result, { delivery: "accepted" });
    assert.ok(steerPath && fs.existsSync(steerPath));
    releasePrompt();
    await prompt;
    assert.equal((transport.responseFor(2).result as Record<string, unknown>).status, "completed");
    assert.equal(fs.existsSync(promptPath), false);
    assert.equal(fs.existsSync(steerPath), false);
  } finally {
    releasePrompt();
    await bridge.dispose();
  }
});

test("OarBridgeServer refuses images before calling an unnegotiated runtime", async () => {
  let calls = 0;
  const runtime = scriptedRuntime({ turn: () => { calls++; } });
  const unsupported = {
    ...runtime,
    session: async (...args: Parameters<NonNullable<typeof runtime.session>>) => {
      const inner = await runtime.session!(...args);
      return { ...inner, capabilities: { ...inner.capabilities, images: false } };
    },
  };
  const transport = new MemoryTransport();
  const bridge = new OarBridgeServer(transport, async () => unsupported);
  try {
    await bridge.handleMessage({ id: 1, method: "session/open", params: { runtime: "antigravity", cwd: process.cwd() } });
    assert.equal((transport.responseFor(1).result as Record<string, unknown>).supportsImages, false);
    const payload = { mediaType: "image/png", data: "AQIDBA==" };
    for (const [id, method] of [[2, "session/prompt"], [3, "session/steer"]] as const) {
      await bridge.handleMessage({ id, method, params: { images: [payload], correlationId: "turn" } });
      const refusal = transport.responseFor(id).result as Record<string, unknown>;
      assert.equal(refusal.code, "attachments_unsupported");
      assert.equal(refusal.beforePrompt, true);
    }
    assert.equal(calls, 0);
  } finally {
    await bridge.dispose();
  }
});

test("preparePromptImages removes its directory after a later image fails", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "perga-image-cleanup-test-"));
  const tempVariable = process.platform === "win32" ? "TEMP" : "TMPDIR";
  const previous = process.env[tempVariable];
  process.env[tempVariable] = root;
  try {
    const first = { mediaType: "image/png", data: "AQIDBA==" };
    const invalid = {
      get mediaType() {
        const entries = fs.readdirSync(root);
        assert.equal(entries.length, 1);
        const written = path.join(root, entries[0]!, "0.png");
        assert.deepEqual(fs.readFileSync(written), Buffer.from(first.data, "base64"));
        return "image/unsupported";
      },
      data: first.data,
    };
    await assert.rejects(preparePromptImages([first, invalid]), { message: "Unsupported image type." });
    assert.deepEqual(fs.readdirSync(root), []);
  } finally {
    if (previous === undefined) delete process.env[tempVariable];
    else process.env[tempVariable] = previous;
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("OarBridgeServer resumes Antigravity percent-encoded Unicode cwd while isolating sibling literal %XX and different directories", async () => {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "perga-agy-resume-"));
  const shQuote = (value: string): string => `'${value.replace(/'/gu, "'\\''")}'`;

  try {
    const unicodeDir = path.join(tmpDir, "小米");
    const literalPctDir = path.join(tmpDir, "%E5%B0%8F%E7%B1%B3");
    const otherDir = path.join(tmpDir, "other");
    fs.mkdirSync(unicodeDir);
    fs.mkdirSync(literalPctDir);
    fs.mkdirSync(otherDir);

    const resumeLogPath = path.join(tmpDir, "resumes.jsonl");
    const stubJsPath = path.join(tmpDir, "agy-acp-stub.mjs");
    const stubBinPath = path.join(tmpDir, "agy-acp-stub.sh");
    const sessions = [
      { sessionId: "sess-unicode", cwd: pathToFileURL(unicodeDir).pathname },
      { sessionId: "sess-literal", cwd: pathToFileURL(literalPctDir).pathname },
    ];

    fs.writeFileSync(
      stubJsPath,
      `import fs from "node:fs";
import readline from "node:readline";

const sessions = ${JSON.stringify(sessions)};
const resumeLogPath = ${JSON.stringify(resumeLogPath)};
const send = (msg) => process.stdout.write(JSON.stringify(msg) + "\\n");

readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on("line", (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  const req = JSON.parse(trimmed);
  if (req.id === undefined) return;
  if (req.method === "initialize") {
    send({
      jsonrpc: "2.0",
      id: req.id,
      result: {
        protocolVersion: req.params?.protocolVersion ?? 1,
        agentCapabilities: {
          loadSession: true,
          sessionCapabilities: { list: {}, resume: {}, close: {} },
        },
      },
    });
    return;
  }
  if (req.method === "session/list") {
    send({ jsonrpc: "2.0", id: req.id, result: { sessions } });
    return;
  }
  if (req.method === "session/resume" || req.method === "session/load") {
    fs.appendFileSync(resumeLogPath, JSON.stringify({ method: req.method, params: req.params }) + "\\n");
    send({ jsonrpc: "2.0", id: req.id, result: {} });
    return;
  }
  if (req.method === "session/close") {
    send({ jsonrpc: "2.0", id: req.id, result: {} });
    return;
  }
  send({ jsonrpc: "2.0", id: req.id, error: { code: -32601, message: "Method not found: " + req.method } });
});
`,
    );

    fs.writeFileSync(
      stubBinPath,
      `#!/bin/sh\nexec ${shQuote(process.execPath)} ${shQuote(stubJsPath)} "$@"\n`,
      { mode: 0o755 },
    );

    const controlledAgy = {
      ...antigravityRuntime,
      listModels: undefined,
      installation: async () => ({
        kind: "available" as const,
        via: "executable" as const,
        command: stubBinPath,
        version: "1.5.16",
      }),
    };

    const readResumes = (): Array<{ method: string; params: Record<string, unknown> }> => {
      if (!fs.existsSync(resumeLogPath)) return [];
      const content = fs.readFileSync(resumeLogPath, "utf8").trim();
      return content ? content.split(/\r?\n/u).map((line) => JSON.parse(line) as { method: string; params: Record<string, unknown> }) : [];
    };

    const openResume = async (cwd: string, resume: string): Promise<Record<string, unknown>> => {
      const transport = new MemoryTransport();
      const bridge = new OarBridgeServer(transport, async () => controlledAgy);
      try {
        await bridge.handleMessage({
          jsonrpc: "2.0",
          id: 1,
          method: "session/open",
          params: { runtime: "antigravity", cwd, resume },
        });
        return transport.responseFor(1);
      } finally {
        await bridge.dispose();
      }
    };

    // 1. Percent-encoded Unicode listing resumes cleanly in its unencoded directory.
    const unicodeRes = await openResume(unicodeDir, "sess-unicode");
    assert.equal(unicodeRes.error, undefined, `Expected Unicode resume to succeed, got: ${JSON.stringify(unicodeRes.error)}`);
    assert.equal((unicodeRes.result as Record<string, unknown>).sessionId, "sess-unicode");

    // 2. Sibling literal %E5%B0%8F%E7%B1%B3 listing (encoded as %25...) resumes cleanly in its own directory.
    const literalRes = await openResume(literalPctDir, "sess-literal");
    assert.equal(literalRes.error, undefined, `Expected literal %XX resume to succeed, got: ${JSON.stringify(literalRes.error)}`);
    assert.equal((literalRes.result as Record<string, unknown>).sessionId, "sess-literal");

    const acceptedResumes = readResumes();
    assert.equal(acceptedResumes.length, 2);
    assert.equal(acceptedResumes[0]!.method, "session/resume");
    assert.equal(acceptedResumes[0]!.params.sessionId, "sess-unicode");
    assert.equal(acceptedResumes[0]!.params.cwd, unicodeDir);
    assert.equal(acceptedResumes[1]!.method, "session/resume");
    assert.equal(acceptedResumes[1]!.params.sessionId, "sess-literal");
    assert.equal(acceptedResumes[1]!.params.cwd, literalPctDir);

    // 3. Unicode session must not resume in sibling literal %E5%B0%8F%E7%B1%B3 directory.
    const crossToLiteral = await openResume(literalPctDir, "sess-unicode");
    const crossToLiteralErr = crossToLiteral.error as Record<string, unknown> | undefined;
    assert.ok(crossToLiteralErr, "Expected cross-directory resume to literal %XX sibling to be refused");
    assert.deepEqual(crossToLiteralErr.data, { errorName: "UnsupportedOptionError", option: "cwd" });

    // 4. Literal %E5%B0%8F%E7%B1%B3 session must not double-decode and resume in Unicode directory.
    const crossToUnicode = await openResume(unicodeDir, "sess-literal");
    const crossToUnicodeErr = crossToUnicode.error as Record<string, unknown> | undefined;
    assert.ok(crossToUnicodeErr, "Expected literal %XX session resume in Unicode sibling to be refused");
    assert.deepEqual(crossToUnicodeErr.data, { errorName: "UnsupportedOptionError", option: "cwd" });

    // 5. Genuinely different directory must continue to be rejected before session/resume.
    const diffDirRes = await openResume(otherDir, "sess-unicode");
    const diffDirErr = diffDirRes.error as Record<string, unknown> | undefined;
    assert.ok(diffDirErr, "Expected different directory resume to be refused");
    assert.deepEqual(diffDirErr.data, { errorName: "UnsupportedOptionError", option: "cwd" });

    // None of the three refused attempts may reach native session/resume.
    assert.equal(readResumes().length, 2);
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
});

test("OarBridgeServer probe surfaces OpenCode v2 per-model display names and effortLevels", async () => {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "perga-opencode-probe-"));
  const shQuote = (value: string): string => `'${value.replace(/'/gu, "'\\''")}'`;

  try {
    const workspaceDir = path.join(tmpDir, "workspace");
    fs.mkdirSync(workspaceDir);

    const stubJsPath = path.join(tmpDir, "opencode-v2-stub.mjs");
    const stubBinPath = path.join(tmpDir, "opencode-v2-stub.sh");

    fs.writeFileSync(
      stubJsPath,
      `import http from "node:http";

const args = process.argv.slice(2);
if (args[0] === "--version") {
  process.stdout.write("opencode v2.1.4\\n");
  process.exit(0);
}
if (args[0] === "--help") {
  process.stdout.write("OpenCode command line interface\\n");
  process.exit(0);
}
if (args[0] === "models" && args[1] === "--standalone") {
  process.stdout.write("opencode/plain\\nzhipuai-coding-plan/glm-5.4\\n");
  process.exit(0);
}
if (args[0] === "serve" && args.includes("--stdio")) {
  const password = process.env.OPENCODE_PASSWORD ?? "";
  const expectedAuth = "Basic " + Buffer.from("opencode:" + password).toString("base64");
  const expectedDir = encodeURIComponent(${JSON.stringify(workspaceDir)});
  const server = http.createServer((req, res) => {
    if (req.headers.authorization !== expectedAuth || req.headers["x-opencode-directory"] !== expectedDir) {
      res.writeHead(401, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: "unauthorized" }));
      return;
    }
    const pathname = new URL(req.url ?? "/", "http://127.0.0.1").pathname;
    if (pathname === "/api/plugin") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({ data: [{ id: "builtin", state: { status: "active" } }] }));
      return;
    }
    if (pathname === "/api/model") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        data: [
          {
            providerID: "zhipuai-coding-plan",
            id: "glm-5.4",
            name: "GLM-5.4",
            variants: [{ id: "low" }, { id: "default" }, { id: "high" }, { id: "max" }],
          },
          {
            providerID: "opencode",
            id: "plain",
            name: "Plain",
            variants: [{ id: "default" }],
          },
        ],
      }));
      return;
    }
    res.writeHead(404);
    res.end();
  });
  server.listen(0, "127.0.0.1", () => {
    const addr = server.address();
    const port = typeof addr === "object" && addr ? addr.port : 0;
    process.stdout.write(JSON.stringify({ url: "http://127.0.0.1:" + port }) + "\\n");
  });
  process.stdin.resume();
  process.stdin.on("end", () => {
    server.closeAllConnections?.();
    server.close(() => process.exit(0));
  });
} else {
  process.stderr.write("unexpected args: " + JSON.stringify(args) + "\\n");
  process.exit(1);
}
`,
    );

    fs.writeFileSync(
      stubBinPath,
      `#!/bin/sh\nexec ${shQuote(process.execPath)} ${shQuote(stubJsPath)} "$@"\n`,
      { mode: 0o755 },
    );

    const transport = new MemoryTransport();
    const bridge = new OarBridgeServer(transport, async () => opencodeRuntime);
    try {
      await bridge.handleMessage({
        jsonrpc: "2.0",
        id: 1,
        method: "probe",
        params: {
          runtime: "opencode",
          executable: stubBinPath,
          cwd: workspaceDir,
          timeoutMs: 10_000,
        },
      });
      const res = transport.responseFor(1);
      assert.equal(res.error, undefined, `Expected probe to succeed, got: ${JSON.stringify(res.error)}`);
      const result = res.result as {
        available: boolean;
        version: string | null;
        reason: string | null;
        models: Array<{
          id: string;
          displayName: string;
          defaultEffort: string | null;
          effortLevels: string[];
        }>;
      };
      assert.equal(result.available, true, `Expected probe available=true, got reason=${result.reason}`);
      assert.equal(result.version, "opencode v2.1.4");
      assert.equal(result.reason, null);
      assert.deepEqual(
        result.models.map((m) => ({
          id: m.id,
          displayName: m.displayName,
          defaultEffort: m.defaultEffort,
          effortLevels: m.effortLevels,
        })),
        [
          {
            id: "opencode/plain",
            displayName: "Plain",
            defaultEffort: null,
            effortLevels: [],
          },
          {
            id: "zhipuai-coding-plan/glm-5.4",
            displayName: "GLM-5.4",
            defaultEffort: null,
            effortLevels: ["low", "high", "max"],
          },
        ],
      );
    } finally {
      await bridge.dispose();
    }
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
});
