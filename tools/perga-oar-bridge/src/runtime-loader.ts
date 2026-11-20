import type { Runtime } from "@botiverse/oar/browser";

export const BIN_ENV_VARS: Readonly<Record<string, string>> = {
  antigravity: "OAR_ANTIGRAVITY_BIN",
  claude: "OAR_CLAUDE_BIN",
  codebuddy: "OAR_CODEBUDDY_BIN",
  codex: "OAR_CODEX_BIN",
  devin: "OAR_DEVIN_BIN",
  dsh: "OAR_DSH_BIN",
  factory: "OAR_FACTORY_BIN",
  grok: "OAR_GROK_BIN",
  kimi: "OAR_KIMI_BIN",
  opencode: "OAR_OPENCODE_BIN",
  qoder: "OAR_QODER_BIN",
  qwen: "OAR_QWEN_BIN",
};

export function normalizeRuntimeId(raw: string): string {
  const trimmed = raw.trim().toLowerCase();
  if (trimmed === "acp:antigravity" || trimmed === "agy" || trimmed === "antigravity") {
    return "antigravity";
  }
  if (trimmed === "acp:opencode" || trimmed === "opencode") {
    return "opencode";
  }
  if (trimmed.startsWith("oar:")) {
    return normalizeRuntimeId(trimmed.slice(4));
  }
  return trimmed;
}

export function buildExecutableEnv(
  runtimeId: string,
  executable: string | undefined,
  extraEnv?: Readonly<Record<string, string | null>>,
): Readonly<Record<string, string | null>> | undefined {
  const result: Record<string, string | null> = {};
  if (extraEnv) {
    for (const [k, v] of Object.entries(extraEnv)) {
      result[k] = v;
    }
  }
  const envVar = BIN_ENV_VARS[runtimeId];
  if (executable && executable.trim().length > 0 && envVar) {
    result[envVar] = executable.trim();
  }
  return Object.keys(result).length > 0 ? result : undefined;
}

/**
 * Lazily imports only the requested OAR runtime so bridge memory stays ~2 MB
 * instead of eagerly loading every runtime SDK.
 */
export async function loadOarRuntime(rawId: string): Promise<Runtime> {
  const id = normalizeRuntimeId(rawId);
  switch (id) {
    case "antigravity":
      return (await import("@botiverse/oar/runtimes/antigravity")).antigravityRuntime;
    case "codex":
      return (await import("@botiverse/oar/runtimes/codex")).codexRuntime;
    case "opencode":
      return (await import("@botiverse/oar/runtimes/opencode")).opencodeRuntime;
    case "claude":
      return (await import("@botiverse/oar/runtimes/claude")).claudeRuntime;
    case "codebuddy":
      return (await import("@botiverse/oar/runtimes/codebuddy")).codebuddyRuntime;
    case "devin":
      return (await import("@botiverse/oar/runtimes/devin")).devinRuntime;
    case "dsh":
      return (await import("@botiverse/oar/runtimes/dsh")).dshRuntime;
    case "factory":
      return (await import("@botiverse/oar/runtimes/factory")).factoryRuntime;
    case "grok":
      return (await import("@botiverse/oar/runtimes/grok")).grokRuntime;
    case "kimi":
      return (await import("@botiverse/oar/runtimes/kimi")).kimiRuntime;
    case "qoder":
      return (await import("@botiverse/oar/runtimes/qoder")).qoderRuntime;
    case "qwen":
      return (await import("@botiverse/oar/runtimes/qwen")).qwenRuntime;
    default:
      throw new Error(`Unsupported OAR runtime '${rawId}'.`);
  }
}
