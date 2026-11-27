// Route targets and the single navigation exit.

import { trimPathRight,useNavigate,useRouter,useRouterState,type RouterHistory } from '@tanstack/react-router';
import { useCallback,useMemo } from 'react';

import { useDraftBackOwner,type NavigationSource } from './draft-back-owner.tsx';
import type { MobilePanel,TrackSearch,TrackSource } from './track-search.ts';
import { useTrackViews } from './track-view-state.tsx';

import { formatFileLine, parseFileLine, isFilePath, type FileLineLocation } from '../../../../core/domain/report-file.ts';

export type { MobilePanel,TrackSearch,TrackSource } from './track-search.ts';

/** `ncPanelPushed` tells close to `back()` rather than `replace()`: `replace` never merges with the previous entry, and `back()` on a cold deep link would leave the app. */
declare module '@tanstack/history' {
  interface HistoryState {
    ncPanelPushed?: boolean;
    /** New-track Back restores the report that opened the draft; cold links stay in the app. */
    ncNewTrackPushed?: boolean;
    /** A plain Track selection resumes its saved viewport; explicit targets reveal themselves. */
    ncResumeTrackView?: boolean;
    /** Explicit report-anchor activation, independent of file/panel navigation. */
    ncReportAnchorIntent?: string;
    ncFilePushed?: boolean;
    ncFileBackPath?: string;
    ncFileSilent?: boolean;
    ncFileOpenRequest?: number;
    ncOpenPlanner?: boolean;
  }
}

export const PANEL_PUSHED_STATE_KEY = 'ncPanelPushed';
export const FILE_PUSHED_STATE_KEY = 'ncFilePushed';
export const PLANNER_OPEN_STATE_KEY = 'ncOpenPlanner';

export type NavTarget =
  | Readonly<{ name: 'today'; day?: string; view?: 'today' }>
  | Readonly<{ name: 'memory' }>
  | Readonly<{ name: 'area-memory'; areaId: string }>
  /** Starting a track is a route, not a dialog; the track row is minted on submit, not on entering the route. */
  | Readonly<{ name: 'new-track'; areaId: string }>
  /** `blockId` rides in the hash; `cardId`/`filePath`/`panel`/`from` become `?card=`/`?file=`/`?panel=`/`?from=`. */
  | Readonly<{
    name: 'track';
    trackId: string;
    blockId?: string;
    cardId?: string;
    filePath?: string;
    fileLine?: number | string;
    panel?: MobilePanel;
    from?: TrackSource;
    openPlanner?: boolean;
  }>
  | Readonly<{ name: 'settings' }>
  /** Settings › Plugins — the installed list and its enable/disable switch. */
  /** Settings › Planners — whether each Planner provider can run now (#1817). */
  | Readonly<{ name: 'settings-planners' }>
  | Readonly<{ name: 'settings-appearance' }>
  | Readonly<{ name: 'settings-about' }>;

export type GoOptions = Readonly<{ replace?: boolean }>;

type TrackRouteAlias = Readonly<{ trackId: string; path: string }>;

function trackPath(trackId: string, alias?: TrackRouteAlias): string {
  return alias?.trackId === trackId ? alias.path : pathFor({ name: 'track', trackId });
}

export function pathFor(target: NavTarget): string {
  switch (target.name) {
    case 'today': return '/';
    case 'memory': return '/memory';
    case 'area-memory': return `/area/${encodeURIComponent(target.areaId)}/memory`;
    case 'new-track': return `/area/${encodeURIComponent(target.areaId)}/new`;
    case 'track': return `/track/${encodeURIComponent(target.trackId)}`;
    case 'settings': return '/settings';
    case 'settings-planners': return '/settings/planners';
    case 'settings-appearance': return '/settings/appearance';
    case 'settings-about': return '/settings/about';
  }
}

/** Navigation is a `<button>` plus this callback, never `<a href>`: mixing the two forks Enter/Space activation semantics. */
export function useGo(alias?: TrackRouteAlias): (target: NavTarget, options?: GoOptions) => void {
  const navigate = useNavigate();
  const router = useRouter();
  const views = useTrackViews();
  const owner = useDraftBackOwner();
  return useCallback((target: NavTarget, options?: GoOptions) => {
    const execute = (source: NavigationSource) => {
      const destination = target.name === 'track' ? trackPath(target.trackId, alias) : pathFor(target);
      // The block anchor rides in the hash: the track route remounts per track, so
      // state set before `go` would be discarded by the very move it describes.
      const hash = target.name === 'track' ? target.blockId : undefined;
      // All fields are built from the target, never spread from the previous
      // location: "not passed" means "cleared".
      const search: Record<string, unknown> = target.name === 'track'
        ? buildTrackSearch({ card: target.cardId, file: target.filePath, line: target.fileLine, panel: target.panel, from: target.from })
        : target.name === 'today'
          ? {
            ...(target.day !== undefined ? { day: target.day } : {}),
            ...(target.view !== undefined ? { view: target.view } : {}),
          }
          : {};
      // A plain cross-track selection resumes that track. Explicit links and
      // same-track commands retain their existing clear/replace semantics.
      const resumeTrack = target.name === 'track' && target.blockId === undefined
        && target.cardId === undefined && target.filePath === undefined
        && target.panel === undefined && target.openPlanner !== true
        && !source.pathname.endsWith(destination);
      const resumedView = resumeTrack ? views?.get(target.trackId) : undefined;
      if (resumedView !== undefined) Object.assign(search, resumedView.search);
      // The planner-open intent rides on the history entry this navigation creates;
      // written only when asked for, so an ordinary move leaves `state` alone.
      const state = target.name === 'track' && target.openPlanner === true
        ? { [PLANNER_OPEN_STATE_KEY]: true }
        : undefined;
      const switchingDraft = target.name === 'new-track' && isNewTrackPath(source.pathname);
      const draftState = target.name === 'new-track'
        ? { ncNewTrackPushed: switchingDraft ? source.state.ncNewTrackPushed === true : options?.replace !== true }
        : undefined;
      void navigate({
        to: destination,
        hash,
        search,
        replace: switchingDraft || options?.replace,
        ...(draftState === undefined ? {} : { state: draftState }),
        ...(target.name === 'track' ? { state: { ...state, ncResumeTrackView: resumeTrack,
          ...(target.blockId === undefined ? {} : { ncReportAnchorIntent: Array.from(crypto.getRandomValues(new Uint32Array(4))).join('-') }) } } : {}),
      });
    };
    if (owner === null) execute(router.parseLocation((router.history as RouterHistory).location));
    else owner.go(execute);
  }, [navigate, router, views, owner, alias]);
}

function isNewTrackPath(path: string): boolean {
  return routeParamFromPath(path, '/area/') !== undefined && path.endsWith('/new');
}

/** An explicit entry marker prevents cold draft links from navigating outside the app. */
export function useNewTrackBack(): (() => void) | null {
  const history = useRouter().history as RouterHistory;
  const owner = useDraftBackOwner();
  const location = useRouterState({ select: (state) => state.location });
  const go = useGo();
  const back = useCallback(() => {
    if (location.state.ncNewTrackPushed === true && history.canGoBack()) {
      if (owner === null) throw new Error('Draft Back requires app router context');
      owner.back();
    }
    else go({ name: 'today' }, { replace: true });
  }, [go, history, location.state.ncNewTrackPushed, owner]);
  return isNewTrackPath(location.pathname) ? back : null;
}

/**
 * The planner-open intent belongs to the one history entry the navigation
 * creates: it is consumed and struck off the first time that entry's route body
 * mounts, whether or not a planner card exists yet.
 */
export type PlannerOpenIntent = Readonly<{
  armed: boolean;
  disarm: () => void;
}>;

export function hasPlannerOpenMarker(state: unknown): boolean {
  if (typeof state !== 'object' || state === null) return false;
  return (state as Record<string, unknown>)[PLANNER_OPEN_STATE_KEY] === true;
}

export function usePlannerOpenIntent(trackId: string): PlannerOpenIntent {
  const navigate = useNavigate();
  const location = useRouterState({ select: (state) => state.location });
  const armed = hasPlannerOpenMarker(location.state)
    && routeParamFromPath(location.pathname, '/track/') === trackId;
  const disarm = useCallback(() => {
    void navigate({
      to: pathFor({ name: 'track', trackId }),
      // Everything else about this entry is kept: the disarm is not a
      // navigation the reader asked for, and it must be invisible to them.
      search: true,
      hash: true,
      replace: true,
      state: (previous) => {
        const next = { ...previous };
        delete next[PLANNER_OPEN_STATE_KEY];
        return next;
      },
    });
  }, [navigate, trackId]);
  return useMemo(() => ({ armed, disarm }), [armed, disarm]);
}

/** `card`, `file` and `panel` are mutually exclusive: a persisted card wins, then a transient file, then a panel. */
function buildTrackSearch(fields: Readonly<{
  card?: string; file?: string; line?: number | string; panel?: MobilePanel; from?: TrackSource;
}>): TrackSearch {
  const search: { card?: string; file?: string; line?: number | string; panel?: MobilePanel; from?: TrackSource } = {};
  if (fields.card !== undefined) search.card = fields.card;
  else if (fields.file !== undefined) {
    search.file = fields.file;
    const line = trackFileLine(fields.line);
    if (line !== undefined) search.line = line;
  } else if (fields.panel !== undefined) search.panel = fields.panel;
  if (fields.from !== undefined) search.from = fields.from;
  return search;
}

/** The card the current URL points at, or `null`. */
export function useRouteCardId(): string | null {
  return useRouterState({ select: (state) => cardIdFromLocation(state.location) });
}

/** The normalized file named by the current URL. */
export function useRouteFilePath(): string | null {
  return useRouterState({ select: (state) => filePathFromLocation(state.location) });
}

/** The shape every parser here reads; `ParsedLocation` satisfies it. */
export type SearchCarrier = Readonly<{
  searchStr?: string;
  search?: unknown;
  href?: string;
}>;

export function cardIdFromLocation(location: SearchCarrier): string | null {
  return rawParamFromLocation(location, 'card');
}

export function cardIdFromSearchString(searchStr: string): string | null {
  return rawParamFromSearchString(searchStr, 'card');
}

function parseRouteFilePath(value: string | null): string | null {
  if (value === null) return null;
  if (value.startsWith('"')) {
    // TanStack quotes filenames such as "42" to retain their string type.
    try {
      const decoded: unknown = JSON.parse(value);
      if (typeof decoded === 'string') return isFilePath(decoded) ? decoded : null;
    } catch {
      // A hand-edited URL can still name a file that begins with a quote.
    }
  }
  return isFilePath(value) ? value : null;
}

export function filePathFromSearchString(searchStr: string): string | null {
  return parseRouteFilePath(rawParamFromSearchString(searchStr, 'file'));
}

export function filePathFromLocation(location: SearchCarrier): string | null {
  return parseRouteFilePath(rawParamFromLocation(location, 'file'));
}

function trackFileLine(value: unknown): number | string | undefined {
  const line = parseFileLine(value);
  // TanStack quotes strings it could otherwise parse as numbers. Keep ?line=42 readable.
  return line.endLine === undefined ? line.line : formatFileLine(line);
}

export function useRouteFileLine(): string | undefined {
  return useRouterState({ select: (state) => formatFileLine(parseFileLine(rawParamFromLocation(state.location, 'line'))) });
}

/** A value outside the union is dropped, not thrown on: the query string is user-editable text. */
export function panelFromSearchString(searchStr: string): MobilePanel | null {
  return asMobilePanel(rawParamFromSearchString(searchStr, 'panel'));
}

export function fromFromSearchString(searchStr: string): TrackSource | null {
  return asTrackSource(rawParamFromSearchString(searchStr, 'from'));
}

export function panelFromLocation(location: SearchCarrier): MobilePanel | null {
  return asMobilePanel(rawParamFromLocation(location, 'panel'));
}

export function fromFromLocation(location: SearchCarrier): TrackSource | null {
  return asTrackSource(rawParamFromLocation(location, 'from'));
}

export function asMobilePanel(value: string | null): MobilePanel | null {
  switch (value) {
    case 'outline': case 'cards': case 'tasks': case 'conversations': return value;
    default: return null;
  }
}

export function asTrackSource(value: string | null): TrackSource | null {
  switch (value) {
    case 'pages': case 'area': return value;
    default: return null;
  }
}

/** Reads the raw query string so a repeated key is rejected rather than folded by a parser. */
function rawParamFromLocation(location: SearchCarrier, key: string): string | null {
  if (typeof location.searchStr === 'string' && location.searchStr !== '') {
    return rawParamFromSearchString(location.searchStr, key);
  }
  if (typeof location.search === 'string') return rawParamFromSearchString(location.search, key);
  if (typeof location.href === 'string' && location.href.includes('?')) {
    const query = location.href.split('?')[1]?.split('#')[0] ?? '';
    const fromHref = rawParamFromSearchString(query, key);
    if (fromHref !== null) return fromHref;
    if (new URLSearchParams(query).getAll(key).length !== 1) return null;
  }
  if (typeof location.search === 'object' && location.search !== null) {
    const value = (location.search as Record<string, unknown>)[key];
    return typeof value === 'string' && value !== '' ? value : null;
  }
  return null;
}

function rawParamFromSearchString(searchStr: string, key: string): string | null {
  const raw = searchStr.startsWith('?') ? searchStr.slice(1) : searchStr;
  if (raw === '') return null;
  const values = new URLSearchParams(raw).getAll(key);
  if (values.length !== 1) return null;
  const value = values[0];
  return value === undefined || value === '' ? null : value;
}

/** The panel the current URL points at, or `null`. */
export function useRoutePanel(): MobilePanel | null {
  return useRouterState({ select: (state) => panelFromLocation(state.location) });
}

/** The surface the reader came from, or `null` (callers default to `pages`). */
export function useRouteFrom(): TrackSource | null {
  return useRouterState({ select: (state) => fromFromLocation(state.location) });
}

/** `validateSearch` for `/track/$trackId`; a repeated key arrives as an array and is dropped. */
export function validateTrackSearch(search: Record<string, unknown>): TrackSearch {
  const card = search.card;
  const file = search.file;
  const panel = search.panel;
  const from = search.from;
  return buildTrackSearch({
    card: typeof card === 'string' && card !== '' ? card : undefined,
    file: typeof file === 'string' && isFilePath(file) ? file : undefined,
    line: trackFileLine(search.line),
    panel: asMobilePanel(typeof panel === 'string' ? panel : null) ?? undefined,
    from: asTrackSource(typeof from === 'string' ? from : null) ?? undefined,
  });
}

/**
 * The panel a renderer may open: `?panel=` only counts on a compact viewport
 * (above the breakpoint it would `inert` the desktop panel), and an open overlay
 * owns the surface.
 */
export function renderedMobilePanel(
  panel: MobilePanel | null,
  visit: Readonly<{ compact: boolean; overlayOpen: boolean }>,
): MobilePanel | null {
  if (!visit.compact || visit.overlayOpen) return null;
  return panel;
}

/** The block anchor the current URL points at, or `null`. */
export function useRouteHash(): string | null {
  const hash = useRouterState({ select: (state) => state.location.hash });
  return hash === '' ? null : hash;
}

export function useCurrentPath(): string {
  return useRouterState({ select: (state) => state.location.pathname });
}

/** Reads a path segment off the URL; `useParams({ strict: false })` widens to `any` outside a typed route context. */
export function useRouteParam(prefix: '/area/' | '/track/'): string | undefined {
  const path = useCurrentPath();
  return routeParamFromPath(path, prefix);
}

export function routeParamFromPath(path: string, prefix: '/area/' | '/track/'): string | undefined {
  if (!path.startsWith(prefix)) return undefined;
  const segment = path.slice(prefix.length).split('/', 1)[0];
  if (segment === '') return undefined;
  try {
    return decodeURIComponent(segment);
  } catch {
    return undefined;
  }
}

/**
 * The fields exactly as they stand in `location`, deliberately not run
 * through `buildTrackSearch`: the card/panel exclusion is a rule about the URL a
 * navigation produces, not the one it reads.
 */
export function trackSearchFromLocation(location: SearchCarrier): TrackSearch {
  const search: { card?: string; file?: string; line?: number | string; panel?: MobilePanel; from?: TrackSource } = {};
  const card = cardIdFromLocation(location);
  const file = filePathFromLocation(location);
  const panel = panelFromLocation(location);
  const from = fromFromLocation(location);
  if (card !== null) search.card = card;
  if (file !== null) {
    search.file = file;
    const line = trackFileLine(rawParamFromLocation(location, 'line'));
    if (line !== undefined) search.line = line;
  }
  if (panel !== null) search.panel = panel;
  if (from !== null) search.from = from;
  return search;
}

/** A key *present* with `undefined` clears that field; an absent key keeps what the URL holds (own-property, never value). */
export type TrackSearchPatch = Readonly<{
  card?: string | undefined;
  file?: string | undefined;
  line?: number | string | undefined;
  panel?: MobilePanel | undefined;
  from?: TrackSource | undefined;
}>;

/**
 * The search `useGoSameTrack` would navigate to, or `null` when `location` is
 * not on `expectedTrackId`. Rebuilt field by field, never `{ ...prev, ...patch }`.
 */
export function sameTrackSearch(
  location: SearchCarrier & Readonly<{ pathname: string }>,
  expectedTrackId: string,
  patch: TrackSearchPatch,
  alias?: TrackRouteAlias,
): TrackSearch | null {
  if (routeParamFromPath(location.pathname, '/track/') !== expectedTrackId &&
    !(alias?.trackId === expectedTrackId && trimPathRight(location.pathname) === trimPathRight(alias.path))) return null;
  const current = trackSearchFromLocation(location);
  return buildTrackSearch({
    card: Object.hasOwn(patch, 'card') ? patch.card : current.card,
    file: Object.hasOwn(patch, 'file') ? patch.file : current.file,
    line: Object.hasOwn(patch, 'file') || Object.hasOwn(patch, 'line') ? patch.line : current.line,
    panel: Object.hasOwn(patch, 'panel') ? patch.panel : current.panel,
    from: Object.hasOwn(patch, 'from') ? patch.from : current.from,
  });
}

export type GoSameTrack = (
  expectedTrackId: string,
  patch: TrackSearchPatch,
  options?: GoOptions,
) => void;

/** The keeping exit: edit one whitelisted field of the current track's URL and leave the others, including the hash. */
export function useGoSameTrack(alias?: TrackRouteAlias): GoSameTrack {
  const navigate = useNavigate();
  const go = useGo(alias);
  const location = useRouterState({ select: (state) => state.location });
  return useCallback((expectedTrackId, patch, options) => {
    const search = sameTrackSearch(location, expectedTrackId, patch, alias);
    if (search === null) {
      go(
        {
          name: 'track', trackId: expectedTrackId, cardId: patch.card,
          filePath: patch.file, fileLine: patch.line, panel: patch.panel, from: patch.from,
        },
        options,
      );
      return;
    }
    void navigate({
      to: trackPath(expectedTrackId, alias),
      search,
      // `true` keeps the current value; `undefined` would clear it.
      hash: true,
      state: true,
      replace: options?.replace,
    });
  }, [go, location, navigate, alias]);
}

export function hasPanelPushedMarker(state: unknown): boolean {
  if (typeof state !== 'object' || state === null) return false;
  return (state as Record<string, unknown>)[PANEL_PUSHED_STATE_KEY] === true;
}

export function hasFilePushedMarker(state: unknown): boolean {
  if (typeof state !== 'object' || state === null) return false;
  return (state as Record<string, unknown>)[FILE_PUSHED_STATE_KEY] === true;
}

export type FileOpenOptions = FileLineLocation & Readonly<{ focus?: boolean; fromPath?: string }>;

export type TrackFileNavigation = Readonly<{
  openFile: (expectedTrackId: string, path: string, options?: FileOpenOptions) => boolean;
  closeFile: (expectedTrackId: string) => void;
}>;

/** The label follows the same history guard as closeFile. */
export function useRouteFileBackPath(): string | null {
  const history = useRouter().history as RouterHistory;
  return useRouterState({ select: (state) => hasFilePushedMarker(state.location.state)
    && history.canGoBack() && typeof state.location.state.ncFileBackPath === 'string'
    ? state.location.state.ncFileBackPath : null });
}

/** Distinct files push; same-file jumps and surface switches replace. */
export function useTrackFileNavigation(alias?: TrackRouteAlias): TrackFileNavigation {
  const router = useRouter();
  const history = router.history as RouterHistory;
  const navigate = useNavigate();
  const go = useGo(alias);

  const openFile = useCallback((expectedTrackId: string, path: string, options?: FileOpenOptions) => {
    const location = router.parseLocation(history.location);
    if (!isFilePath(path)) return false;
    const line = formatFileLine(options ?? {});
    const silent = options?.focus === false;
    const search = sameTrackSearch(location, expectedTrackId, {
      file: path, line, card: undefined, panel: undefined,
    }, alias);
    if (search === null) {
      if (silent) return false;
      go({ name: 'track', trackId: expectedTrackId, filePath: path, fileLine: line });
      return true;
    }
    const currentFile = filePathFromLocation(location);
    const sameFile = currentFile === path;
    const switchingPanel = panelFromLocation(location) !== null;
    const leavingCard = cardIdFromLocation(location) !== null;
    const replace = sameFile || switchingPanel || leavingCard;
    const pushed = !replace || (!leavingCard && (
      (sameFile && hasFilePushedMarker(location.state)) || (switchingPanel && hasPanelPushedMarker(location.state))
    ));
    const backPath = sameFile
      ? (typeof location.state.ncFileBackPath === 'string' ? location.state.ncFileBackPath : undefined)
      : switchingPanel
        ? (hasPanelPushedMarker(location.state) && typeof location.state.ncFileBackPath === 'string'
          ? location.state.ncFileBackPath : undefined)
        : (!leavingCard && currentFile !== null ? (options?.fromPath ?? currentFile) : undefined);
    void navigate({
      to: trackPath(expectedTrackId, alias),
      search,
      hash: true,
      replace,
      state: (previous) => ({
        ...previous,
        ncPanelPushed: false,
        ncFilePushed: pushed,
        ncFileBackPath: backPath,
        ncFileSilent: silent,
        // A new request can repeat the same file and line.
        ncFileOpenRequest: (previous.ncFileOpenRequest ?? 0) + 1,
      }),
    });
    return true;
  }, [go, history, navigate, router, alias]);

  const closeFile = useCallback((expectedTrackId: string) => {
    const location = router.parseLocation(history.location);
    if (hasFilePushedMarker(location.state) && history.canGoBack()) {
      history.back();
      return;
    }
    const search = sameTrackSearch(location, expectedTrackId, { file: undefined }, alias);
    if (search === null) {
      go({ name: 'track', trackId: expectedTrackId }, { replace: true });
      return;
    }
    void navigate({
      to: trackPath(expectedTrackId, alias),
      search,
      hash: true,
      replace: true,
    });
  }, [go, history, navigate, router, alias]);

  return { openFile, closeFile };
}

export type TrackPanelNavigation = Readonly<{
  openPanel: (expectedTrackId: string, panel: MobilePanel) => void;
  closePanel: (expectedTrackId: string) => void;
}>;

/**
 * Opening a panel pushes a marked entry, panel-to-panel replaces it, close pops
 * the marked entry; a cold `?panel=` deep link has no marker, so close replaces
 * (an unconditional `back()` would walk out of the app).
 */
export function useTrackPanelNavigation(alias?: TrackRouteAlias): TrackPanelNavigation {
  // `useRouter()` is registered as `AnyRouter`, so its `history` widens to
  // `any`; naming the type back is what keeps `canGoBack`/`back` checked.
  const history = useRouter().history as RouterHistory;
  const navigate = useNavigate();
  const go = useGo(alias);
  const location = useRouterState({ select: (state) => state.location });

  const openPanel = useCallback((expectedTrackId: string, panel: MobilePanel) => {
    const search = sameTrackSearch(location, expectedTrackId, {
      panel, card: undefined, file: undefined,
    }, alias);
    if (search === null) {
      go({ name: 'track', trackId: expectedTrackId, panel });
      return;
    }
    const switching = panelFromLocation(location) !== null;
    const switchingFile = filePathFromLocation(location) !== null;
    const backPath = switchingFile && hasFilePushedMarker(location.state) && typeof location.state.ncFileBackPath === 'string'
      ? location.state.ncFileBackPath
      : undefined;
    void navigate({
      to: trackPath(expectedTrackId, alias),
      search,
      hash: true,
      replace: switching || switchingFile,
      state: switching ? true : {
        ncPanelPushed: !switchingFile || hasFilePushedMarker(location.state),
        ncFileBackPath: backPath,
      },
    });
  }, [go, location, navigate, alias]);

  const closePanel = useCallback((expectedTrackId: string) => {
    if (hasPanelPushedMarker(location.state) && history.canGoBack()) {
      history.back();
      return;
    }
    const search = sameTrackSearch(location, expectedTrackId, { panel: undefined }, alias);
    if (search === null) {
      go({ name: 'track', trackId: expectedTrackId }, { replace: true });
      return;
    }
    void navigate({
      to: trackPath(expectedTrackId, alias),
      search,
      hash: true,
      replace: true,
    });
  }, [go, history, location, navigate, alias]);

  return { openPanel, closePanel };
}
