import assert from "node:assert/strict";
import test from "node:test";
import { defineRuntime, type AdapterSession } from "@botiverse/oar/browser";
import { createSessionKernel, sealSession } from "@botiverse/oar/kernel";
import { OarBridgeServer } from "../src/bridge.ts";

test("runtime tasks and idle activity are forwarded outside prompts and status ticks coalesce", async context => {
  context.mock.timers.enable({ apis: ["Date"], now: 1000 });
  const kernel = createSessionKernel("runtime-activity");
  kernel.frame({ type: "task", native: {}, events: [{ kind: "task_started", taskId: "startup", taskType: "agent" }] });
  const mcpStatus = (name: string, status: string, error?: string, sessionId?: string) =>
    kernel.frame({ type: "mcpServer/startupStatus/updated", native: { name, status, error }, events: [] }, { sessionId });
  const sensitiveError = "socks5://alice:secret@proxy.local:1080/?key=private with foo_pg_secret123 and Bearer pg_xyz";
  mcpStatus("perga", "cancelled", `HTTP 401 ${sensitiveError}`);
  const adapter: AdapterSession = {
    id: kernel.sessionId,
    capabilities: { queue: { durable: false }, attribution: "opaque", images: false },
    prompt: async input => kernel.control({ kind: "prompt", input }, () => ({ kind: "rejected", code: "unsupported", reason: "unused" })),
    queue: async input => kernel.control({ kind: "queue", input }, () => ({ kind: "rejected", code: "unsupported", reason: "unused" })),
    abort: async () => kernel.control({ kind: "abort" }, () => ({ kind: "accepted" })),
    rawEvents: (observer, cursor) => kernel.rawEvents(observer, cursor),
    records: () => kernel.records(), graph: () => kernel.graph(),
    dispose: async () => { await kernel.dispose(() => {}); },
  };
  const runtime = defineRuntime({ id: "codex",
    installation: async () => ({ kind: "available", via: "executable", command: "unused", version: "1" }),
    session: async (_, options) => {
      for (const key of ["NO_PROXY", "no_proxy"]) {
        assert.deepEqual(options.env?.[key]?.split(","), ["example.test", "localhost", "127.0.0.1", "::1"]);
      }
      return sealSession(adapter);
    },
  }, { execution: "process" });
  const messages: Record<string, unknown>[] = [];
  const bridge = new OarBridgeServer({ send: message => messages.push(message) }, async () => runtime);
  try {
    await bridge.handleMessage({ id: 1, method: "session/open", params: { runtime: "codex", cwd: process.cwd(),
      env: { HTTP_PROXY: "http://proxy.invalid", ALL_PROXY: "http://proxy.invalid", NO_PROXY: "example.test", no_proxy: "example.test" },
      mcpServers: [{ name: "perga", type: "http", url: "http://127.0.0.1/mcp/kernel" }],
    } });
    const opened = messages.find(message => message.id === 1)?.result as Record<string, unknown>;
    assert.ok(opened, JSON.stringify(messages.find(message => message.id === 1)));
    assert.equal((opened.agentStatus as { kind: string }).kind, "idle");
    mcpStatus("perga", "failed", `HTTP 502 ${sensitiveError}`);
    mcpStatus("perga", "failed", `configured proxy error at ${sensitiveError}`);
    mcpStatus("perga", "failed", sensitiveError, "child");
    mcpStatus("vcs", "failed", "other server");
    mcpStatus("perga", "ready", undefined, "child");
    mcpStatus("perga", "starting");
    mcpStatus("perga", "ready");
    mcpStatus("perga", "ready");
    const statusesOfMcp = messages.map(message => message.params as Record<string, unknown> | undefined)
      .filter(event => event?.kind === "mcp_status");
    assert.deepEqual(statusesOfMcp, [
      { kind: "mcp_status", server: "perga", status: "failed", error: "MCP startup was cancelled." },
      { kind: "mcp_status", server: "perga", status: "failed", error: "MCP server returned HTTP 502." },
      { kind: "mcp_status", server: "perga", status: "failed", error: "Failed to connect through the configured proxy." },
    ]);
    kernel.frame({ type: "task", native: {}, events: [
      { kind: "task_started", taskId: "task", taskType: "shell", background: true, ambient: false },
      { kind: "task_updated", taskId: "task", description: "progress", status: "running" },
      { kind: "task_ended", taskId: "task", status: "completed", outputFile: "/tmp/output", summary: "done" },
    ] });
    for (let index = 0; index < 20; index++)
      kernel.frame({ type: "text", native: {}, events: [{ kind: "text_delta", text: "tick" }] });
    const events = messages.filter(message => message.method === "oar/event").map(message => message.params as Record<string, unknown>);
    assert.ok(events.some(event => event.kind === "task_started" && event.taskId === "startup"));
    assert.deepEqual(events.filter(event => event.taskId === "task").map(event => event.kind),
      ["task_started", "task_updated", "task_ended"]);
    assert.equal(events.find(event => event.kind === "task_ended")?.outputFile, "/tmp/output");
    const statuses = events.filter(event => event.kind === "agent_status").map(event => event.status as {kind: string; activity?: unknown});
    assert.ok(statuses.some(status => status.kind === "idle" && status.activity));
    assert.ok(statuses.length <= 3, `unexpected status spam: ${statuses.length}`);
    const count = statuses.length;
    context.mock.timers.tick(1001);
    kernel.frame({ type: "text", native: {}, events: [{ kind: "text_delta", text: "later" }] });
    assert.equal(messages.filter(message => message.method === "oar/event" &&
      (message.params as {kind: string}).kind === "agent_status").length, count + 1);
    await kernel.control({ kind: "prompt", input: "hello" }, () => ({ kind: "accepted" }));
    kernel.frame({ type: "thinking", native: {}, events: [{ kind: "reasoning", content: { kind: "text", text: "thinking" } }] });
    for (let index = 0; index < 2000; index++)
      kernel.frame({ type: "text", native: {}, events: [{ kind: "text_delta", text: "tick" }] });
    const running = messages.filter(message => message.method === "oar/event")
      .map(message => message.params as {kind: string; status?: {kind: string; phase?: string; activity?: unknown}})
      .filter(event => event.kind === "agent_status" && event.status?.kind === "running");
    assert.ok(running.some(event => event.status?.phase === "thinking"));
    assert.equal(running.filter(event => event.status?.phase === "responding").length, 1);
    kernel.frame({ type: "done", native: {}, events: [{ kind: "turn_ended", outcome: { kind: "completed" } }] });
    const latest = messages.filter(message => message.method === "oar/event" &&
      (message.params as {kind: string}).kind === "agent_status").at(-1)?.params as {status: {kind: string; activity?: unknown}};
    assert.equal(latest.status.kind, "idle");
    assert.equal(latest.status.activity, undefined);
  } finally { await bridge.dispose(); }
});
