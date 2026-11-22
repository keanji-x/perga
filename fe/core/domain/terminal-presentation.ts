export type TerminalPresentation = Readonly<{
  source: 'manual' | 'agent' | 'task' | 'system' | 'plugin' | 'unrecorded';
  inWorkbench: boolean;
}>;
/** Explicit local placement; never a server payload. */
export function terminalPresentation(item: Readonly<{ kind: string; presentation?: TerminalPresentation }>): TerminalPresentation {
  if (item.kind !== 'terminal' || item.presentation === undefined) throw new Error('Terminal presentation requires a terminal panel');
  return item.presentation;
}
