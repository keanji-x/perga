import { trimPathRight, useRouterState } from '@tanstack/react-router';
import type { Area } from '../../../../core/domain/area.ts';
import type { AppEntry } from './app-history.tsx';
import { isMobileFocusHomeLocation, isNewTrackPath, routeParamFromPath, useAppBack, type NavTarget } from './navigation.ts';

function isAreaMemoryPath(path: string): boolean {
  return /^\/area\/[^/]+\/memory$/.test(path);
}

export function mobileBackLabel(entry: AppEntry | null, fallback: string, areas: readonly Pick<Area, 'id' | 'name'>[]): string {
  if (entry === null) return fallback;
  const path = trimPathRight(entry.pathname);
  if (path === '/') return isMobileFocusHomeLocation(entry) ? 'Focus' : 'Today';
  if (path === '/workspace') return 'Workspace';
  if (path === '/memory' || isAreaMemoryPath(path)) return 'Memory';
  if (path.startsWith('/track/')) return 'Report';
  if (path === '/settings' || path.startsWith('/settings/')) return 'Settings';
  const areaId = routeParamFromPath(path, '/area/');
  return areas.find((area) => area.id === areaId)?.name ?? fallback;
}

export function useMobileBack(fallback: NavTarget, fallbackLabel: string, areas: readonly Area[]) {
  const { previousEntry, goBack } = useAppBack();
  return { backLabel: mobileBackLabel(previousEntry, fallbackLabel, areas), onBack: () => goBack(fallback) };
}

/** Before a route commits, its predecessor still owns the shell header. */
export function useMobileHeaderPath() {
  return useRouterState({ select: (state) => trimPathRight(
    (state.isLoading ? state.resolvedLocation ?? state.location : state.location).pathname,
  ) });
}

/** The shell's report, Memory, Today and draft routes use the same Back slot. */
export function useMobileShellBack(areas: readonly Area[], shareMode: boolean, draftArea?: Pick<Area, 'id' | 'name'>) {
  const path = useMobileHeaderPath();
  const { canGoBack, previousEntry, goBack } = useAppBack();
  const areaId = routeParamFromPath(path, '/area/');
  const parent = areaId === undefined || shareMode ? undefined
    : areas.find((area) => area.id === areaId) ?? (draftArea?.id === areaId ? draftArea : undefined);
  const fallback: NavTarget = areaId !== undefined && !shareMode
    && (isAreaMemoryPath(path) || (isNewTrackPath(path) && parent !== undefined))
    ? { name: 'area', areaId } : { name: 'today' };
  return {
    backLabel: mobileBackLabel(previousEntry, fallback.name === 'area' ? parent?.name ?? 'Area' : 'Focus', areas),
    onBack: shareMode && !canGoBack ? undefined : () => goBack(fallback),
  };
}
