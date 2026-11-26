import { useCallback, useEffect } from 'react';

import type { Area } from '../../../../../core/domain/area.ts';
import { useState } from '../../../ui/state/public.ts';

/** Routes can reopen an Area's Tracks or the main Workspace view. */
type MobileSection =
  | Readonly<{ kind: 'areas'; areaId: string | undefined }>
  | Readonly<{ kind: 'tracks'; areaId: string | undefined; returnTo?: 'areas' | 'report' }>;

export type OpenMobileSection = (section: MobileSection, returnFocus?: 'opener' | 'workspace') => void;

export type MobileReturnIntent =
  | Readonly<{ kind: 'opener'; path: string; element: HTMLElement | null }>
  | Readonly<{ kind: 'workspace'; path: string }>
  | null;

type MobileNavigationState = Readonly<{
  section: MobileSection | null;
  closing: boolean;
  returnIntent: MobileReturnIntent;
}>;

const CLOSED_NAVIGATION: MobileNavigationState = Object.freeze({
  section: null,
  closing: false,
  returnIntent: null,
});

type UseMobileNavigationOptions = Readonly<{
  compact: boolean;
  suppressed: boolean;
  currentPath: string;
  areas: readonly Area[];
  activeAreaId: string | undefined;
  onBeforeOpen: () => void;
}>;

/** Cohesive mobile workspace-sheet state machine: keeps section, closing, and return focus together. */
export function useMobileNavigation({
  compact,
  suppressed,
  currentPath,
  areas,
  activeAreaId,
  onBeforeOpen,
}: UseMobileNavigationOptions) {
  const [state, setState] = useState<MobileNavigationState>(CLOSED_NAVIGATION);

  const resetImmediate = useCallback(() => {
    setState(CLOSED_NAVIGATION);
  }, []);

  const close = useCallback((immediate = false) => {
    if (immediate) {
      setState(CLOSED_NAVIGATION);
      return;
    }
    setState((current) => (current.section === null || current.closing ? current : { ...current, closing: true }));
  }, []);

  const finishClose = useCallback(() => {
    setState(CLOSED_NAVIGATION);
  }, []);

  const select = useCallback((section: MobileSection) => {
    setState((current) => ({ ...current, section, closing: false }));
  }, []);

  const open: OpenMobileSection = useCallback((section, returnFocus = 'opener') => {
    const returnIntent: MobileReturnIntent = returnFocus === 'workspace'
      ? { kind: 'workspace', path: currentPath }
      : { kind: 'opener', path: currentPath, element: document.activeElement instanceof HTMLElement ? document.activeElement : null };
    // Daily/system Tracks have no visible workspace entry. Their switch starts in the active user Area.
    const nextSection: MobileSection = section.kind === 'tracks' && section.returnTo === 'report'
      ? (() => {
        const areaId = areas.find((area) => area.id === section.areaId)?.id ?? activeAreaId;
        return areaId === undefined ? { kind: 'areas', areaId: undefined } : { ...section, areaId };
      })()
      : section;
    onBeforeOpen();
    setState({ section: nextSection, closing: false, returnIntent });
  }, [activeAreaId, areas, currentPath, onBeforeOpen]);

  useEffect(() => {
    if (!compact && state.section !== null) {
      setState(CLOSED_NAVIGATION);
    }
  }, [compact, state.section]);

  const isOpen = state.section !== null;
  const interactive = compact && isOpen && !state.closing;
  const dialogActive = interactive && !suppressed;

  return {
    section: state.section,
    returnIntent: state.returnIntent,
    isOpen,
    interactive,
    dialogActive,
    open,
    select,
    close,
    finishClose,
    resetImmediate,
  };
}

export type MobileNavigationController = ReturnType<typeof useMobileNavigation>;
