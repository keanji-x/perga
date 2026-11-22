import type { TerminalDto } from '../api/perga-v1.js';
import type { TerminalPresentation } from './terminal-presentation.js';

/** Local layout metadata; none of these descriptors is an HTTP entity. */
type PanelMetadata = Readonly<{
  id: string; trackId: string; title: string | null; sort: number;
  removable: boolean; createdAtMs: number; updatedAtMs: number;
}>;

export type TerminalPanelItem = PanelMetadata & Readonly<{
  panelType: 'terminal'; kind: 'terminal'; terminal: TerminalDto;
  presentation: TerminalPresentation; payload?: never;
}>;
export type ReportPanelItem = PanelMetadata & Readonly<{
  panelType: 'report'; kind: 'track-report'; payload: unknown;
  terminal?: never; presentation?: never;
}>;
export type FileViewerPanelItem = PanelMetadata & Readonly<{
  panelType: 'file-viewer'; kind: 'file-viewer'; payload: Readonly<{ path: string }>;
  terminal?: never; presentation?: never;
}>;
/** Explicit registry extension surface; its payload belongs to the registering client component. */
export type CustomPanelItem = PanelMetadata & Readonly<{
  panelType: 'custom'; kind: string; payload: unknown;
  terminal?: TerminalDto; presentation?: TerminalPresentation;
}>;
export type PanelItem = TerminalPanelItem | ReportPanelItem | FileViewerPanelItem | CustomPanelItem;

export function terminalPanelItem(terminal: TerminalDto, sort = 100, inWorkbench = true): TerminalPanelItem {
  return { panelType: 'terminal', id: terminal.id, trackId: terminal.trackId, kind: 'terminal', title: terminal.title,
    sort, terminal, presentation: { source: terminal.sessionId == null ? 'unrecorded' : 'agent', inWorkbench },
    removable: true, createdAtMs: terminal.createdAtMs, updatedAtMs: terminal.exitedAtMs ?? terminal.createdAtMs };
}
