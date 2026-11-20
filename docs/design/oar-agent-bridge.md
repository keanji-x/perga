# Perga OAR Agent Bridge Design

## Goal

Replace all hand-maintained CLI/ACP provider adapters and process-level `bwrap` / `OverlayMounts` wrappers in `Perga.AgentKit` with a thin, clean `IAgentRuntime` / `IAgentSession` isolation boundary backed solely by [`@botiverse/oar`](https://github.com/botiverse/oar) (`>= 0.124.0`, Node.js 24+).

### Core Requirements

1. **Single authoritative runtime**: Every provider (`codex`, `claude`, `acp:antigravity`, `acp:opencode`, and generic `oar:<runtimeId>`) runs through `OarAgentSession` + `tools/perga-oar-bridge`.
2. **Node.js 24+ only**: Require Node `>= 24.0.0` (no Node 22 compatibility).
3. **Compaction across Codex, OpenCode, Antigravity (AGY), and Claude**:
   - **Codex / OpenCode v2 / Claude**: OAR native `session.compact()`.
   - **Antigravity (AGY)**: Observe ACP `available_commands_update` via `session.rawEvents()` and send `session.prompt("/compact")` when `"compact"` is advertised; otherwise `TrackActor` falls back to Epoch Handover.
4. **Zero credential leakage on Antigravity MCP**:
   - `tools/perga-oar-bridge/src/mcp-proxy.ts` starts an ephemeral `127.0.0.1:0` HTTP reverse proxy per session for runtimes refusing `mcpServerCredentials`, injecting `Authorization` headers in flight so bearer tokens never reach AGY's conversation SQLite DB.
5. **Workspace-level isolation**:
   - Isolation is owned by Perga's workspace drivers (`direct` / `vcs` / `kernel-overlayfs`). Legacy `OsSandboxWrapper` and `HarnessLaunchSpec.OverlayMounts` are removed.

## Architecture

```text
Perga.Kernel (IAgentRuntime / IAgentSession)
        │
        ▼
Perga.AgentKit (clean interface boundary + OAR stdio client)
  ├── Contracts.cs / SessionConfiguration.cs / WorkerProviderConfiguration.cs
  ├── AgentRuntime.cs + OarAgentSession.cs + Internal/OarNodeResolver.cs
  └── Internal/StdioSubprocess.cs + Internal/JsonRpcStdioConnection.cs
        │  NDJSON JSON-RPC 2.0 over stdin/stdout
        ▼
tools/perga-oar-bridge (Node 24+, single-file ESM bundle dist/perga-oar-bridge.mjs)
  ├── runtime-loader.ts (lazy @botiverse/oar/runtimes/<id> imports)
  ├── mcp-proxy.ts      (per-session 127.0.0.1:0 header-injecting HTTP/SSE proxy)
  └── bridge.ts         (RPC dispatch + OAR Event/RawEvent projection + AGY /compact)
```

## JSON-RPC 2.0 Wire Protocol

- `probe`: `{ runtime, cwd, executable?, timeoutMs? }` -> `{ runtime, available, version, reason, supportsNativeSteer, supportsManualCompaction, models[] }`
- `session/open`: `{ runtime, cwd, model?, effort?, resume?, systemPrompt?, executable?, mcpServers[], env?, launchArgs? }` -> `{ sessionId, model, effort, supportsNativeSteer, supportsNativeCompact, supportsSessionResume, contextUsage? }`
  - Codex permission policy (`never` / `full`) is passed via `env.OAR_CODEX_SANDBOX` (`workspace-write` / `danger-full-access`) and applied to `process.env.OAR_CODEX_SANDBOX` for `codexRuntime.session(...)`.
  - Claude permission policy (`dontAsk` / `acceptEdits` / `plan` / `bypassPermissions`) is enforced via `launchArgs: ["--permission-mode", ...]` (or omitted for OAR's default `--dangerously-skip-permissions`).
- `session/prompt`: `{ prompt, correlationId }` -> `{ status: "completed"|"aborted"|"failed", usage?, error? }`
- `session/steer`: `{ message, correlationId? }` -> `{ delivery: "accepted"|"declined" }`
- `session/abort`: `{}` -> `{ accepted }`
- `session/compact_support`: `{ graceTimeoutMs? }` -> `{ supported, mode: "native"|"slash_command"|"unsupported" }`
- `session/compact`: `{ timeoutMs? }` -> `{ completed, summary, usage? }`
- `session/close`: `{}` -> `{ closed: true }`
- Notification `oar/event`: `{ kind: "text_delta"|"thinking_delta"|"tool_call_started"|"tool_call_completed"|"context_usage"|"model_changed"|"exited", ... }`
