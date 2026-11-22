import type { CardComponentProps, CardEntry, PanelInput } from '../registry.js';
import type { TerminalDto } from '../../../../../core/api/perga-v1.js';
import { TerminalCardView } from './terminal-card.tsx';

declare module '../registry.js' {
  interface CardDataMap {
    terminal: TerminalCard;
  }
}

export type TerminalCard = Readonly<{
  type: 'terminal';
  id: string;
  title: string | null;
  terminalId: string | null;
  sessionState: TerminalDto['status'] | null;
  cwd: string | null;
  gateCwd: string | null;
}>;

export function terminalSessionFromCard(card: PanelInput): Pick<TerminalCard, 'terminalId' | 'sessionState'> {
  return { terminalId: card.terminal?.id ?? null, sessionState: card.terminal?.status ?? null };
}

export const TERMINAL_CARD_ENTRY = Object.freeze({
  type: 'terminal',
  component: (props: CardComponentProps<TerminalCard>) => TerminalCardView(props),
  headless: false,
  defaultSize: Object.freeze({ w: 6, h: 10, minW: 4, minH: 6 }),
  claim: Object.freeze({ mode: 'exact', kind: 'terminal' } as const),
  title: (card: TerminalCard) => card.title ?? 'Terminal',
  accessibleName: (card: TerminalCard) => card.title ?? 'Terminal',
  create: Object.freeze({
    mode: 'atomic' as const,
    submit: (): Promise<{ cardId: string }> => Promise.reject(new Error('TerminalCardSubmitViaTrackRoute')),
  }),
  addPanel: Object.freeze({ label: 'terminal' }),
  fromPanel: (card: PanelInput): TerminalCard | null => (
    card.kind === 'terminal'
      ? Object.freeze({ type: 'terminal', id: card.id, title: null, ...terminalSessionFromCard(card), cwd: card.terminal?.cwd ?? null, gateCwd: null } as const)
      : null
  ),
}) satisfies CardEntry<TerminalCard>;
