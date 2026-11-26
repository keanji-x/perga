import { isImeKeyEvent } from '@astryxdesign/core/utils';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useCallback, useDeferredValue, useEffect, useEffectEvent, useMemo } from 'react';

import type { WorkspaceFileOpenRequested } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import type { UnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { listTrackWorkspaceDirectoryOperation, searchTrackWorkspaceFilesOperation } from '../../../../core/domain/fs.ts';
import { createQuickOpenFileIndex, quickOpenDirectoryEntries, quickOpenFileEntries, quickOpenQuery } from '../../../../core/domain/quick-open.ts';
import { parseFileOpenInput, type ReportFileLinkTarget } from '../../../../core/domain/report-file.ts';
import { QuickOpen } from '../../features/report/quick-open/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { useFileOpenRequests } from '../providers/connection-status.tsx';
import { runOperation } from '../providers/query-operation.ts';

const lookupStaleTime = 15_000;

function waitForSearchPause(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    signal.throwIfAborted();
    const abort = () => {
      clearTimeout(timer);
      reject(new DOMException('File search was aborted.', 'AbortError'));
    };
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', abort);
      resolve();
    }, 140);
    signal.addEventListener('abort', abort, { once: true });
  });
}

function TrackQuickOpen({ trackId, transport, unauthorized, recent, initialQuery, onOpen, onClose }: Readonly<{
  trackId: string;
  transport: ApiTransportPort;
  unauthorized: UnauthorizedChannel;
  recent: readonly string[];
  initialQuery: string;
  onOpen: (target: ReportFileLinkTarget) => void;
  onClose: () => void;
}>) {
  const [input, setInput] = useState(initialQuery);
  const mode = quickOpenQuery(input);
  const directory = mode.kind === 'directory' ? mode.directory : null;
  const term = mode.kind === 'search' ? mode.query : '';
  const deferredTerm = useDeferredValue(term);
  const index = useQuery({
    queryKey: ['track-file-index', trackId],
    queryFn: async ({ signal }) => {
      const { paths, truncated } = await runOperation(transport,
        searchTrackWorkspaceFilesOperation(trackId, '', signal, 20_000), unauthorized);
      return { files: createQuickOpenFileIndex(paths), truncated };
    },
    staleTime: lookupStaleTime,
    refetchOnWindowFocus: false,
    retry: false,
  });
  const needsSupplement = index.data?.truncated === true && deferredTerm !== '';
  const supplement = useQuery({
    queryKey: ['track-file-search', trackId, needsSupplement ? deferredTerm : ''],
    queryFn: async ({ signal }) => {
      await waitForSearchPause(signal);
      return runOperation(transport, searchTrackWorkspaceFilesOperation(trackId, deferredTerm, signal, 200), unauthorized);
    },
    enabled: needsSupplement,
    placeholderData: keepPreviousData,
    staleTime: lookupStaleTime,
    refetchOnWindowFocus: false,
    retry: false,
  });
  const listing = useQuery({
    queryKey: ['track-file-directory', trackId, directory],
    queryFn: async ({ signal }) => ({
      directory: directory!,
      entries: (await runOperation(transport,
        { ...listTrackWorkspaceDirectoryOperation(trackId, directory!), signal }, unauthorized)).entries,
    }),
    enabled: directory !== null,
    placeholderData: keepPreviousData,
    staleTime: lookupStaleTime,
    refetchOnWindowFocus: false,
    retry: false,
  });
  const emptyIndex = useMemo(() => createQuickOpenFileIndex([]), []);
  const workspaceIndex = index.data?.files ?? emptyIndex;
  const recentIndex = useMemo(() => createQuickOpenFileIndex(recent), [recent]);
  const supplementIndex = useMemo(() => supplement.data === undefined ? undefined
    : createQuickOpenFileIndex(supplement.data.paths), [supplement.data]);
  const listingDirectory = listing.data?.directory;
  const directoryEntries = listing.data?.entries;
  const staleDirectory = directory !== null && listingDirectory !== undefined && listingDirectory !== directory;
  const prefix = mode.kind === 'directory' && !staleDirectory ? mode.prefix : '';
  const entries = useMemo(() => {
    if (directory !== null) {
      return listingDirectory === undefined || directoryEntries === undefined ? []
        : quickOpenDirectoryEntries(listingDirectory, prefix, directoryEntries);
    }
    return quickOpenFileEntries(deferredTerm, recentIndex, workspaceIndex, needsSupplement ? supplementIndex : undefined);
  }, [deferredTerm, directory, directoryEntries, listingDirectory, needsSupplement, prefix, recentIndex, supplementIndex, workspaceIndex]);
  const lookup = directory === null ? index : listing;
  const busy = lookup.data === undefined && lookup.isFetching;
  const emptyMessage = (lookup.isError || lookup.isPaused) && lookup.data === undefined
    ? directory === null ? 'Could not search files.' : 'Could not list this folder.'
    : directory === null && term === '' ? 'No workspace files' : 'No matching files';
  return <QuickOpen input={input} onInputChange={setInput} mode={mode} entries={entries} busy={busy}
    staleDirectory={staleDirectory} searchPending={mode.kind === 'search' && term !== deferredTerm}
    emptyMessage={emptyMessage} onOpen={onOpen} onClose={onClose} />;
}

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
    dialog: dialog === null ? null : <TrackQuickOpen key={trackId} trackId={trackId} transport={transport}
      unauthorized={unauthorized} initialQuery={dialog.query} recent={recent}
      onClose={() => setDialog(null)} onOpen={target => open(target, true, dialog.opener)} />,
  };
}
