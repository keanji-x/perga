import crypto from "node:crypto";
import type {
  AvailableInstallation,
  InstallationSnapshot,
  RawEvent,
  Runtime,
  Session,
  ToolOutputPart,
} from "@botiverse/oar/browser";

export interface UsagePayload {
  readonly inputTokens: number;
  readonly outputTokens: number;
  readonly cachedInputTokens: number;
  readonly contextWindowTokens: number | null;
  readonly usedTokens: number | null;
}

export interface ListedModel {
  readonly id: string;
  readonly displayName: string;
  readonly providerName: string;
  readonly isDefault: boolean;
  readonly defaultEffort: string | null;
  readonly effortLevels: readonly string[];
}

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;

export function asRecord(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

export function asString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : undefined;
}

export function asNumber(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

export function toValidInputId(correlationId: string | undefined): string {
  return correlationId && UUID_RE.test(correlationId) ? correlationId : crypto.randomUUID();
}

export function rpcError(code: number, message: string): Error & { code: number } {
  const err = new Error(message) as Error & { code: number };
  err.code = code;
  return err;
}

export function installationReason(snapshot: Exclude<InstallationSnapshot, AvailableInstallation>): string {
  return snapshot.kind === "not_found" ? "not_found" : snapshot.reason;
}

export function installationVersion(snapshot: AvailableInstallation): string {
  return snapshot.via === "executable" ? (snapshot.version ?? "unknown") : "bundled";
}

export async function withCodexSandboxEnv<T>(
  rawEnv: Record<string, string | null> | null,
  runtimeId: string,
  run: () => Promise<T>,
): Promise<T> {
  if (runtimeId !== "codex" || !rawEnv || !Object.prototype.hasOwnProperty.call(rawEnv, "OAR_CODEX_SANDBOX")) {
    return await run();
  }
  const previous = process.env.OAR_CODEX_SANDBOX;
  const next = asString(rawEnv.OAR_CODEX_SANDBOX);
  if (next !== undefined) {
    process.env.OAR_CODEX_SANDBOX = next;
  } else {
    delete process.env.OAR_CODEX_SANDBOX;
  }
  try {
    return await run();
  } finally {
    if (previous === undefined) {
      delete process.env.OAR_CODEX_SANDBOX;
    } else {
      process.env.OAR_CODEX_SANDBOX = previous;
    }
  }
}

export async function listEnabledModels(
  runtime: Runtime,
  installation: AvailableInstallation,
  cwd: string,
  timeoutMs: number,
  env: Readonly<Record<string, string | null>> | undefined,
): Promise<{ models: readonly ListedModel[]; reason: string | null }> {
  if (!runtime.listModels) {
    return { models: [], reason: null };
  }
  try {
    const listed = await runtime.listModels(installation, {
      cwd,
      timeoutMs,
      ...(env && Object.keys(env).length > 0 ? { env } : {}),
    });
    if (listed.kind === "ok") {
      return {
        models: listed.models
          .filter((m) => !m.disabled)
          .map((m) => ({
            id: m.id,
            displayName: m.displayName ?? m.id,
            providerName: m.provider?.displayName ?? "",
            isDefault: m.default === true,
            defaultEffort: m.defaultEffort ?? null,
            effortLevels: m.effortLevels ? [...m.effortLevels] : [],
          })),
        reason: null,
      };
    }
    if (listed.kind === "unauthenticated") {
      return { models: [], reason: listed.detail ?? "unauthenticated" };
    }
    return { models: [], reason: listed.reason };
  } catch (err) {
    return { models: [], reason: err instanceof Error ? err.message : String(err) };
  }
}

export function flattenToolOutput(parts: readonly ToolOutputPart[] | undefined): string {
  if (!parts || parts.length === 0) {
    return "";
  }
  const chunks: string[] = [];
  for (const part of parts) {
    if (part.type === "text") {
      chunks.push(part.text);
    } else if (part.type === "image") {
      chunks.push(`[image: ${part.mediaType}]`);
    } else {
      try {
        chunks.push(JSON.stringify(part.value));
      } catch {
        chunks.push("[unserializable tool output]");
      }
    }
  }
  return chunks.join("\n");
}

export function parseToolArgs(rawInput: string | undefined): unknown {
  if (rawInput === undefined || rawInput.length === 0) {
    return {};
  }
  try {
    return JSON.parse(rawInput);
  } catch {
    return { raw: rawInput };
  }
}

export function buildUsagePayload(session: Session): UsagePayload | null {
  const ctx = session.contextUsage().value;
  const totals = session.usage().value.total;
  if (ctx === null && totals === null) {
    return null;
  }
  return {
    inputTokens: totals?.input ?? 0,
    outputTokens: totals?.output ?? 0,
    cachedInputTokens: totals?.cacheRead ?? 0,
    contextWindowTokens: ctx?.contextWindow ?? null,
    usedTokens: ctx?.tokens ?? null,
  };
}

export function inspectAcpAvailableCommands(record: RawEvent): boolean | null {
  if (record.kind !== "frame") {
    return null;
  }
  const native = asRecord(record.body.native);
  const update = asRecord(native?.update) ?? native;
  if (!update) {
    return null;
  }
  const kind = update.sessionUpdate ?? update.type ?? update.kind ?? record.body.type;
  if (kind !== "available_commands_update") {
    return null;
  }
  const commands = update.availableCommands;
  if (!Array.isArray(commands)) {
    return false;
  }
  for (const entry of commands) {
    const rec = asRecord(entry);
    const name = typeof rec?.name === "string" ? rec.name.trim() : typeof rec?.id === "string" ? rec.id.trim() : "";
    if (name === "compact" || name === "/compact") {
      return true;
    }
  }
  return false;
}
