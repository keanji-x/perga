import type { AgentProvider } from '../api/schemas.ts';

/**
 * Legacy Planner presentation only; managed groups use the declared provider identifier.
 * Total over AgentProvider, so a new backend is a compile error until its Planner presentation is declared.
 * `unavailable` says why that provider's `source: 'unavailable'` catalog is empty. A Claude group waits for
 * its availability and catalog before it joins a menu that offers other providers (`hiddenUntilKnown`); a
 * Codex one stays while the daemon is down, saying so. Only codex compacts (`switchNote`).
 * The host supplies the server's blank-creation capability only on the new-track surface.
 */
export const PROVIDER_PRESENTATION: Readonly<Record<AgentProvider, Readonly<{
  label: string; unavailable: string; hiddenUntilKnown: boolean; switchNote: boolean;
}>>> = Object.freeze({
  codex: Object.freeze({
    label: 'Codex', unavailable: 'codex is not running', hiddenUntilKnown: false, switchNote: true,
  }),
  claude: Object.freeze({
    label: 'Claude', unavailable: 'Claude cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  opencode: Object.freeze({
    label: 'OpenCode', unavailable: 'OpenCode cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  antigravity: Object.freeze({
    label: 'Antigravity (agy)', unavailable: 'Antigravity (agy) cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  grok: Object.freeze({
    label: 'Grok', unavailable: 'Grok cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
});
