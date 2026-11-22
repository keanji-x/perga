

/** Known display labels and menu hints. Unknown runtime IDs remain usable and round-trip unchanged. */
export const PROVIDER_PRESENTATION: Readonly<Record<string, Readonly<{
  icon?: 'codex' | 'claude' | 'agent'; label: string; unavailable: string; hiddenUntilKnown: boolean; switchNote: boolean;
}>>> = Object.freeze({
  codex: Object.freeze({
    icon: 'codex', label: 'Codex', unavailable: 'codex is not running', hiddenUntilKnown: false, switchNote: true,
  }),
  claude: Object.freeze({
    icon: 'claude', label: 'Claude', unavailable: 'Claude cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  opencode: Object.freeze({
    icon: 'agent', label: 'OpenCode', unavailable: 'OpenCode cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  antigravity: Object.freeze({
    icon: 'agent', label: 'Antigravity (agy)', unavailable: 'Antigravity (agy) cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
  grok: Object.freeze({
    icon: 'agent', label: 'Grok', unavailable: 'Grok cannot run on this server right now', hiddenUntilKnown: true,
    switchNote: false,
  }),
});

/** Resolve known product labels without changing the runtime provider identity. */
export function providerPresentation(provider: string) {
  const labelKey = provider.slice(provider.lastIndexOf(':') + 1);
  return (Object.hasOwn(PROVIDER_PRESENTATION, labelKey) ? PROVIDER_PRESENTATION[labelKey] : undefined) ?? {
    icon: undefined,
    label: provider, unavailable: `${provider} cannot run on this server right now`,
    hiddenUntilKnown: false, switchNote: false,
  };
}
