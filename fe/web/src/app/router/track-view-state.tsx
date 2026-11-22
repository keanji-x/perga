import { createTerminalWorkbenchSessions } from '../../features/track/workbench/public.tsx';
import { createContext, useContext, useLayoutEffect, useRef, type ReactNode } from 'react';
import { useRouterState } from '@tanstack/react-router';
import { useState } from '../../ui/state/public.ts';
import { createReportPreparationCache } from '../../features/report/document/preparation.ts';
import type { TrackSearch } from './track-search.ts';

type Offset = Readonly<{ top: number; left: number }>;
type TrackView = {
  search: TrackSearch;
  progressOpen: boolean;
  scroll: Map<string, Offset>;
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
        view = { search: {}, scroll: new Map(), progressOpen: false };
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
      const { card, file, panel } = search;
      views.ensure(trackId).search = { card, file, panel };
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
      if (event.type === 'pointerdown' && target?.closest('button, a, input, textarea, select, [role="button"], [contenteditable="true"]') !== null) return;
      if (event instanceof KeyboardEvent) {
        if (!['PageDown', 'PageUp', 'Home', 'End', 'ArrowDown', 'ArrowUp', ' '].includes(event.key)) return;
        if (target?.closest('input, textarea, select, [contenteditable="true"]') !== null) return;
        if (event.key === ' ' && target?.closest('button, a, [role="button"]') !== null) return;
      }
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
