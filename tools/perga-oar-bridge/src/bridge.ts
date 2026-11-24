import { awaitTurnEnd, eventsOf, initialStatus, reduceStatus, type StatusState } from "@botiverse/oar/observe";
import {
  type Event,
  type AgentStatus,
  type InstallationSnapshot,
  type InputImage,
  type RawEvent,
  type Runtime,
  type Session,
  type SessionOptions,
  UnsupportedOptionError,
} from "@botiverse/oar/browser";
import {
  asNumber,
  asRecord,
  asString,
  buildUsagePayload,
  flattenToolOutput,
  inspectAcpAvailableCommands,
  installationReason,
  installationVersion,
  listEnabledModels,
  parseToolArgs,
  rpcError,
  statusEmissionKey,
  toValidInputId,
  withCodexSandboxEnv,
} from "./bridge-helpers.ts";
import { preparePromptImages } from "./prompt-images.ts";
import { prepareMcpServers, type RawMcpServerInput } from "./mcp-proxy.ts";
import { buildExecutableEnv, loadOarRuntime, normalizeRuntimeId } from "./runtime-loader.ts";

export type RuntimeLoader = (runtimeId: string) => Promise<Runtime>;

export interface JsonRpcRequest {
  readonly jsonrpc?: string;
  readonly id?: string | number | null;
  readonly method: string;
  readonly params?: Record<string, unknown>;
}

export interface BridgeTransport {
  send(message: Record<string, unknown>): void;
}

interface ActiveTurnCollector {
  readonly captureText: boolean;
  readonly correlationId: string | undefined;
  readonly imageCleanups: Array<() => Promise<void>>;
  text: string;
}

function imageRefusal(code: "attachments_unsupported" | "attachment_unavailable") {
  return {
    code,
    message: code === "attachments_unsupported"
      ? "The provider session does not support image attachments."
      : "The runtime host could not prepare image attachments.",
    beforePrompt: true,
  };
}

const DEFAULT_COMPACT_GRACE_MS = 2_000;
const DEFAULT_COMPACT_TIMEOUT_MS = 120_000;
const DEFAULT_PROBE_TIMEOUT_MS = 30_000;

export class OarBridgeServer {
  private readonly _transport: BridgeTransport;
  private readonly _loadRuntime: RuntimeLoader;

  private _runtimeId: string | null = null;
  private _session: Session | null = null;
  private _closeMcpProxy: (() => Promise<void>) | null = null;
  private _unsubEvents: (() => void) | null = null;
  private _unsubRaw: (() => void) | null = null;
  private _pendingSystemPrompt: string | null = null;
  private readonly _startedTools = new Set<string>();
  private _activeTurn: ActiveTurnCollector | null = null;
  private _lastAgentStatus: AgentStatus | null = null;
  private _statusState: StatusState = initialStatus;

  private _slashCompactAdvertised = false;
  private _availableCommandsObserved = false;
  private _commandsWaiters: Array<() => void> = [];
  private _disposed = false;

  constructor(transport: BridgeTransport, loadRuntime: RuntimeLoader = loadOarRuntime) {
    this._transport = transport;
    this._loadRuntime = loadRuntime;
  }

  public async handleMessage(msg: JsonRpcRequest): Promise<void> {
    const hasId = msg.id !== undefined && msg.id !== null;
    try {
      const result = await this.dispatch(msg.method, msg.params ?? {});
      if (hasId) {
        this._transport.send({ jsonrpc: "2.0", id: msg.id, result });
      }
    } catch (err) {
      if (!hasId) {
        return;
      }
      const code = typeof (err as { code?: unknown })?.code === "number"
        ? (err as { code: number }).code
        : -32000;
      const message = err instanceof Error ? err.message : String(err);
      const data = err instanceof UnsupportedOptionError
        ? { errorName: err.name, option: err.option }
        : err instanceof Error
          ? { errorName: err.name }
          : undefined;
      this._transport.send({
        jsonrpc: "2.0",
        id: msg.id,
        error: { code, message, ...(data ? { data } : {}) },
      });
    }
  }

  public async dispose(): Promise<void> {
    if (this._disposed) {
      return;
    }
    this._disposed = true;
    await this.closeSessionInternal();
  }

  private emitEvent(payload: Record<string, unknown>): void {
    if (this._disposed) {
      return;
    }
    this._transport.send({
      jsonrpc: "2.0",
      method: "oar/event",
      params: payload,
    });
  }

  private async dispatch(method: string, params: Record<string, unknown>): Promise<Record<string, unknown>> {
    switch (method) {
      case "probe":
        return await this.handleProbe(params);
      case "session/open":
        return await this.handleSessionOpen(params);
      case "session/prompt":
        return await this.handleSessionPrompt(params);
      case "session/steer":
        return await this.handleSessionSteer(params);
      case "session/abort":
        return await this.handleSessionAbort();
      case "session/compact_support":
        return await this.handleCompactSupport(params);
      case "session/compact":
        return await this.handleSessionCompact(params);
      case "session/close":
        await this.closeSessionInternal();
        return { closed: true };
      default:
        throw rpcError(-32601, `Unknown method '${method}'.`);
    }
  }

  private async resolveInstallation(
    runtime: Runtime,
    env: Readonly<Record<string, string | null>> | undefined,
  ): Promise<InstallationSnapshot> {
    if (runtime.installation) {
      return await runtime.installation(env ? { env } : undefined);
    }
    return { kind: "available", via: "bundled" };
  }

  private async handleProbe(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    const rawRuntime = asString(params.runtime);
    if (!rawRuntime) {
      throw new Error("probe requires 'runtime'.");
    }
    const runtimeId = normalizeRuntimeId(rawRuntime);
    const runtime = await this._loadRuntime(runtimeId);
    const executable = asString(params.executable);
    const cwd = asString(params.cwd) ?? process.cwd();
    const timeoutMs = asNumber(params.timeoutMs) ?? DEFAULT_PROBE_TIMEOUT_MS;
    const env = buildExecutableEnv(runtimeId, executable);

    const installation = await this.resolveInstallation(runtime, env);
    if (installation.kind !== "available") {
      return {
        runtime: runtimeId,
        available: false,
        version: "installedVersion" in installation ? (installation.installedVersion ?? null) : null,
        reason: installationReason(installation),
        supportsNativeSteer: false,
        supportsManualCompaction: false,
        models: [],
      };
    }

    const caps = runtime.sessionCapabilities?.(installation);
    const manualCompaction = caps?.manualCompaction ?? runtime.manualCompaction;
    const supportsManualCompaction =
      manualCompaction?.kind === "supported" || runtimeId === "antigravity";

    const { models: enabledModels, reason: probeReason } = await listEnabledModels(
      runtime,
      installation,
      cwd,
      timeoutMs,
      env,
    );

    return {
      runtime: runtimeId,
      available: true,
      version: installationVersion(installation),
      reason: probeReason,
      supportsNativeSteer: caps?.steer === true,
      supportsImages: caps?.images ?? null,
      supportsManualCompaction,
      models: enabledModels.map((m) => ({
        id: m.id,
        displayName: m.displayName,
        description: m.providerName,
        isDefault: m.isDefault,
        defaultEffort: m.defaultEffort,
        effortLevels: [...m.effortLevels],
      })),
    };
  }

  private async handleSessionOpen(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    if (this._session !== null) {
      throw new Error("A session is already open in this bridge process.");
    }

    const rawRuntime = asString(params.runtime);
    const cwd = asString(params.cwd);
    if (!rawRuntime || !cwd) {
      throw new Error("session/open requires 'runtime' and 'cwd'.");
    }

    const runtimeId = normalizeRuntimeId(rawRuntime);
    const runtime = await this._loadRuntime(runtimeId);
    const executable = asString(params.executable);
    const rawEnv = asRecord(params.env) as Record<string, string | null> | null;
    const env = buildExecutableEnv(runtimeId, executable, rawEnv ?? undefined);

    const installation = await this.resolveInstallation(runtime, env);
    if (installation.kind !== "available") {
      throw new Error(
        `OAR runtime '${runtimeId}' is unavailable (${installation.kind}): ${installationReason(installation)}`,
      );
    }

    const caps = runtime.sessionCapabilities?.(installation);
    const refused = {
      ...(runtime.refusedSessionOptions ?? {}),
      ...(caps?.refusedSessionOptions ?? {}),
    };

    const stripHttpCredentials = "mcpServerCredentials" in refused;
    const rawMcps = Array.isArray(params.mcpServers)
      ? (params.mcpServers as RawMcpServerInput[])
      : [];
    const preparedMcp = await prepareMcpServers(rawMcps, { stripHttpCredentials });

    const model = asString(params.model);
    const effort = asString(params.effort);
    const resume = asString(params.resume);
    const systemPrompt = asString(params.systemPrompt);
    const launchArgs = Array.isArray(params.launchArgs)
      ? params.launchArgs.filter((arg): arg is string => typeof arg === "string" && arg.length > 0)
      : undefined;

    let appendSystemPrompt: string | undefined;
    let replaceSystemPrompt: string | undefined;
    this._pendingSystemPrompt = null;

    if (systemPrompt) {
      if (!("appendSystemPrompt" in refused)) {
        appendSystemPrompt = systemPrompt;
      } else if (!("systemPrompt" in refused)) {
        replaceSystemPrompt = systemPrompt;
      } else {
        this._pendingSystemPrompt = systemPrompt;
      }
    }

    // OAR's codex runtime reads process.env.OAR_CODEX_SANDBOX (not SessionOptions.env).
    const stripCodexSandboxFromChild =
      runtimeId === "codex" && rawEnv !== null && Object.prototype.hasOwnProperty.call(rawEnv, "OAR_CODEX_SANDBOX");
    const childEnv = env && stripCodexSandboxFromChild
      ? Object.fromEntries(Object.entries(env).filter(([key]) => key !== "OAR_CODEX_SANDBOX"))
      : env;

    const sessionOptions: SessionOptions = {
      cwd,
      ...(model ? { model } : {}),
      ...(effort ? { effort } : {}),
      ...(resume ? { resume } : {}),
      ...(appendSystemPrompt ? { appendSystemPrompt } : {}),
      ...(replaceSystemPrompt ? { systemPrompt: replaceSystemPrompt } : {}),
      ...(launchArgs && launchArgs.length > 0 ? { launchArgs } : {}),
      ...(preparedMcp.servers.length > 0 ? { mcpServers: preparedMcp.servers } : {}),
      ...(childEnv && Object.keys(childEnv).length > 0 ? { env: childEnv } : {}),
    };

    let session: Session;
    try {
      session = await withCodexSandboxEnv(rawEnv, runtimeId, () => runtime.session(installation, sessionOptions));
    } catch (err) {
      await preparedMcp.close();
      throw err;
    }

    this._runtimeId = runtimeId;
    this._session = session;
    this._closeMcpProxy = preparedMcp.close;
    this._slashCompactAdvertised = false;
    this._availableCommandsObserved = false;

    this.attachSessionListeners(session);

    const nativeCompact = typeof session.compact === "function";
    const resolvedModel = session.model().value ?? model ?? null;
    const resolvedEffort = session.effort().value ?? effort ?? null;

    let sessionConfiguration: Record<string, unknown> | null = null;
    {
      const { models: catalog } = await listEnabledModels(
        runtime,
        installation,
        cwd,
        DEFAULT_PROBE_TIMEOUT_MS,
        childEnv,
      );
      const workerModels = catalog.map((m) => ({
        id: m.id,
        name: m.displayName,
        reasoningEfforts: [...m.effortLevels],
        defaultReasoningEffort: m.defaultEffort,
      }));
      const listedDefaultModel = catalog.find((m) => m.isDefault)?.id ?? null;
      const resolvedModelInCatalog =
        resolvedModel !== null && (workerModels.length === 0 || workerModels.some((entry) => entry.id === resolvedModel));
      const defaultModel = (!model && resolvedModelInCatalog ? resolvedModel : null) ?? listedDefaultModel;
      const defaultModelOption = defaultModel ? workerModels.find((entry) => entry.id === defaultModel) : undefined;
      const defaultReasoningEffort =
        (!effort ? resolvedEffort : null) ?? (defaultModelOption?.defaultReasoningEffort ?? null);
      if (workerModels.length > 0 || defaultModel !== null || defaultReasoningEffort !== null) {
        sessionConfiguration = { models: workerModels, defaultModel, defaultReasoningEffort };
      }
    }

    return {
      sessionId: session.id,
      model: resolvedModel,
      effort: resolvedEffort,
      supportsNativeSteer: typeof session.steer === "function",
      supportsImages: session.capabilities?.images === true,
      supportsNativeCompact: nativeCompact || this._slashCompactAdvertised,
      supportsSessionResume: true,
      contextUsage: buildUsagePayload(session),
      ...(typeof session.status === "function" ? { agentStatus: session.status().value } : {}),
      sessionConfiguration,
    };
  }

  private attachSessionListeners(session: Session): void {
    const observeRaw = (record: RawEvent): void => {
      this._statusState = reduceStatus(this._statusState, record, session.id);
      this.emitAgentStatus(session);
      const hasCompact = inspectAcpAvailableCommands(record);
      if (hasCompact !== null) {
        if (hasCompact) {
          this._slashCompactAdvertised = true;
        }
        this._availableCommandsObserved = true;
        const waiters = this._commandsWaiters;
        this._commandsWaiters = [];
        for (const resolve of waiters) {
          resolve();
        }
      }
    };

    const existing = session.records();
    for (const record of existing) {
      observeRaw(record);
      for (const event of eventsOf(record)) {
        if (event.kind === "task_started" || event.kind === "task_updated" || event.kind === "task_ended")
          this.onSessionEvent(session, event);
      }
    }

    const lastSeq = existing.length > 0 ? existing[existing.length - 1]!.seq : -1;
    this._unsubRaw = session.rawEvents(observeRaw, {
      sessionId: session.id,
      afterSeq: lastSeq,
    });

    this._unsubEvents = session.events(
      (event: Event) => {
        this.onSessionEvent(session, event);
        this.emitAgentStatus(session);
      },
      { cursor: { sessionId: session.id, afterSeq: lastSeq } },
    );
  }

  private onSessionEvent(session: Session, event: Event): void {
    if (event.sessionId !== session.id) {
      return;
    }
    const isRoot = event.agentPath.length === 0;

    switch (event.kind) {
      case "task_started":
      case "task_updated":
      case "task_ended":
        this.emitEvent({ ...event });
        break;
      case "text_delta":
        if (!isRoot) return;
        if (this._activeTurn?.captureText) {
          this._activeTurn.text += event.text;
        }
        this.emitEvent({ kind: "text_delta", delta: event.text });
        break;

      case "reasoning":
        if (!isRoot) return;
        if (event.content.kind === "text" && event.content.text.length > 0) {
          this.emitEvent({ kind: "thinking_delta", delta: event.content.text });
        }
        break;

      case "tool_call_started": {
        const callId = isRoot ? event.callId : `${event.agentPath.join("/")}:${event.callId}`;
        const name = event.server && event.toolName ? `${event.server}:${event.toolName}` : event.tool;
        this._startedTools.add(callId);
        this.emitEvent({
          kind: "tool_call_started",
          callId,
          name,
          args: parseToolArgs(event.input),
        });
        break;
      }

      case "tool_call_input": {
        const callId = isRoot ? event.callId : `${event.agentPath.join("/")}:${event.callId}`;
        if (!this._startedTools.has(callId)) {
          this._startedTools.add(callId);
          this.emitEvent({
            kind: "tool_call_started",
            callId,
            name: "tool",
            args: parseToolArgs(event.input),
          });
        }
        break;
      }

      case "tool_call_ended": {
        const callId = isRoot ? event.callId : `${event.agentPath.join("/")}:${event.callId}`;
        this._startedTools.delete(callId);
        const output = flattenToolOutput(event.content);
        const isError = event.result === "failed" || (typeof event.exitCode === "number" && event.exitCode !== 0);
        this.emitEvent({
          kind: "tool_call_completed",
          callId,
          output,
          isError,
        });
        break;
      }

      case "usage": {
        const usage = buildUsagePayload(session);
        if (usage) {
          this.emitEvent({ kind: "context_usage", usage });
        }
        break;
      }

      case "model":
        if (isRoot) {
          this.emitEvent({ kind: "model_changed", model: event.model });
        }
        break;

      case "exited":
        this.emitEvent({
          kind: "exited",
          code: event.code,
          reason: event.reason ?? null,
        });
        break;

      default:
        break;
    }
  }

  private requireSession(): Session {
    if (!this._session) {
      throw new Error("No open OAR session.");
    }
    return this._session;
  }

  private async handleSessionPrompt(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    const session = this.requireSession();
    const rawPrompt = typeof params.prompt === "string" ? params.prompt : "";
    const correlationId = asString(params.correlationId);
    const incoming = Array.isArray(params.images) ? params.images : [];
    if (incoming.length && session.capabilities?.images !== true) {
      return { status: "failed", ...imageRefusal("attachments_unsupported") };
    }
    const prepared = incoming.length ? await preparePromptImages(incoming).catch(() => null) : null;
    if (incoming.length && !prepared) {
      return { status: "failed", ...imageRefusal("attachment_unavailable") };
    }

    let effectivePrompt = rawPrompt;
    if (this._pendingSystemPrompt) {
      effectivePrompt = `${this._pendingSystemPrompt}\n\n${rawPrompt}`;
      this._pendingSystemPrompt = null;
    }

    try {
      return await this.runPromptTurn(session, effectivePrompt, correlationId, false, prepared?.images);
    } finally {
      await prepared?.cleanup();
    }
  }

  private async runPromptTurn(
    session: Session,
    prompt: string,
    correlationId: string | undefined,
    captureText: boolean,
    images?: readonly InputImage[],
  ): Promise<Record<string, unknown>> {
    const collector: ActiveTurnCollector = {
      captureText,
      correlationId,
      imageCleanups: [],
      text: "",
    };
    this._activeTurn = collector;
    try {
      const inputId = toValidInputId(correlationId);
      const outcome = await session.prompt(prompt, { inputId, ...(images && { images }) });
      if (outcome.kind !== "accepted") {
        if (images && outcome.code === "unsupported")
          return { status: "failed", ...imageRefusal("attachments_unsupported") };
        return {
          status: "failed",
          code: outcome.code,
          message: outcome.reason,
          isRetryable: outcome.code === "busy",
          beforePrompt: true,
          usage: buildUsagePayload(session),
        };
      }

      const settled = await awaitTurnEnd(session, outcome.seq, inputId);
      const usage = buildUsagePayload(session);

      if (settled.kind === "completed") {
        return {
          status: "completed",
          usage,
          ...(captureText ? { text: collector.text.trim() } : {}),
        };
      }
      if (settled.kind === "aborted") {
        return { status: "aborted", usage };
      }
      if (settled.kind === "failed") {
        return {
          status: "failed",
          code: settled.failure,
          message: settled.reason || `Turn failed (${settled.failure}).`,
          isRetryable:
            settled.failure === "rate_limited" ||
            settled.failure === "overloaded" ||
            settled.failure === "provider",
          usage,
        };
      }

      // InputHandled (runtime handled input natively without a model turn)
      return {
        status: "completed",
        usage,
        ...(captureText ? { text: collector.text.trim() } : {}),
      };
    } finally {
      if (this._activeTurn === collector) {
        this._activeTurn = null;
      }
      await Promise.all(collector.imageCleanups.map(cleanup => cleanup()));
    }
  }

  private async handleSessionSteer(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    const session = this.requireSession();
    const message = typeof params.message === "string" ? params.message : "";
    const incoming = Array.isArray(params.images) ? params.images : [];
    if (incoming.length && session.capabilities?.images !== true) {
      return { delivery: "declined", ...imageRefusal("attachments_unsupported") };
    }
    if ((!message && !incoming.length) || typeof session.steer !== "function") {
      return { delivery: "declined" };
    }
    const collector = this._activeTurn;
    const correlationId = asString(params.correlationId);
    if (incoming.length && (!collector || collector.correlationId !== correlationId)) {
      return { delivery: "declined" };
    }
    const prepared = incoming.length ? await preparePromptImages(incoming).catch(() => null) : null;
    if (incoming.length && !prepared) {
      return { delivery: "declined", ...imageRefusal("attachment_unavailable") };
    }

    let accepted = false;
    try {
      if (prepared && this._activeTurn !== collector) return { delivery: "declined" };
      const outcome = await session.steer(message, {
        inputId: toValidInputId(correlationId), ...(prepared && { images: prepared.images }),
      });
      if (prepared && outcome.kind === "rejected" && outcome.code === "unsupported") {
        return { delivery: "declined", ...imageRefusal("attachments_unsupported") };
      }
      accepted = outcome.kind === "accepted";
      return { delivery: accepted ? "accepted" : "declined" };
    } finally {
      if (prepared) {
        // Some runtimes acknowledge steer before consuming it. Hold files until the active prompt settles.
        if (accepted && collector && this._activeTurn === collector) collector.imageCleanups.push(prepared.cleanup);
        else await prepared.cleanup();
      }
    }
  }

  private async handleSessionAbort(): Promise<Record<string, unknown>> {
    const session = this.requireSession();
    const outcome = await session.abort();
    return { accepted: outcome.kind === "accepted" };
  }

  private async waitForAvailableCommands(graceTimeoutMs: number): Promise<void> {
    if (this._slashCompactAdvertised || this._availableCommandsObserved) {
      return;
    }
    if (graceTimeoutMs <= 0) {
      return;
    }
    await new Promise<void>((resolve) => {
      const done = (): void => {
        clearTimeout(timer);
        resolve();
      };
      const timer = setTimeout(() => {
        this._commandsWaiters = this._commandsWaiters.filter((fn) => fn !== done);
        resolve();
      }, graceTimeoutMs);
      this._commandsWaiters.push(done);
    });
  }

  private async handleCompactSupport(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    const session = this.requireSession();
    if (typeof session.compact === "function") {
      return { supported: true, mode: "native" };
    }
    const graceMs = asNumber(params.graceTimeoutMs) ?? DEFAULT_COMPACT_GRACE_MS;
    await this.waitForAvailableCommands(graceMs);
    if (this._slashCompactAdvertised) {
      return { supported: true, mode: "slash_command" };
    }
    return { supported: false, mode: "unsupported" };
  }

  private async handleSessionCompact(params: Record<string, unknown>): Promise<Record<string, unknown>> {
    const session = this.requireSession();
    const timeoutMs = asNumber(params.timeoutMs) ?? DEFAULT_COMPACT_TIMEOUT_MS;

    // Path 1: OAR native session.compact() (Codex, OpenCode v2, Claude, Grok, Qoder, etc.)
    if (typeof session.compact === "function") {
      const outcome = await session.compact({ timeoutMs });
      const usage = buildUsagePayload(session);
      if (outcome.kind === "completed") {
        return {
          completed: true,
          mode: "native",
          summary: "Context compacted.",
          usage,
        };
      }
      if (outcome.kind === "aborted") {
        throw rpcError(-32011, "Native compaction was aborted.");
      }
      const reason = "reason" in outcome && typeof outcome.reason === "string"
        ? outcome.reason
        : `Compaction ended with '${outcome.kind}'.`;
      throw new Error(reason);
    }

    // Path 2: ACP /compact slash command (Antigravity 1.5.x advertises "compact" in available_commands_update)
    await this.waitForAvailableCommands(DEFAULT_COMPACT_GRACE_MS);
    if (!this._slashCompactAdvertised) {
      throw rpcError(-32010, `Runtime '${this._runtimeId}' does not support manual compaction.`);
    }

    const turn = await this.runPromptTurnWithTimeout(session, "/compact", undefined, true, timeoutMs);
    if (turn.status === "aborted") {
      throw rpcError(-32011, "Compaction was aborted.");
    }
    if (turn.status !== "completed") {
      throw new Error(typeof turn.message === "string" ? turn.message : "Slash compaction failed.");
    }
    const text = typeof turn.text === "string" && turn.text.length > 0 ? turn.text : "Context compacted.";
    return {
      completed: true,
      mode: "slash_command",
      summary: text,
      usage: turn.usage ?? buildUsagePayload(session),
    };
  }

  private async runPromptTurnWithTimeout(
    session: Session,
    prompt: string,
    correlationId: string | undefined,
    captureText: boolean,
    timeoutMs: number,
  ): Promise<Record<string, unknown>> {
    if (timeoutMs <= 0) {
      return await this.runPromptTurn(session, prompt, correlationId, captureText);
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    const timeoutPromise = new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => {
        void session.abort().catch(() => {});
        reject(rpcError(-32011, `Slash compaction timed out after ${timeoutMs}ms.`));
      }, timeoutMs);
    });
    try {
      return await Promise.race([
        this.runPromptTurn(session, prompt, correlationId, captureText),
        timeoutPromise,
      ]);
    } finally {
      if (timer !== undefined) {
        clearTimeout(timer);
      }
    }
  }

  private emitAgentStatus(session: Session): void {
    if (this._session !== session || typeof session.status !== "function") return;
    const { inputAdmission, adapterQueue, compactionState, ...status } = this._statusState;
    void inputAdmission;
    void adapterQueue;
    void compactionState;
    const previous = this._lastAgentStatus;
    if (previous && statusEmissionKey(previous) === statusEmissionKey(status) &&
      !(status.kind === "idle" && status.activity && previous.kind === "idle" && previous.activity &&
        status.activity.lastEventAt - previous.activity.lastEventAt >= 1000)) return;
    this._lastAgentStatus = status;
    this.emitEvent({ kind: "agent_status", status });
  }

  private async closeSessionInternal(): Promise<void> {
    this._lastAgentStatus = null;
    this._statusState = initialStatus;
    this._unsubEvents?.();
    this._unsubEvents = null;
    this._unsubRaw?.();
    this._unsubRaw = null;
    this._startedTools.clear();

    const waiters = this._commandsWaiters;
    this._commandsWaiters = [];
    for (const resolve of waiters) {
      resolve();
    }

    const session = this._session;
    this._session = null;
    if (session) {
      try {
        await session.dispose();
      } catch {
        // Best-effort cleanup.
      }
    }

    const closeProxy = this._closeMcpProxy;
    this._closeMcpProxy = null;
    if (closeProxy) {
      try {
        await closeProxy();
      } catch {
        // Best-effort cleanup.
      }
    }
  }
}
