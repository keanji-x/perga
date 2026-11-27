import { createTerminalWorkbenchSessions } from '../../features/track/workbench/public.tsx';
import { createContext, useCallback, useContext, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react';
import { useRouterState } from '@tanstack/react-router';
import { useState } from '../../ui/state/public.ts';
import { createReportPreparationCache } from '../../features/report/document/preparation.ts';
import type { TrackSearch } from './track-search.ts';

type ScrollOffset = Readonly<{ top: number; left: number }>;
type FileScrollPort = 'file' | 'code';
type FileEntryView = { scroll: Map<FileScrollPort, ScrollOffset>; sourceMode?: boolean };
type TrackView = {
  search: TrackSearch;
  progressOpen: boolean;
  fileEntries: Map<string, FileEntryView>;
  scroll: Map<string, ScrollOffset>;
};

function createTrackViews() {
  const views = new Map<string, TrackView>();
  return {
    reports: createReportPreparationCache(),
    terminals: createTerminalWorkbenchSessions(),
    get: (id: string) => views.get(id),
    ensure: (id: string) => {
      let view = views.get(id);
      if (view === undefined) {
        view = { search: {}, scroll: new Map(), fileEntries: new Map(), progressOpen: false };
        views.set(id, view);
      }
      return view;
    },
  };
}

const TrackViewsContext = createContext<ReturnType<typeof createTrackViews> | null>(null);

/** Owned by the signed-in router tree; leaving a route disposes its resources,
 * but keeps its small UI snapshot. Nothing is written to browser storage. */
export function TrackViewProvider({ children }: { children: ReactNode }) {
  const [views] = useState(createTrackViews);
  return <TrackViewsContext.Provider value={views}>{children}</TrackViewsContext.Provider>;
}

export function useTrackViews() { return useContext(TrackViewsContext); }

/** The signed-in scope owns one data-only report cache. Execution stays injected. */
export function useTrackReportCache() {
  const views = useTrackViews();
  if (views === null) throw new Error('Track report preparation requires TrackViewProvider');
  return views.reports;
}

/** Detail/search are current immediately; report geometry waits for its committed content. */
export function useTrackViewState(trackId: string, reportReady: boolean) {
  const views = useTrackViews();
  const [progressOpen, setProgressOpen] = useState(() => views?.get(trackId)?.progressOpen ?? false);
  const search = useRouterState({ select: (state) => state.location.search as TrackSearch });
  const hash = useRouterState({ select: (state) => state.location.hash });
  useLayoutEffect(() => {
    if (views !== null) {
      const { card, file, line, panel } = search;
      const view = views.ensure(trackId);
      view.search = { card, file, line, panel };
    }
  }, [search, trackId, views]);
  useTrackScrollPorts(views, trackId, reportReady, hash);
  return { open: progressOpen, onOpenChange: (open: boolean) => {
    if (views !== null) views.ensure(trackId).progressOpen = open;
    setProgressOpen(open);
  } };
}

type ScrollPort = 'page' | 'panel';
function portNode(port: ScrollPort) {
  if (port === 'page') return document.querySelector<HTMLElement>('[data-nc-track-page]');
  return document.querySelector<HTMLElement>('[data-nc-panel]');
}

function isUserScrollInput(event: Event, target: Element | null): boolean {
  if (target === null) return false;
  if (event.type === 'pointerdown' && target.closest('button, a, input, textarea, select, [role="button"], [contenteditable="true"]') !== null) return false;
  if (event instanceof KeyboardEvent) {
    if (!['PageDown', 'PageUp', 'Home', 'End', 'ArrowDown', 'ArrowUp', ' '].includes(event.key)) return false;
    if (target.closest('input, textarea, select, [contenteditable="true"]') !== null) return false;
    if (event.key === ' ' && target.closest('button, a, [role="button"]') !== null) return false;
  }
  return true;
}

/** History owns identity; the signed-in Track scope keeps only local view state. */
export function useTrackFileView(trackId: string, entryKey: string | undefined, filePath: string | null) {
  const views = useTrackViews();
  // Never read the live entry during a parent rerender: a fresh visit must not
  // become a restoration merely because its first scroll has been recorded.
  const saved = useMemo(() => {
    const entry = entryKey === undefined ? undefined : views?.get(trackId)?.fileEntries.get(entryKey);
    return entry === undefined ? undefined : { scroll: new Map(entry.scroll), sourceMode: entry.sourceMode };
  }, [entryKey, trackId, views]);
  const ensureEntry = useCallback(() => {
    if (views === null || entryKey === undefined) return undefined;
    const entries = views.ensure(trackId).fileEntries;
    let entry = entries.get(entryKey);
    if (entry === undefined) {
      entry = { scroll: new Map() };
      entries.set(entryKey, entry);
    }
    return entry;
  }, [entryKey, trackId, views]);

  useLayoutEffect(() => {
    if (filePath === null) return;
    const entry = ensureEntry();
    if (entry === undefined) return;
    const ports: readonly FileScrollPort[] = ['file', 'code'];
    const pending = new Set(saved?.scroll.keys());
    const acted = new Set<FileScrollPort>();
    const observed = new Map<FileScrollPort, HTMLElement>();
    const resized = new Set<Element>();
    let frame: number | undefined;
    const remember = (port: FileScrollPort, node: HTMLElement) => {
      if (!pending.has(port) && node.isConnected) {
        entry.scroll.set(port, { top: node.scrollTop, left: node.scrollLeft });
      }
    };
    const scan = () => {
      const file = document.querySelector<HTMLElement>('[data-nc-report-file-viewer][data-nc-report-file-ready]');
      for (const port of ports) {
        const node = port === 'file' ? file : file?.querySelector<HTMLElement>('[data-nc-code-scroller]');
        if (node == null) continue;
        observed.set(port, node);
        if (!pending.has(port) || acted.has(port)) continue;
        if (port === 'code' && node.dataset.ncCodeScroller === 'measuring') continue;
        const offset = saved?.scroll.get(port);
        if (offset === undefined) continue;
        node.scrollTop = offset.top;
        node.scrollLeft = offset.left;
        const clamped = (offset.top > 0 && Math.abs(node.scrollTop - offset.top) > 1)
          || (offset.left > 0 && Math.abs(node.scrollLeft - offset.left) > 1);
        if (!clamped) pending.delete(port);
        else {
          for (const target of [node, node.firstElementChild]) {
            if (target !== null && !resized.has(target)) {
              resized.add(target);
              resize.observe(target);
            }
          }
          schedule();
        }
      }
    };
    const schedule = () => {
      if (frame === undefined) frame = requestAnimationFrame(() => { frame = undefined; scan(); });
    };
    const resize = new ResizeObserver(schedule);
    const mutations = new MutationObserver(scan);
    const root = document.querySelector('[data-nc-report-file-viewer]') ?? document.querySelector('[data-nc-track-page]');
    if (root !== null) mutations.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-nc-report-file-ready', 'data-nc-code-scroller'] });
    const input = (event: Event) => {
      const target = event.target instanceof Element ? event.target : null;
      if (!isUserScrollInput(event, target)) return;
      if (target?.closest('[data-nc-report-file-viewer]') == null) return;
      const port = target.closest('[data-nc-code-scroller]') === null ? 'file' : 'code';
      acted.add(port);
      pending.delete(port);
    };
    const scroll = (event: Event) => {
      for (const [port, node] of observed) if (event.target === node) remember(port, node);
    };
    scan();
    document.addEventListener('wheel', input, { capture: true, passive: true });
    document.addEventListener('touchmove', input, { capture: true, passive: true });
    document.addEventListener('pointerdown', input, true);
    document.addEventListener('keydown', input, true);
    document.addEventListener('scroll', scroll, true);
    return () => {
      mutations.disconnect();
      resize.disconnect();
      if (frame !== undefined) cancelAnimationFrame(frame);
      document.removeEventListener('wheel', input, true);
      document.removeEventListener('touchmove', input, true);
      document.removeEventListener('pointerdown', input, true);
      document.removeEventListener('keydown', input, true);
      document.removeEventListener('scroll', scroll, true);
      for (const [port, node] of observed) remember(port, node);
    };
  }, [ensureEntry, filePath, saved]);

  const onSourceModeChange = useCallback((sourceMode: boolean) => {
    const entry = ensureEntry();
    if (entry !== undefined) entry.sourceMode = sourceMode;
  }, [ensureEntry]);
  return { restoreScroll: saved !== undefined && saved.scroll.size > 0, initialSourceMode: saved?.sourceMode, onSourceModeChange };
}

/** One input subscription per Track. The nearest scrollport owns each gesture. */
function useTrackScrollPorts(views: ReturnType<typeof useTrackViews>, trackId: string,
  reportReady: boolean, hash: string) {
  const actedRef = useRef(new Set<ScrollPort>());
  useLayoutEffect(() => { actedRef.current.clear(); }, [trackId]);
  useLayoutEffect(() => {
    if (views === null) return;
    const ports: readonly ScrollPort[] = ['page', 'panel'];
    const view = views.ensure(trackId);
    const acted = actedRef.current;
    const observed = new Map<ScrollPort, HTMLElement>();
    const saved = new Map(view.scroll);
    const ready = (port: ScrollPort) => port !== 'page' || reportReady;
    const input = (event: Event) => {
      const target = event.target instanceof Element ? event.target : null;
      const node = target?.closest('[data-nc-panel], [data-nc-track-page]');
      const port = ports.find(candidate => node != null && portNode(candidate) === node);
      if (port === undefined) return;
      if (!isUserScrollInput(event, target)) return;
      acted.add(port);
    };
    const restore = (port: ScrollPort) => {
      const node = portNode(port);
      if (node === null || !ready(port)) return;
      observed.set(port, node);
      const offset = saved.get(port);
      if (offset !== undefined && hash === '' && !acted.has(port)) {
        node.scrollTop = offset.top; node.scrollLeft = offset.left;
      }
    };
    const remember = (event: Event) => {
      for (const port of ports) {
        const node = portNode(port);
        if (ready(port) && node !== null && event.target === node) {
          observed.set(port, node);
          view.scroll.set(port, { top: node.scrollTop, left: node.scrollLeft });
        }
      }
    };
    ports.forEach(restore);
    document.addEventListener('wheel', input, { capture: true, passive: true });
    document.addEventListener('touchmove', input, { capture: true, passive: true });
    document.addEventListener('pointerdown', input, true);
    document.addEventListener('keydown', input, true);
    document.addEventListener('scroll', remember, true);
    return () => {
      document.removeEventListener('wheel', input, true);
      document.removeEventListener('touchmove', input, true);
      document.removeEventListener('pointerdown', input, true);
      document.removeEventListener('keydown', input, true);
      document.removeEventListener('scroll', remember, true);
      for (const [port, node] of observed) {
        if (ready(port)) view.scroll.set(port, { top: node.scrollTop, left: node.scrollLeft });
      }
    };
  }, [hash, reportReady, trackId, views]);
}
