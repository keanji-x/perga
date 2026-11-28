import { useRouter, useRouterState, type RouterHistory } from '@tanstack/react-router';
import { createContext, useCallback, useContext, useLayoutEffect, useRef, useSyncExternalStore, type ReactNode } from 'react';
import { useState } from '../../ui/state/public.ts';

export type AppEntry = Readonly<{ pathname: string; searchStr: string }>;
type ObservedEntry = { location: AppEntry; scrollTop: number; view?: unknown; returnFocus?: string };
type HistoryAction = Parameters<Parameters<RouterHistory['subscribe']>[0]>[0]['action']['type'];
type Snapshot = Readonly<{
  canGoBack: boolean;
  previousEntry: AppEntry | null;
  lastAction: HistoryAction | null;
}>;

const INITIAL: Snapshot = Object.freeze({ canGoBack: false, previousEntry: null, lastAction: null });
const emptySnapshot = () => INITIAL;
const noSubscribe = () => () => {};

/** The router owns navigation. This mount-local observer remembers which
 * entries the app has seen, along with their mobile scroll positions. */
export function createAppHistory(history: RouterHistory, readEntry: (location: RouterHistory['location']) => AppEntry) {
  let current = history.location.state.__TSR_index;
  let floor = current;
  let snapshot = INITIAL;
  const entries = new Map<number, ObservedEntry>([[current, { location: readEntry(history.location), scrollTop: 0 }]]);
  const listeners = new Set<() => void>();

  return {
    observe: () => history.subscribe(({ location, action }) => {
      const index = location.state.__TSR_index;
      const entry = readEntry(location);
      if (action.type === 'PUSH') {
        for (const key of entries.keys()) {
          if (key >= index) entries.delete(key);
        }
      }
      const observed = entries.get(index)?.location;
      if (observed?.pathname !== entry.pathname || observed.searchStr !== entry.searchStr) {
        entries.set(index, { location: entry, scrollTop: 0 });
      }
      current = index;
      floor = Math.min(floor, current);
      const canGoBack = current > floor && history.canGoBack();
      snapshot = {
        canGoBack,
        previousEntry: canGoBack ? entries.get(current - 1)?.location ?? null : null,
        lastAction: action.type,
      };
      for (const listener of listeners) listener();
    }),
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    snapshot: () => snapshot,
    stepsBackOutOf: (predicate: (entry: AppEntry) => boolean) => {
      if (!snapshot.canGoBack) return 0;
      for (let index = current; index >= floor; index--) {
        const entry = entries.get(index)?.location;
        if (entry === undefined) continue;
        if (!predicate(entry)) return current - index;
      }
      return 0;
    },
    returnFocus: (index: number) => index === current ? entries.get(index)?.returnFocus : undefined,
    rememberReturnFocus: (key: string) => {
      const entry = entries.get(current);
      if (entry !== undefined) entry.returnFocus = key;
    },
    scrollTop: (index: number) => entries.get(index)?.scrollTop ?? 0,
    rememberScroll: (index: number, top: number) => {
      const entry = entries.get(index);
      if (entry !== undefined) entry.scrollTop = top;
    },
    readView: (index: number) => entries.get(index)?.view,
    rememberView: (index: number, view: unknown) => {
      const entry = entries.get(index);
      if (entry !== undefined) entry.view = view;
    },
  };
}

const AppHistoryContext = createContext<ReturnType<typeof createAppHistory> | null>(null);

export function AppHistoryProvider({ children }: { children: ReactNode }) {
  const router = useRouter();
  const [stack] = useState(() => createAppHistory(router.history as RouterHistory, (location) => {
    const { pathname, searchStr } = router.parseLocation(location);
    return { pathname, searchStr };
  }));
  useLayoutEffect(() => stack.observe(), [stack]);
  return <AppHistoryContext.Provider value={stack}>{children}</AppHistoryContext.Provider>;
}

export function useAppHistory() {
  const stack = useContext(AppHistoryContext);
  const snapshot = useSyncExternalStore(stack?.subscribe ?? noSubscribe, stack?.snapshot ?? emptySnapshot);
  return { stack, ...snapshot };
}

/** Keep the current page's entry until its successor is ready to render. */
export function useHistoryEntryIndex() {
  return useRouterState({ select: (state) =>
    (state.isLoading ? state.resolvedLocation ?? state.location : state.location).state.__TSR_index,
  });
}

export function useHistoryReturnFocus(ready: boolean) {
  const { stack, lastAction } = useAppHistory();
  const index = useHistoryEntryIndex();
  const restoreReturnFocus = useCallback(() => {
    if (!ready || lastAction !== 'BACK' || document.activeElement !== document.body) return;
    const key = stack?.returnFocus(index);
    if (key === undefined) return;
    const target = Array.from(document.querySelectorAll<HTMLButtonElement>('[data-nc-return-focus]'))
      .find((button) => button.dataset.ncReturnFocus === key);
    if (target?.isConnected && target.closest('[inert]') === null
      && target.getClientRects().length > 0 && getComputedStyle(target).visibility === 'visible') {
      target.focus({ preventScroll: true });
    }
  }, [index, lastAction, ready, stack]);

  useLayoutEffect(restoreReturnFocus, [restoreReturnFocus]);

  const rememberReturnFocus = (trigger: HTMLButtonElement) => {
    const key = trigger.dataset.ncReturnFocus;
    if (key !== undefined) stack?.rememberReturnFocus(key);
  };
  return { rememberReturnFocus, restoreReturnFocus };
}

/** Keep scroll and view controls together for the page keyed by this history index. */
export function useMobileHistoryView<View>(index: number, ready: boolean) {
  const stack = useContext(AppHistoryContext);
  const [initialView] = useState(() => stack?.readView(index) as View | undefined);
  const ref = useRef<HTMLDivElement>(null);
  const restored = useRef(false);
  useLayoutEffect(() => {
    const node = ref.current;
    if (stack === null || node === null) return;
    if (!restored.current) {
      const top = stack.scrollTop(index);
      if (!ready && top > node.scrollHeight - node.clientHeight) return;
      node.scrollTop = top;
      restored.current = true;
    }
    const remember = () => stack.rememberScroll(index, node.scrollTop);
    node.addEventListener('scroll', remember);
    return () => { node.removeEventListener('scroll', remember); };
  }, [index, ready, stack]);
  const rememberView = useCallback((view: View) => stack?.rememberView(index, view), [index, stack]);
  return { initialView, rememberView, scrollRef: ref };
}
