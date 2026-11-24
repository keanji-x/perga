import type { AgentSessionDto } from '../api/perga-v1.js';

/** Format the server's latest measurement for the context indicator. */
export function sessionContextUsage(session: AgentSessionDto) {
  return formatSessionContextUsage(session.contextUsage);
}

export function formatSessionContextUsage(usage: AgentSessionDto['contextUsage']) {
  if (usage == null) return null;
  return { used_tokens: usage.usedTokens, context_window: usage.windowTokens,
    percent: usage.ratio == null ? null : Math.round(usage.ratio * 100), at_ms: usage.observedAtMs };
}
