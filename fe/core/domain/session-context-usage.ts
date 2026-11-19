import type { AgentSessionDto } from '../api/perga-v1.js';

/** Preserve measured occupancy and derive a percentage only from a valid window. */
export function sessionContextUsage(session: AgentSessionDto) {
  if (session.contextUsedTokens == null) return null;
  const used = session.contextUsedTokens;
  const window = session.contextWindowTokens ?? null;
  return { used_tokens: used, context_window: window,
    percent: window != null && window > 0 && used >= 0 && used <= window ? Math.round(used / window * 100) : null,
    at_ms: session.contextUsageAtMs ?? null };
}
