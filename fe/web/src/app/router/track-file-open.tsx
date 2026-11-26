import { isImeKeyEvent } from '@astryxdesign/core/utils';
import { useCallback, useEffect, useEffectEvent, useMemo } from 'react';

import type { WorkspaceFileOpenRequested } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { listTrackWorkspaceDirectoryOperation, searchTrackWorkspaceFilesOperation } from '../../../../core/domain/fs.ts';
import { parseFileOpenInput, type ReportFileLinkTarget } from '../../../../core/domain/report-file.ts';
import { QuickOpen, type QuickOpenLookup } from '../../features/report/quick-open/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useFileOpenRequests } from '../providers/connection-status.tsx';
import { runOperation } from '../providers/query-operation.ts';

function modalIsOpen() {
  return Array.from(document.querySelectorAll<HTMLElement>(':is([role="dialog"][aria-modal="true"], dialog:modal)'))
    .some(dialog => dialog.getClientRects().length > 0 && dialog.closest('[hidden], [inert], [aria-hidden="true"]') === null);
}

export function useTrackQuickOpen({ trackId, transport, unauthorized, recent, open, remember }: Readonly<{
  trackId: string;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  recent: readonly string[];
  open: (target: ReportFileLinkTarget, focus?: boolean, opener?: Element | null) => boolean;
  remember: (path: string) => void;
}>) {
  const [dialog, setDialog] = useState<Readonly<{ query: string; opener: Element | null }> | null>(null);
  const showQuickOpen = useCallback((query: string) => {
    setDialog({ query, opener: document.activeElement });
  }, [setDialog]);
  const lookup = useMemo<QuickOpenLookup>(() => ({
    search: async (value, signal) => (await runOperation(
      transport, searchTrackWorkspaceFilesOperation(trackId, value, signal), unauthorized,
    )).paths,
    list: async (directory, signal) => (await runOperation(
      transport, { ...listTrackWorkspaceDirectoryOperation(trackId, directory), signal }, unauthorized,
    )).entries,
  }), [trackId, transport, unauthorized]);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((!event.metaKey && !event.ctrlKey) || event.altKey || event.shiftKey || event.key.toLowerCase() !== 'p'
        || isImeKeyEvent(event)) return;
      if (event.ctrlKey && !event.metaKey && document.activeElement?.closest('[data-nc-xterm]')) return;
      event.preventDefault();
      if (!modalIsOpen()) showQuickOpen('');
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [showQuickOpen]);
  const requests = useFileOpenRequests();
  const receiveFileOpen = useEffectEvent((request: WorkspaceFileOpenRequested) => {
    if (modalIsOpen()) return;
    if (open({ path: request.path, line: request.line ?? undefined, endLine: request.endLine ?? undefined }, false)) {
      remember(request.path);
    }
  });
  useEffect(() => requests?.subscribe(request => {
    if (request.trackId === trackId) receiveFileOpen(request);
  }), [requests, trackId]);
  const command = useCallback((argument: string) => {
    const target = parseFileOpenInput(argument);
    if (target !== null && (target.line !== undefined || /[/.\\]/.test(argument))) open(target);
    else showQuickOpen(argument);
  }, [open, showQuickOpen]);
  return {
    command,
    dialog: dialog === null ? null : <QuickOpen initialQuery={dialog.query} lookup={lookup} recent={recent}
      onClose={() => setDialog(null)} onOpen={target => open(target, true, dialog.opener)} />,
  };
}
