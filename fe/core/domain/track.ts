import type { CreateTrackRequest, PatchTrackRequest, OpenTerminalRequest } from '../api/generated/wire.js';
import { type NotifiableSessionWatermark, type TrackOverlayItemDto } from '../api/perga-v1.js';
// Track: the unit of work the product is organised around — wire decode, open or closed,
// and the pure predicates several surfaces must agree on.

import { z } from 'zod';

import type { WorkspaceIsolation } from './presentation-types.js';
import { compiledTemplateManifestDtoSchema, overlaysListDtoSchema, terminalDtoSchema, trackAggregateSnapshotDtoSchema, trackDtoSchema, trackWorkspaceOptionsDtoSchema, type TrackDto, type AgentSessionDto, type AskDto, type TaskDto, type TerminalDto, type TrackAggregateSnapshotDto, type TrackWorkspaceOptionsDto } from '../api/perga-v1.js';
import type { ApiFailure,ApiOperation } from '../api/types.js';
import {
activityStateOf,type ActivityItem,type ActivityState,type AttentionKind,type CardActivity,
} from './activity.js';
import { visibleAreas,type Area } from './area.js';
import { normalizeAskQuestions } from './ask.js';
import {
classifyFailure,NotSentError,refusedText,writeFailureOf,
type FailureTable,
type WriteClass,type WriteFailure,type WriteText
} from './failure-class.js';

/** Live activity projection for a track; neutral values mean idle/quiet. */
export type TrackActivity = Readonly<{
  notifiableSessions: readonly NotifiableSessionWatermark[];
  working: boolean;
  attention: AttentionKind;
  /** Latest finite server timestamp from the overlay/snapshot; used for recent-activity ordering. */
  recentAt: number | null;
  /** Open asks requiring user input on this track. */
  attentionItems: readonly ActivityItem[];
  /** Per-session activity verdicts, keyed by session id. Read through `cardActivityOf`. */
  cards: Readonly<Record<string, CardActivity>>;
}>;

/* Nested containers are frozen too: `no-module-runtime-state` only credits a fully frozen literal,
 * and a `new Map()` here would be rejected — hence `cards` is a `Record`. */
export const NEUTRAL_ACTIVITY: TrackActivity = Object.freeze({
  notifiableSessions: Object.freeze([]),
  working: false,
  attention: 'none',
  recentAt: null,
  attentionItems: Object.freeze([]),
  cards: Object.freeze({}),
});

export type Track = Readonly<{
  id: string;
  areaId: string;
  title: string;
  sort: number;
  /** The user's checkout (`workspace.path`). */
  cwd: string;
  /** Where the track's agents run and write: the track worktree when there is one, else `cwd`. */
  agentCwd: string;
  /** Explicit server isolation; never inferred from a worktree path. */
  isolation: WorkspaceIsolation;
  pinnedAt: number | null;
  /** Unix-ms time the track was closed; `null` while it is open. */
  closedAt: number | null;
  createdAt: number;
  updatedAt: number;
  originTrackId?: string;
  /** Historical snapshots may omit presentation metadata; toTrack supplies neutral defaults. */
  templateId?: TrackDto['templateId'];
  templateTitle?: TrackDto['templateTitle'];
  templateInputs?: TrackDto['templateInputs'];
  templateHeaderView?: TrackDto['templateHeaderView'];
}> & TrackActivity;

/** Workspace interpretation belongs to presentation, never to the HTTP carrier. */
export function workspaceForTrack(dto: TrackDto): { isolation: WorkspaceIsolation; agentCwd: string } {
  const spec = typeof dto.workspaceSpec === 'object' && dto.workspaceSpec !== null
    ? dto.workspaceSpec as Record<string, unknown> : {};
  const worktreePath = [spec.mergedCwd, spec.worktreePath, spec.workspacePath]
    .find((path): path is string => typeof path === 'string' && path !== '');
  const isWorktree = dto.workspaceDriver === 'vcs-workspace'
    || dto.workspaceDriver === 'vcs' || dto.workspaceDriver === 'git-worktree'
    || dto.workspaceDriver === 'jj' || dto.workspaceDriver === 'worktree';
  return {
    isolation: isWorktree ? 'worktree' : dto.workspaceDriver === 'direct' || dto.workspaceDriver === 'none' ? 'none' : 'managed',
    agentCwd: isWorktree ? worktreePath ?? dto.cwd : dto.cwd,
  };
}

export function toTrack(wire: TrackDto, activity: TrackActivity = NEUTRAL_ACTIVITY): Track {
  const workspace = workspaceForTrack(wire);
  return {
    id: wire.id,
    areaId: wire.areaId,
    title: wire.title,
    sort: wire.sortOrder,
    cwd: wire.cwd,
    agentCwd: workspace.agentCwd,
    isolation: workspace.isolation,
    pinnedAt: wire.pinnedAtMs ?? null,
    closedAt: wire.closedAtMs ?? null,
    createdAt: wire.createdAtMs,
    updatedAt: wire.updatedAtMs,
    originTrackId: wire.originTrackId ?? undefined,
    templateId: wire.templateId ?? null,
    templateTitle: wire.templateTitle ?? null,
    templateInputs: wire.templateInputs ?? {},
    templateHeaderView: wire.templateHeaderView ?? null,
    ...activity,
  };
}

function finiteOrNull(value: number): number | null {
  return Number.isFinite(value) ? value : null;
}

const WORKING_SESSION_STATUSES: readonly string[] = Object.freeze(['queued', 'running', 'stopping', 'compacting']);

function isWorkingSessionStatus(status: string): boolean {
  return WORKING_SESSION_STATUSES.includes(status);
}

/** Shared rule for both Track-level and per-session red indicators. */
function isSessionBlockingTrack(session: AgentSessionDto, tasks: readonly TaskDto[]): boolean {
  if (session.status !== 'unknown') return false;
  const owner = tasks.find(task => task.sessionId === session.id);
  return owner === undefined || owner.status === 'running' || owner.status === 'verifying';
}

/** Direct projection of one `/api/v1/overlays` item into `TrackActivity`. */
export function trackActivityFromOverlay(item: TrackOverlayItemDto): TrackActivity {
  const attention: AttentionKind = item.hasBlockedSession || item.workspaceFailed
    ? 'failed'
    : item.openAskCount + item.pendingApprovalCount > 0
      ? 'input'
      : 'none';
  return {
    notifiableSessions: item.notifiableSessions,
    working: item.isWorking,
    attention,
    recentAt: finiteOrNull(item.updatedAtMs),
    attentionItems: Object.freeze([]),
    cards: Object.freeze({}),
  };
}

/** Bulk projection of `/api/v1/overlays` items keyed by track id. */
export function trackActivitiesFromOverlays(items: readonly TrackOverlayItemDto[]): ReadonlyMap<string, TrackActivity> {
  return new Map(items.map(item => [item.trackId, trackActivityFromOverlay(item)] as const));
}

/** Effective recency for a track row. Malformed fixtures fail closed onto the remaining finite evidence. */
export function trackRecentAt(track: Track): number {
  const rowTime = finiteOrNull(track.updatedAt) ?? finiteOrNull(track.createdAt) ?? 0;
  const overlayTime = track.recentAt === null ? null : finiteOrNull(track.recentAt);
  return overlayTime === null ? rowTime : Math.max(rowTime, overlayTime);
}

function bytewiseCompare(left: string, right: string): number {
  if (left < right) return -1;
  if (left > right) return 1;
  return 0;
}

/** Area-only display order. The source array and its Track objects remain untouched. */
export function sortAreaTracksByRecent(tracks: readonly Track[]): Track[] {
  return [...tracks].sort((left, right) => {
    const recency = trackRecentAt(right) - trackRecentAt(left);
    if (recency !== 0) return recency;
    const leftSort = Number.isFinite(left.sort) ? left.sort : Number.POSITIVE_INFINITY;
    const rightSort = Number.isFinite(right.sort) ? right.sort : Number.POSITIVE_INFINITY;
    return leftSort - rightSort || bytewiseCompare(left.id, right.id);
  });
}

/** Stable partition: personal Area pins precede the owner's existing recency order. */
export function areaPinnedTracks(tracks: readonly Track[], isPinned: (track: Track) => boolean): Track[] {
  return [...tracks.filter(isPinned), ...tracks.filter((track) => !isPinned(track))];
}

/**
 * The desktop rail's Area rows before the limit: an open track, a closed one that is unread or
 * open in the view, and every track when the Area shows closed ones. Order is kept. It runs
 * before `limitAreaTracks`, so `Show N more` never counts a hidden closed track.
 */
export function railAreaTracks(
  sorted: readonly Track[],
  activeTrackId: string | null,
  isUnread: (track: Track) => boolean,
  showClosed: boolean,
): Track[] {
  if (showClosed) return [...sorted];
  return sorted.filter((track) => !isClosed(track) || isUnread(track) || track.id === activeTrackId);
}

/** How many of an expanded Area's most recent Tracks the desktop rail shows before `Show N more`. */
export const AREA_TRACK_LIMIT = 5;

export type LimitedAreaTracks = Readonly<{ rows: readonly Track[]; hiddenCount: number }>;

/**
 * The collapsed projection of an Area's already-sorted, already-visible Tracks:
 * the first `limit`, plus the open Track at its own position when it sorts past
 * them, so the current row never disappears. `hiddenCount` is what the rows
 * leave out, so it never counts the open Track. The input is not modified.
 */
export function limitAreaTracks(
  sorted: readonly Track[],
  limit: number,
  activeTrackId: string | null,
): LimitedAreaTracks {
  const rows = sorted.filter((track, index) => index < limit || track.id === activeTrackId);
  return { rows, hiddenCount: sorted.length - rows.length };
}

/**
 * Matches the kernel's `str::trim().is_empty()`: Rust whitespace is Unicode `White_Space`, which
 * JS `trim()` is not (`U+0085`). Answers only "is this blank" — the value itself is never trimmed.
 */
export function isBlankForKernel(text: string): boolean {
  return /^\p{White_Space}*$/u.test(text);
}

export type NewTrackBody = Readonly<CreateTrackRequest>;

/** A selectable compiled Host template, projected for the new Track form. */
export type TrackTemplate = Readonly<{
  id: string; title: string; description: string | null;
  availability: Readonly<{ status: 'ready' }>;
  input_schema?: unknown;
  tasks: readonly Readonly<{ key: string; goal: string }>[];
}>;

export function trackTemplatesOperation(): ApiOperation<TrackTemplate[]> {
  return { method: 'GET', path: '/api/v1/templates',
    responseSchema: z.array(compiledTemplateManifestDtoSchema).transform(manifests => manifests.map(manifest => ({
      id: manifest.id, title: manifest.title, description: manifest.description, availability: { status: 'ready' as const },
      ...(manifest.inputsSchema != null && (typeof manifest.inputsSchema !== 'object' || Array.isArray(manifest.inputsSchema) || Object.keys(manifest.inputsSchema).length > 0)
        ? { input_schema: manifest.inputsSchema } : {}), tasks: [],
    }))),
  };
}

/**
 * A keyed create's failure classes: also `stuck`, a create the kernel stopped part way and never drives again
 * (`operation_stuck`, #2175). What it made may exist, and a retry under its key only replays the same answer, so it is
 * final for the key like a refusal; its sentence sends the reader to look before making another.
 */
export type KeyedCreateClass = WriteFailure | 'stuck';

/** A keyed create's fixed sentences: a refusal without a reason, an unknown outcome, and a create that stopped part way. */
export type KeyedCreateText = WriteText & Readonly<{ stuck: string }>;

/**
 * One failed keyed create as its table reads it, with its sentence: a write that was not sent is `refused`; a refusal
 * says the server's reason (or `text.refused`), and `unknown` and `stuck` their fixed sentence. No transport text shows.
 */
export function readKeyedCreateFailure(
  error: unknown, table: FailureTable<KeyedCreateClass>, text: KeyedCreateText,
): Readonly<{ is: KeyedCreateClass; text: string }> {
  const failure = writeFailureOf(error);
  const is = failure instanceof NotSentError ? 'refused' : classifyFailure(failure, table);
  return { is, text: is === 'refused' ? refusedText(failure, text.refused) : text[is] };
}

export type TrackPatchBody = Readonly<Pick<PatchTrackRequest, 'title' | 'closed' | 'pinned' | 'sortOrder'>>;

function askDtoToActivityItem(ask: AskDto): ActivityItem {
  const questions = normalizeAskQuestions(ask.questions);
  return {
    source: 'ask',
    key: `ask:${ask.id}`,
    text: questions.map((question) => question.title).join('\n') || 'Question waiting for your answer',
    atMs: ask.createdAtMs,
    askId: ask.id,
    questions: [...questions],
    delivery: ask.delivery,
  };
}

/** Direct activity projection from a Track aggregate snapshot. */
export function trackActivityFromSnapshot(snapshot: TrackAggregateSnapshotDto): TrackActivity {
  const openAsks = snapshot.openAsks.filter((ask) => ask.status === 'open');
  const openAskItems = openAsks.map(askDtoToActivityItem);
  const closed = snapshot.track.closedAtMs != null;
  const sessions = snapshot.sessions.filter(s => s.archivedAtMs == null);
  const blockedSessionIds = new Set(
    sessions.filter(s => isSessionBlockingTrack(s, snapshot.tasks)).map(s => s.id),
  );
  const failed = blockedSessionIds.size > 0
    || snapshot.workspacePreparation === 'failed'
    || snapshot.workspacePreparation === 'unknown';
  const working = !closed && (
    snapshot.tasks.some(t => t.status === 'running' || t.status === 'verifying')
    || sessions.some(s => isWorkingSessionStatus(s.status))
    || snapshot.workspacePreparation === 'pending'
  );
  const attention: AttentionKind = closed
    ? 'none'
    : failed
      ? 'failed'
      : openAskItems.length > 0 || (snapshot.pendingApprovals?.length ?? 0) > 0
        ? 'input'
        : 'none';
  const leadSessionId = snapshot.sessions.find(s => s.isLead)?.id;
  const inputIds = new Set<string>([
    ...(snapshot.pendingApprovals ?? []).map(a => a.sessionId),
    ...openAsks.flatMap(a => {
      const target = a.sessionId ?? leadSessionId;
      return target === undefined ? [] : [target];
    }),
  ]);
  const cards: Record<string, CardActivity> = {};
  if (!closed) {
    for (const session of sessions) {
      if (blockedSessionIds.has(session.id)) cards[session.id] = 'failed';
      else if (inputIds.has(session.id)) cards[session.id] = 'input';
      else if (isWorkingSessionStatus(session.status)) cards[session.id] = 'working';
    }
  }
  return {
    notifiableSessions: sessions
      .filter((s): s is AgentSessionDto & { lastNotifiableTurnAtMs: number } => !snapshot.tasks.some(task => task.sessionId === s.id) && s.lastNotifiableTurnAtMs != null)
      .map(s => ({ sessionId: s.id, lastNotifiableTurnAtMs: s.lastNotifiableTurnAtMs })),
    working,
    attention,
    recentAt: finiteOrNull(snapshot.track.updatedAtMs),
    attentionItems: closed ? Object.freeze([]) : openAskItems,
    cards,
  };
}

export function tracksInAreaOperation(areaId: string, includeClosed = true): ApiOperation<TrackDto[]> {
  return {
    method: 'GET',
    path: `/api/v1/areas/${encodeURIComponent(areaId)}/tracks?includeClosed=${includeClosed ? 'true' : 'false'}`,
    responseSchema: z.array(trackDtoSchema),
  };
}

export function trackDetailOperation(trackId: string): ApiOperation<TrackAggregateSnapshotDto> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}`,
    responseSchema: trackAggregateSnapshotDtoSchema,
  };
}

export function retryTrackWorkspaceOperation(trackId: string): ApiOperation<unknown> {
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/workspace/retry`,
    responseSchema: z.unknown(),
  };
}

export type TrackWorkspaceOptions = TrackWorkspaceOptionsDto;

export function trackWorkspaceOptionsOperation(
  areaId: string,
  cwd?: string,
  templateId?: string,
  targetId?: string,
): ApiOperation<TrackWorkspaceOptions> {
  const query = [
    ...(cwd === undefined ? [] : [`cwd=${encodeURIComponent(cwd)}`]),
    ...(templateId ? [`templateId=${encodeURIComponent(templateId)}`] : []),
    ...(targetId ? [`targetId=${encodeURIComponent(targetId)}`] : []),
  ].join('&');
  return {
    method: 'GET',
    path: `/api/v1/areas/${encodeURIComponent(areaId)}/workspace-options${query === '' ? '' : `?${query}`}`,
    responseSchema: trackWorkspaceOptionsDtoSchema,
  };
}

/**
 * `POST /api/v1/tracks`, keyed per draft on every create.
 */
export function createTrackOperation(body: NewTrackBody, idempotencyKey: string): ApiOperation<TrackDto> {
  return {
    method: 'POST',
    path: '/api/v1/tracks',
    body: Object.fromEntries(Object.entries({
      areaId: body.areaId, title: body.title, cwd: body.cwd, provider: body.provider,
      model: body.model, reasoningEffort: body.reasoningEffort, permissionMode: body.permissionMode,
      workspaceDriver: body.workspaceDriver, workspaceOptionId: body.workspaceOptionId,
      executionTargetId: body.executionTargetId, baseRevision: body.baseRevision,
      templateId: body.templateId, templateInputs: body.templateInputs,
      enabledPlugins: body.enabledPlugins, sortOrder: body.sortOrder,
    } satisfies CreateTrackRequest).filter(([, value]) => value !== undefined)),
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: trackAggregateSnapshotDtoSchema.transform((snapshot) => snapshot.track),
  };
}

/**
 * What one failed `POST /api/v1/tracks` means for its draft and key.
 * - `exhausted`: the key can never mint again (spent, its track deleted, or refused as invalid);
 *   nothing was minted under it for this request, so only a fresh key goes anywhere.
 * - `key-reused`: the key is bound to another create (or one the server can no longer compare), so
 *   no retry under it can succeed; a new track is an explicit choice, never automatic. Final.
 * - `rejected`: refused before anything was minted; the key is unspent.
 * - `unconfirmed`: the create may have committed (a lost answer, a 5xx, a timeout, a concurrent
 *   create under the same key), so the key and the original request are kept for a retry.
 */
export type TrackCreateFailure = 'exhausted' | 'key-reused' | 'rejected' | 'unconfirmed';

/** First match wins: the codes, then 408/409 (which may follow a commit), then the other 4xx. */
export const TRACK_CREATE_FAILURES: FailureTable<TrackCreateFailure> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ code: 'idempotency_key_exhausted', is: 'exhausted' as const }),
    Object.freeze({ code: 'idempotency_key_invalid', is: 'exhausted' as const }),
    Object.freeze({ code: 'idempotency_key_reused', is: 'key-reused' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'precondition_failed', is: 'rejected' as const }),
    Object.freeze({ status: Object.freeze([408, 409]), is: 'unconfirmed' as const }),
    Object.freeze({ status: Object.freeze({ from: 400, to: 499 }), is: 'rejected' as const }),
  ]),
  unauthorized: 'unconfirmed',
  otherwise: 'unconfirmed',
});

/** A refused create, and one whose outcome is unknown: the request and its key are kept, so a retry cannot mint a second track. */
export const TRACK_CREATE_TEXT: WriteText = Object.freeze({
  refused: 'The track was not created.', unknown: 'The track creation is unconfirmed. Try again to check the same track.',
});

/** The server identifies this post-commit conflict with a stable code. Keep the original create for retry. */
export function trackCreateWorkspaceError(failure: ApiFailure | null): string | null {
  if (failure?.kind !== 'http' || failure.status !== 409 || failure.code !== 'attached_repo_diverged') return null;
  return 'The attached repository’s Git branch has diverged from its upstream. The Track was saved, '
    + 'but its working directory could not be created. Reconcile the branch and try again here.';
}

export function updateTrackOperation(trackId: string, body: TrackPatchBody): ApiOperation<TrackDto> {
  return {
    method: 'PATCH',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}`,
    body: Object.fromEntries(Object.entries({ title: body.title, closed: body.closed, pinned: body.pinned, sortOrder: body.sortOrder } satisfies PatchTrackRequest).filter(([, value]) => value !== undefined)),
    responseSchema: trackDtoSchema,
  };
}

/**
 * What a failed `PATCH /api/v1/tracks/{id}` (rename, pin, close, reopen) says. It sets a value, so a retry is safe: 400, 403,
 * 404, 409 (a child track reopened under a closed parent), 413 and 422 are answered before anything is stored; anything
 * else may have stored it.
 */
export const TRACK_PATCH_FAILURES: FailureTable<WriteClass> = Object.freeze({
  rules: Object.freeze([Object.freeze({ status: Object.freeze([400, 403, 404, 409, 413, 422]), is: 'refused' as const })]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const TRACK_PATCH_TEXT = Object.freeze({
  rename: Object.freeze({ refused: 'The track was not renamed.', unknown: 'The rename is unconfirmed.' }),
  pin: Object.freeze({ refused: 'The pin was not changed.', unknown: 'The pin change is unconfirmed.' }),
  close: Object.freeze({ refused: 'The track was not closed.', unknown: 'Closing the track is unconfirmed.' }),
  reopen: Object.freeze({ refused: 'The track was not reopened.', unknown: 'Reopening the track is unconfirmed.' }),
}) satisfies Readonly<Record<string, WriteText>>;

export function deleteTrackOperation(trackId: string): ApiOperation<undefined> {
  return { method: 'DELETE', path: `/api/v1/tracks/${encodeURIComponent(trackId)}`, responseSchema: z.undefined() };
}

export type NewTerminalBody = Readonly<Omit<OpenTerminalRequest, 'idempotencyKey'>>;

export function listTrackTerminalsOperation(trackId: string): ApiOperation<TerminalDto[]> {
  return { method: 'GET', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals`, responseSchema: z.array(terminalDtoSchema) };
}

/** One held intent uses the same body and key across retries. */
export function createTerminalOperation(trackId: string, body: NewTerminalBody, idempotencyKey: string): ApiOperation<TerminalDto> {
  return { method: 'POST', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals`,
    body: Object.fromEntries(Object.entries({ title: body.title, cwd: body.cwd, program: body.program, args: body.args, env: body.env, cols: body.cols, rows: body.rows, idempotencyKey } satisfies OpenTerminalRequest).filter(([, value]) => value !== undefined)), headers: { 'Idempotency-Key': idempotencyKey }, responseSchema: terminalDtoSchema };
}

export function deleteTerminalOperation(trackId: string, termId: string): ApiOperation<undefined> {
  return {
    method: 'DELETE',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals/${encodeURIComponent(termId)}`,
    responseSchema: z.undefined(),
  };
}

/**
 * What a failed card create says: 400, 403, 404 and 422 refuse the body, the track or the plugin before anything is
 * made; a key bound to another body (`idempotency_key_reused`) can never make this one; `conflict` is only a create the
 * kernel refused before its transaction committed (a Pending-phase failure, which a retry under the key replays);
 * `operation_failed` is a create that failed for good, which a retry under the key replays too; and `operation_stuck` is
 * a create that stopped part way, whose card may exist but whose key only replays that. All are final, so the next
 * intent mints a new key. Anything else may have made the card: the key is kept for Try again.
 */
export const CARD_CREATE_FAILURES: FailureTable<KeyedCreateClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ code: 'idempotency_key_reused', is: 'refused' as const }),
    Object.freeze({ code: 'operation_failed', is: 'refused' as const }),
    Object.freeze({ code: 'operation_stuck', is: 'stuck' as const }),
    Object.freeze({ status: Object.freeze([409]), code: 'conflict', is: 'refused' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export function cardCreateText(label: string): KeyedCreateText {
  return {
    refused: `The ${label} card was not created.`,
    unknown: `Creating the ${label} card is unconfirmed.`,
    stuck: `Creating the ${label} card stopped part way, so the card may exist. Check the track before creating another.`,
  };
}

export function trackOverlaysOperation(): ApiOperation<TrackOverlayItemDto[]> {
  return {
    method: 'GET',
    path: '/api/v1/overlays?entity_kind=track&includeClosed=true',
    responseSchema: overlaysListDtoSchema.transform((dto) => dto.tracks),
  };
}

/* The three activity predicates read only the kernel's `activity` overlay — no track-state OR — so they cannot disagree with it. */

/** The kernel says something dispatched is still running. */
export function isWorking(track: Pick<Track, 'working'>): boolean {
  return track.working;
}

/** The kernel says a person has to give input somewhere on this track. */
export function needsUserAttention(track: Track): boolean {
  return track.attention === 'input';
}

/** The kernel says something on this track is broken and needs repair. */
export function hasFailed(track: Track): boolean {
  return track.attention === 'failed';
}

/** The one indicator state for a track on every surface; `unread` is the reader's receipt, the rest is the overlay's. */
export function trackActivityState(track: Pick<Track, 'working' | 'attention'>, unread: boolean): ActivityState {
  return activityStateOf({ working: isWorking(track), attention: track.attention, unread });
}

/** A closed track schedules no new work; only the user reopens it. */
export function isClosed(track: Pick<Track, 'closedAt'>): boolean {
  return track.closedAt !== null;
}

/** Hosted by an area the person may see — the second layer of defence behind `visibleAreas`. */
export function userVisibleTracks(tracks: readonly Track[], areas: readonly Area[]): Track[] {
  const userAreaIds = new Set(visibleAreas(areas).map((area) => area.id));
  return tracks.filter((track) => userAreaIds.has(track.areaId));
}

export const UNTITLED_TRACK_LABEL = 'Untitled track';

/** One display fallback for tracks created without a title. */
export function trackDisplayTitle(title: string): string {
  return title.trim() || UNTITLED_TRACK_LABEL;
}

function startOfDay(day: Date): number {
  const start = new Date(day);
  start.setHours(0, 0, 0, 0);
  return start.getTime();
}

function endOfDay(day: Date): number {
  const end = new Date(day);
  end.setHours(23, 59, 59, 999);
  return end.getTime();
}

/**
 * Every track whose `[createdAt, closedAt ?? nowMs]` interval overlaps the local day owning `day`;
 * endpoints inclusive, sorted by `createdAt` then id.
 */
export function activeTracksOn(tracks: readonly Track[], day: Date, nowMs: number): Track[] {
  const dayStart = startOfDay(day);
  const dayEnd = endOfDay(day);
  const matched = tracks.filter((track) => {
    const end = track.closedAt ?? nowMs;
    return track.createdAt <= dayEnd && end >= dayStart;
  });
  return matched.sort((left, right) => (left.createdAt !== right.createdAt
    ? left.createdAt - right.createdAt
    : left.id < right.id ? -1 : left.id > right.id ? 1 : 0));
}
