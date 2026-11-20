import { notifiableSessionWatermarkSchema, type NotifiableSessionWatermark } from '../api/perga-v1.js';
import type { TerminalPresentation } from './terminal-presentation.js';
// Track: the unit of work the product is organised around — wire decode, open or closed,
// and the pure predicates several surfaces must agree on.

import { z } from 'zod';

import {
  askActionSchema,
  askQuestionSchema,
  cardSchema,
  compiledTemplateManifestDtoSchema,
  overlaySchema,
  overlaysListDtoSchema,
  terminalDtoSchema,
  toWireAgentProvider,
  trackAggregateSnapshotDtoSchema,
  trackDtoSchema,
  trackWorkspaceSchema,
  trackWorkspaceOptionsDtoSchema,
  type AskDto,
  type TerminalDto,
  type TrackAggregateSnapshotDto,
  type TrackWorkspaceOptionsDto,
  type TrackDto,
} from '../api/schemas.js';
import type { AgentProvider, WorkspaceIsolation } from '../api/generated/wire.js';
import type { ApiFailure, ApiOperation } from '../api/types.js';
import {
  activityStateOf, type ActivityItem, type ActivityState, type AttentionKind, type CardActivity,
} from './activity.js';
import { visibleAreas, type Area } from './area.js';
import { normalizeAskQuestions } from './ask.js';
import {
  casAttempts, classifyFailure, NotSentError, refusedText, writeFailureOf,
  type FailureTable, type Landed, type WriteClass, type WriteFailure, type WriteText,
} from './failure-class.js';

/**
 * `cwd` and the `*_at` columns may be absent from the OpenAPI `required` set; the decoder supplies
 * the DB defaults. `closed_at` is null while the track is open.
 */
export const trackWireSchema = z.object({
  id: z.string(),
  area_id: z.string(),
  title: z.string(),
  sort: z.number(),
  cwd: z.string().default(''),
  workspace: trackWorkspaceSchema,
  pinned_at: z.number().nullable().default(null),
  closed_at: z.number().nullable().default(null),
  /** Unix-ms time a person granted Area admin; `null` otherwise. Absent on historical events, which read as not admin. */
  admin_granted_at: z.number().nullable().default(null),
  created_at: z.number(),
  updated_at: z.number(),
  templateId: trackDtoSchema.shape.templateId,
  templateTitle: trackDtoSchema.shape.templateTitle,
  templateInputs: trackDtoSchema.shape.templateInputs.optional(),
  templateHeaderView: trackDtoSchema.shape.templateHeaderView,
});
export type TrackWire = z.infer<typeof trackWireSchema>;

/** Plugin-written activity on top of the kernel row; the neutral values mean "nothing posted", not "unknown". */
export type TrackActivity = Readonly<{
  notifiableSessions: readonly NotifiableSessionWatermark[];
  progress: number;
  eta: string;
  now: string;
  /** From the kernel `activity` overlay: something dispatched is still running. */
  working: boolean;
  /** Same overlay: whether the Track needs input or has a failure. */
  attention: AttentionKind;
  /** Same overlay: high-water mark of completion-class evidence; the read receipt compares against it. */
  activityAt: number | null;
  /** Latest finite write/evidence time from the kernel activity overlay; used for recent-activity ordering. */
  recentAt: number | null;
  /** Same overlay: questions, failures and turn timeouts with their recorded details. */
  attentionItems: readonly ActivityItem[];
  /** Same overlay: the per-card verdicts, keyed by card id. Read through `cardActivityOf`. */
  cards: Readonly<Record<string, CardActivity>>;
}>;

/* Nested containers are frozen too: `no-module-runtime-state` only credits a fully frozen literal,
 * and a `new Map()` here would be rejected — hence `cards` is a `Record`. */
export const NEUTRAL_ACTIVITY: TrackActivity = Object.freeze({
  notifiableSessions: Object.freeze([]),
  progress: 0, eta: '', now: '',
  working: false, attention: 'none', activityAt: null, recentAt: null,
  attentionItems: Object.freeze([]), cards: Object.freeze({}),
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
  /** Historical snapshots may omit presentation metadata; toTrack supplies neutral defaults. */
  templateId?: TrackWire['templateId'];
  templateTitle?: TrackWire['templateTitle'];
  templateInputs?: TrackWire['templateInputs'];
  templateHeaderView?: TrackWire['templateHeaderView'];
}> & TrackActivity;

export function toTrack(wire: TrackWire, activity: TrackActivity = NEUTRAL_ACTIVITY): Track {
  return {
    id: wire.id,
    areaId: wire.area_id,
    title: wire.title,
    sort: wire.sort,
    cwd: wire.cwd,
    agentCwd: wire.workspace.worktree ?? wire.cwd,
    isolation: wire.workspace.isolation,
    pinnedAt: wire.pinned_at,
    closedAt: wire.closed_at,
    createdAt: wire.created_at,
    updatedAt: wire.updated_at,
    templateId: wire.templateId ?? null,
    templateTitle: wire.templateTitle ?? null,
    templateInputs: wire.templateInputs ?? {},
    templateHeaderView: wire.templateHeaderView ?? null,
    ...activity,
  };
}

export type OverlayWire = z.infer<typeof overlaySchema>;

/**
 * Resolves a report's `neige://plugin/<plugin_id>/<kind>` source to at most one overlay's
 * payload, unvalidated (the renderer decodes it); `undefined` when nothing matches.
 */
export function trackOverlayPayload(
  trackId: string,
  overlays: readonly OverlayWire[],
  source: string,
): unknown {
  const rest = source.startsWith(LIVE_TABLE_SOURCE_PREFIX)
    ? source.slice(LIVE_TABLE_SOURCE_PREFIX.length)
    : null;
  if (rest === null) return undefined;
  const slash = rest.indexOf('/');
  if (slash <= 0 || slash === rest.length - 1) return undefined;
  const pluginId = rest.slice(0, slash);
  const kind = rest.slice(slash + 1);
  // `indexOf` above splits at the FIRST slash, so a three-segment source would
  // otherwise resolve as a kind containing a slash. Overlay kinds never do.
  if (kind.includes('/')) return undefined;
  const match = overlays.find((overlay) => overlay.entity_kind === 'track'
    && overlay.entity_id === trackId
    && overlay.plugin_id === pluginId
    && overlay.kind === kind);
  return match?.payload;
}

/** Scheme prefix of a live table `source`. Mirrors `report_blocks::LIVE_SOURCE_PREFIX`. */
const LIVE_TABLE_SOURCE_PREFIX = 'neige://plugin/';

function payloadField(payload: unknown, key: string): unknown {
  return typeof payload === 'object' && payload !== null
    ? (payload as Record<string, unknown>)[key]
    : undefined;
}

const attentionKindSchema = z.enum(['none', 'input', 'failed']);
const activityItemWireSchema = z.discriminatedUnion('source', [
  z.object({
    source: z.literal('ask'),
    key: z.string(),
    text: z.string(),
    at_ms: z.number(),
    ask_id: z.union([z.number(), z.string()]),
    questions: z.array(askQuestionSchema),
    delivery: z.enum(['wake', 'hold']),
    action: askActionSchema.optional(),
  }),
]);
const activityCardWireSchema = z.object({
  card_id: z.string(),
  state: z.enum(['working', 'input', 'failed']),
});

/** Mirrors `calm_truth::validation::KERNEL_OVERLAY_PLUGIN_ID`. */
const KERNEL_OVERLAY_PLUGIN_ID = 'kernel';
const activityOverlayWireSchema = z.object({
  schemaVersion: z.literal(4),
  working: z.boolean(),
  attention: attentionKindSchema,
  activity_at_ms: z.number().nullable(),
  notifiableSessions: z.array(notifiableSessionWatermarkSchema).default([]),
  items: z.array(z.unknown()),
  cards: z.array(z.unknown()),
});

function activityOverlayFields(payload: unknown): Partial<TrackActivity> | null {
  const parsed = activityOverlayWireSchema.safeParse(payload);
  if (!parsed.success) return null;
  const attentionItems: ActivityItem[] = [];
  for (const row of parsed.data.items) {
    const item = activityItemWireSchema.safeParse(row);
    if (!item.success) continue;
    const base = { key: item.data.key, text: item.data.text, atMs: item.data.at_ms };
    attentionItems.push({ ...base, source: 'ask', askId: item.data.ask_id, questions: item.data.questions,
      delivery: item.data.delivery, ...(item.data.action === undefined ? {} : { action: item.data.action }) });
  }
  const cards: Record<string, CardActivity> = {};
  for (const row of parsed.data.cards) {
    const card = activityCardWireSchema.safeParse(row);
    if (card.success) cards[card.data.card_id] = card.data.state;
  }
  return {
    notifiableSessions: parsed.data.notifiableSessions,
    working: parsed.data.working,
    attention: parsed.data.attention,
    activityAt: parsed.data.activity_at_ms,
    attentionItems,
    cards,
  };
}

function newerFinite(current: number | null, candidate: number): number | null {
  if (!Number.isFinite(candidate)) return current;
  return current === null || candidate > current ? candidate : current;
}

/**
 * Folds a track's overlays into its activity fields; junk payloads are ignored, not rejected.
 * Only the kernel-written `activity` row is the verdict — any plugin may write a row of any kind under its own id.
 */
export function trackActivityFrom(trackId: string, overlays: readonly OverlayWire[]): TrackActivity {
  let activity = NEUTRAL_ACTIVITY;
  for (const overlay of overlays) {
    if (overlay.entity_kind !== 'track' || overlay.entity_id !== trackId) continue;
    const value = payloadField(overlay.payload, 'value');
    const text = payloadField(overlay.payload, 'text');
    if (overlay.kind === 'progress' && typeof value === 'number') activity = { ...activity, progress: value };
    else if (overlay.kind === 'eta' && typeof text === 'string') activity = { ...activity, eta: text };
    else if (overlay.kind === 'now' && typeof text === 'string') activity = { ...activity, now: text };
    else if (overlay.kind === 'activity' && overlay.plugin_id === KERNEL_OVERLAY_PLUGIN_ID) {
      const recentAt = newerFinite(activity.recentAt, overlay.updated_at);
      const fields = activityOverlayFields(overlay.payload);
      activity = fields === null ? { ...activity, recentAt } : {
        ...activity,
        ...fields,
        recentAt: fields.activityAt === null || fields.activityAt === undefined
          ? recentAt
          : newerFinite(recentAt, fields.activityAt),
      };
    }
  }
  return activity;
}

/** Bulk projection keeps each entity's original overlay order and the single-track fold's policy. */
export function trackActivitiesFrom(overlays: readonly OverlayWire[]): ReadonlyMap<string, TrackActivity> {
  const byTrack = new Map<string, OverlayWire[]>();
  for (const overlay of overlays) {
    if (overlay.entity_kind !== 'track') continue;
    const id = overlay.entity_id;
    const rows = byTrack.get(id);
    if (rows === undefined) byTrack.set(id, [overlay]);
    else rows.push(overlay);
  }
  return new Map([...byTrack].map(([id, rows]) => [id, trackActivityFrom(id, rows)]));
}

function finiteOrNull(value: number): number | null {
  return Number.isFinite(value) ? value : null;
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

/** HTTP readers normalize an omitted title to null; event readers retain the optional wire title. */
export const cardWireSchema = z.object({
  ...cardSchema.shape,
  title: z.string().nullable().default(null),
});
export type CardWire = z.infer<typeof cardWireSchema>;

/** `{ fg, bg }` RGB the kernel stamps onto a spawning daemon's argv. */
export type ThemeRgb = Readonly<{ fg: readonly [number, number, number]; bg: readonly [number, number, number] }>;

/**
 * Matches the kernel's `str::trim().is_empty()`: Rust whitespace is Unicode `White_Space`, which
 * JS `trim()` is not (`U+0085`). Answers only "is this blank" — the value itself is never trimmed.
 */
export function isBlankForKernel(text: string): boolean {
  return /^\p{White_Space}*$/u.test(text);
}

export type NewTrackBody = Readonly<{
  permissionMode?: string;
  admin?: boolean;
  area_id: string;
  /** The Planner's backend, stamped on its card and never changed. Required: the kernel refuses a create without it. */
  planner_provider: AgentProvider;
  /** Planner overrides applied before the first message; omitted follows installation defaults. */
  model?: string;
  reasoning_effort?: string;
  /** Optional; omitted stores the empty string (the kernel has no default name). Present values, including `""`, are stored verbatim. */
  title?: string;
  /** Omitted (or `null`) means a managed workspace the server allocates beneath its workspace root. */
  cwd?: string | null;
  /** Explicit isolation for an attached folder; omitted defaults to `worktree` on the server. */
  isolation?: 'worktree' | 'none';
  workspace_option_id?: string;
  theme: ThemeRgb;
  /**
   * `false` requires `cwd` to already sit under a claimed folder (409 naming the area otherwise);
   * `true` claims it in the same transaction. Omitting `cwd` forces `false`.
   */
  attach_folder?: boolean;
  /** Single-use consent for the exact foreign folder claim returned by a
   * create 409. The kernel revalidates both ids transactionally. */
  allow_cross_area_cwd?: Readonly<{ folder_id: number; area_id: string }>;
  /** The chosen template's key (`template.id` from `GET /api/track-templates`). Blank omits the key entirely: the kernel 400s a whitespace-only id. */
  template_id?: string;
  /** Only accepted when `GET /api/track-templates` returned an `input_schema` for the template; otherwise a 400. */
  template_input?: Readonly<Record<string, unknown>>;
  /** The chosen user recipe's id. Mutually exclusive with `template_id` (400 naming both). Absent, never `''`. */
  recipe_id?: string;
}>;

/** A selectable starting point for a new track. `input_schema` is present exactly when a running trusted plugin is bound and the template takes input. */
export const trackTemplateSchema = z.object({
  id: z.string(),
  title: z.string(),
  /** Author-owned purpose; null means this author did not supply a description. */
  description: z.string().nullable(),
  /** Server-owned prerequisite state, independent of documentation references. */
  availability: z.discriminatedUnion('status', [
    z.object({ status: z.literal('ready') }),
    z.object({ status: z.literal('requires_plugin'), plugin_id: z.string(), plugin_name: z.string() }),
  ]),
  input_schema: z.unknown().optional(),
  /** Required, not `.default([])`: a default would let a broken read render as "pre-sets nothing". */
  tasks: z.array(z.object({ key: z.string(), goal: z.string() })),
});
export type TrackTemplate = z.infer<typeof trackTemplateSchema>;

export function trackTemplatesOperation(): ApiOperation<TrackTemplate[]> {
  return {
    method: 'GET',
    path: '/api/v1/templates',
    responseSchema: z.union([
      z.array(trackTemplateSchema),
      z.array(compiledTemplateManifestDtoSchema).transform((manifests) => manifests.map((manifest) => ({
        id: manifest.id,
        title: manifest.title,
        description: manifest.description,
        availability: { status: 'ready' as const },
        ...(manifest.inputsSchema !== null
          && manifest.inputsSchema !== undefined
          && (typeof manifest.inputsSchema !== 'object' || Array.isArray(manifest.inputsSchema) || Object.keys(manifest.inputsSchema).length > 0)
          ? { input_schema: manifest.inputsSchema }
          : {}),
        tasks: [],
      }))),
    ]),
  };
}

/** A user-defined starting point for a new track — a `track_recipes` row. */
export const trackRecipeSchema = z.object({
  id: z.string(),
  /** Picker label *and* the instantiated report's summary — one field on the
   *  kernel side too, so the editor edits one thing. */
  title: z.string(),
  /** Saved report content and instructions. The planner derives tasks from the actual request. */
  body: z.string(),
  /** Optimistic-lock anchor a `PUT` must echo as `if_revision`. */
  revision: z.number(),
  created_at: z.number(),
  updated_at: z.number(),
});
export type TrackRecipe = z.infer<typeof trackRecipeSchema>;

export function trackRecipesOperation(): ApiOperation<TrackRecipe[]> {
  return { method: 'GET', path: '/api/track-recipes', responseSchema: z.array(trackRecipeSchema) };
}

/** One recipe as stored: what a save whose answer was lost is read back through. */
export function trackRecipeOperation(recipeId: string): ApiOperation<TrackRecipe> {
  return { method: 'GET', path: `/api/track-recipes/${encodeURIComponent(recipeId)}`, responseSchema: trackRecipeSchema };
}

/** `POST /api/track-recipes`, keyed per save intent: a retry under the key answers with the recipe the first attempt saved. */
export function createTrackRecipeOperation(
  body: Readonly<{ title: string; body: string }>,
  idempotencyKey: string,
): ApiOperation<TrackRecipe> {
  return {
    method: 'POST', path: '/api/track-recipes', body, headers: { 'Idempotency-Key': idempotencyKey }, responseSchema: trackRecipeSchema,
  };
}

/**
 * Whole-document replace gated on `revision`. The response is the stored row, which may differ
 * from the bytes sent (fences re-rendered, tombstones dropped): render what comes back.
 */
export function updateTrackRecipeOperation(
  recipeId: string,
  body: Readonly<{ title: string; body: string; if_revision: number }>,
): ApiOperation<TrackRecipe> {
  return {
    method: 'PUT',
    path: `/api/track-recipes/${encodeURIComponent(recipeId)}`,
    body,
    responseSchema: trackRecipeSchema,
  };
}

export function deleteTrackRecipeOperation(recipeId: string): ApiOperation<undefined> {
  return {
    method: 'DELETE',
    path: `/api/track-recipes/${encodeURIComponent(recipeId)}`,
    responseSchema: z.undefined(),
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

/**
 * What a failed `POST /api/track-recipes` says: 400 (an empty title, a body that would not parse, a malformed key), 403
 * (not the user), 413 and 422 are answered before anything is stored; a key bound to another body
 * (`idempotency_key_reused`) or a create that failed for good under its key (`operation_failed`) can never store this
 * one; a create that stopped part way (`operation_stuck`) may have stored it, but its key only replays that. All are
 * final, so the next save mints a new key. Anything else may have made the recipe: the key is kept, and Try again
 * resends the same key and body, which the kernel answers with the recipe the first attempt made (#2131).
 */
export const RECIPE_CREATE_FAILURES: FailureTable<KeyedCreateClass> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ code: 'idempotency_key_reused', is: 'refused' as const }),
    Object.freeze({ code: 'operation_failed', is: 'refused' as const }),
    Object.freeze({ code: 'operation_stuck', is: 'stuck' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 413, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const RECIPE_CREATE_TEXT: KeyedCreateText = Object.freeze({
  refused: 'The recipe was not created.',
  unknown: 'Creating the recipe is unconfirmed. Try again to check the same recipe.',
  stuck: 'Creating the recipe stopped part way, so the recipe may exist. Check your recipes before saving it again.',
});

/**
 * What a failed `PUT /api/track-recipes/{id}` says. A 409 is the `if_revision` CAS lost: `stale`, nothing was stored. 400,
 * 403, 404 (the recipe is gone), 413 and 422 are refusals answered before anything is stored. Anything else may have
 * stored the save; saving again is safe, because it is gated on the same revision.
 */
export const RECIPE_SAVE_FAILURES: FailureTable<WriteFailure | 'stale'> = Object.freeze({
  rules: Object.freeze([
    Object.freeze({ status: Object.freeze([409]), is: 'stale' as const }),
    Object.freeze({ status: Object.freeze([400, 403, 404, 413, 422]), is: 'refused' as const }),
  ]),
  unauthorized: 'refused',
  otherwise: 'unknown',
});

export const RECIPE_SAVE_TEXT: WriteText = Object.freeze({
  refused: 'The recipe was not saved.', unknown: 'Saving the recipe is unconfirmed. Save again to check.',
});

/** One recipe writer's saves: a save retried after an unknown outcome and answered 409 is read back first. */
export function recipeSaveAttempts() {
  return casAttempts(RECIPE_SAVE_FAILURES);
}

/**
 * Whether the `stored` recipe holds exactly what one save sent, which is how a save whose answer was lost is known to have
 * landed. Compared as sent: a body the server rewrote on the way in (a fence re-rendered) does not match, and stays stale.
 */
export function recipeSaveLanded(stored: TrackRecipe, sent: Readonly<{ title: string; body: string }>): Landed<TrackRecipe> {
  return stored.title === sent.title && stored.body === sent.body ? { stored } : null;
}

export type TrackPatchBody = Readonly<{
  admin?: boolean;
  title?: string;
  sort?: number;
  pinned_at?: number | null;
  pinned?: boolean;
  enabled_plugins?: readonly string[];
  lead_session_id?: string;
  /** `true` closes the track, `false` reopens it; the server stamps the time. */
  closed?: boolean;
}>;

export function trackDtoToWire(dto: TrackDto): TrackWire {
  const spec = typeof dto.workspaceSpec === 'object' && dto.workspaceSpec !== null
    ? (dto.workspaceSpec as Record<string, unknown>)
    : {};
  const worktreePath = typeof spec.mergedCwd === 'string' && spec.mergedCwd !== ''
    ? spec.mergedCwd
    : typeof spec.worktreePath === 'string' && spec.worktreePath !== ''
      ? spec.worktreePath
      : typeof spec.workspacePath === 'string' && spec.workspacePath !== ''
        ? spec.workspacePath
        : undefined;
  const logicalRoot = typeof spec.logicalRoot === 'string' && spec.logicalRoot !== ''
    ? spec.logicalRoot
    : dto.cwd;
  const isWorktree = dto.workspaceDriver === 'vcs-workspace'
    || dto.workspaceDriver === 'vcs'
    || dto.workspaceDriver === 'git-worktree'
    || dto.workspaceDriver === 'jj'
    || dto.workspaceDriver === 'worktree';
  const isAttachedNone = (dto.workspaceDriver === 'direct' || dto.workspaceDriver === 'none') && dto.cwd !== '';
  const workspace = isWorktree
    ? {
      isolation: 'worktree' as const,
      kind: 'attached' as const,
      path: logicalRoot,
      frozen_at: null,
      ...(worktreePath !== undefined ? { worktree: worktreePath } : {}),
    }
    : isAttachedNone
      ? { isolation: 'none' as const, kind: 'attached' as const, path: dto.cwd, frozen_at: null }
      : { isolation: 'managed' as const, kind: 'managed' as const, path: dto.cwd, frozen_at: null };
  return {
    id: dto.id,
    area_id: dto.areaId,
    title: dto.title,
    sort: dto.sortOrder,
    cwd: dto.cwd,
    workspace,
    pinned_at: dto.pinnedAtMs ?? null,
    closed_at: dto.closedAtMs ?? null,
    admin_granted_at: null,
    created_at: dto.createdAtMs,
    updated_at: dto.updatedAtMs,
    templateId: dto.templateId ?? null,
    templateTitle: dto.templateTitle ?? null,
    templateInputs: dto.templateInputs ?? {},
    templateHeaderView: dto.templateHeaderView ?? null,
  };
}

const trackWireOrDtoSchema: z.ZodType<TrackWire> = z.union([
  trackDtoSchema.transform(trackDtoToWire),
  trackWireSchema,
]);

function askDtoToActivityItem(ask: AskDto): z.infer<typeof activityItemWireSchema> {
  const questions = normalizeAskQuestions(ask.questions);
  return {
    source: 'ask',
    key: `ask:${ask.id}`,
    text: questions.map((question) => question.title).join('\n') || 'Question waiting for your answer',
    at_ms: ask.createdAtMs,
    ask_id: ask.id,
    questions: [...questions],
    delivery: ask.delivery,
  };
}

export function terminalDtoToCardWire(dto: TerminalDto, sort = 100, inWorkbench = true): CardWire {
  return {
    id: dto.id,
    track_id: dto.trackId,
    kind: 'terminal',
    title: dto.title,
    sort,
    payload: {
      terminal_id: dto.id,
      cwd: dto.cwd,
      program: dto.program ?? null,
      status: dto.status,
      exit_code: dto.exitCode ?? null,
      terminal_presentation: { source: dto.attemptId == null ? 'unrecorded' : 'task', in_workbench: inWorkbench } satisfies TerminalPresentation,
    },
    runtime: {
      worker_session_id: dto.id,
      kind: 'terminal',
      status: dto.status === 'running' ? 'running' : 'exited',
      terminal_id: dto.id,
      updated_at_ms: dto.exitedAtMs ?? dto.createdAtMs,
    },
    deletable: true,
    created_at: dto.createdAtMs,
    updated_at: dto.exitedAtMs ?? dto.createdAtMs,
  };
}

/** Activity facts read from the same aggregate as sessions and tasks. */
export function snapshotActivityOverlays(snapshot: TrackAggregateSnapshotDto): OverlayWire[] {
  const openAskItems = snapshot.openAsks
    .filter((ask) => ask.status === 'open')
    .map(askDtoToActivityItem);
  const closed = snapshot.track.closedAtMs != null;
  const sessions = snapshot.sessions.filter(s => s.archivedAtMs == null);
  const workingStates = ['queued', 'running', 'stopping', 'compacting'];
  const blocked = sessions.some(s => s.status === 'unknown' && (s.boundTaskId == null
    || (s.boundAttemptId != null && snapshot.tasks.some(t => t.id === s.boundTaskId && t.latestAttemptId === s.boundAttemptId && ['running', 'verifying'].includes(t.status)))));
  const failed = blocked || snapshot.workspacePreparation === 'failed' || snapshot.workspacePreparation === 'unknown';
  const working = !closed && (snapshot.tasks.some(t => t.status === 'running' || t.status === 'verifying')
    || sessions.some(s => workingStates.includes(s.status)) || snapshot.workspacePreparation === 'pending');
  const attention = closed ? 'none' : failed ? 'failed'
    : openAskItems.length > 0 || (snapshot.pendingApprovals?.length ?? 0) > 0 ? 'input' : 'none';
  const inputIds = new Set([...(snapshot.pendingApprovals ?? []).map(a => a.sessionId),
    ...snapshot.openAsks.filter(a => a.status === 'open').map(a => a.sessionId ?? snapshot.sessions.find(s => s.isLead)?.id)]);
  return [{
    id: `activity-${snapshot.track.id}`,
    plugin_id: KERNEL_OVERLAY_PLUGIN_ID,
    entity_kind: 'track',
    entity_id: snapshot.track.id,
    kind: 'activity',
    payload: {
      schemaVersion: 4,
      working,
      attention,
      activity_at_ms: null,
      notifiableSessions: sessions.filter(s => s.boundTaskId == null && s.lastNotifiableTurnAtMs != null)
        .map(s => ({ sessionId: s.id, lastNotifiableTurnAtMs: s.lastNotifiableTurnAtMs })),
      items: closed ? [] : openAskItems,
      cards: closed ? [] : sessions.flatMap(s => {
        const state = s.status === 'unknown' ? 'failed' : inputIds.has(s.id) ? 'input'
          : workingStates.includes(s.status) ? 'working' : null;
        return state === null ? [] : [{ card_id: s.id, state }];
      }),
    },
    updated_at: snapshot.track.updatedAtMs,
  }];

}

export function tracksInAreaOperation(areaId: string, includeClosed = true): ApiOperation<TrackWire[]> {
  return {
    method: 'GET',
    path: `/api/v1/areas/${encodeURIComponent(areaId)}/tracks?includeClosed=${includeClosed ? 'true' : 'false'}`,
    responseSchema: z.array(trackWireOrDtoSchema),
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

export function trackWorkspaceOptionsOperation(areaId: string, cwd?: string, templateId?: string): ApiOperation<TrackWorkspaceOptions> {
  const query = [
    ...(cwd === undefined ? [] : [`cwd=${encodeURIComponent(cwd)}`]),
    ...(templateId ? [`templateId=${encodeURIComponent(templateId)}`] : []),
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
export function createTrackOperation(body: NewTrackBody, idempotencyKey: string): ApiOperation<TrackWire> {
  const { workspace_option_id: workspaceOptionId, ...rest } = body;
  const wireBody = {
    ...rest,
    areaId: body.area_id,
    provider: toWireAgentProvider(body.planner_provider),
    ...(workspaceOptionId !== undefined ? { workspaceOptionId } : body.isolation !== undefined
      ? { workspaceDriver: body.isolation === 'worktree' ? 'vcs-workspace' : 'direct' }
      : {}),
    ...(body.template_id !== undefined ? { templateId: body.template_id } : {}),
    ...(body.template_input !== undefined ? { templateInputs: body.template_input } : {}),
    ...(body.reasoning_effort !== undefined ? { reasoningEffort: body.reasoning_effort } : {}),
  };
  return {
    method: 'POST',
    path: '/api/v1/tracks',
    body: wireBody,
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: z.union([
      trackAggregateSnapshotDtoSchema.transform((snapshot) => trackDtoToWire(snapshot.track)),
      trackWireOrDtoSchema,
    ]),
  };
}

/**
 * What one failed `POST /api/tracks` means for its draft and key.
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

export function updateTrackOperation(trackId: string, body: TrackPatchBody): ApiOperation<TrackWire> {
  const wireBody = {
    ...(body.title !== undefined ? { title: body.title } : {}),
    ...(body.closed !== undefined ? { closed: body.closed } : {}),
    ...(body.pinned !== undefined
      ? { pinned: body.pinned }
      : body.pinned_at !== undefined
        ? { pinned: body.pinned_at !== null, pinned_at: body.pinned_at }
        : {}),
    ...(body.enabled_plugins !== undefined ? { enabledPlugins: [...body.enabled_plugins] } : {}),
    ...(body.lead_session_id !== undefined ? { leadSessionId: body.lead_session_id } : {}),
    ...(body.sort !== undefined ? { sortOrder: body.sort, sort: body.sort } : {}),
    ...(body.admin !== undefined ? { admin: body.admin } : {}),
  };
  return {
    method: 'PATCH',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}`,
    body: wireBody,
    responseSchema: trackWireOrDtoSchema,
  };
}

/**
 * What a failed `PATCH /api/tracks/{id}` (rename, pin, close, reopen, admin) says. It sets a value, so a retry is safe: 400, 403,
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
  grantAdmin: Object.freeze({ refused: 'The track was not made an Area admin.', unknown: 'Making the track an Area admin is unconfirmed.' }),
  revokeAdmin: Object.freeze({ refused: 'The Area admin grant was not removed.', unknown: 'Removing the Area admin grant is unconfirmed.' }),
}) satisfies Readonly<Record<string, WriteText>>;

export function deleteTrackOperation(trackId: string): ApiOperation<undefined> {
  return { method: 'DELETE', path: `/api/v1/tracks/${encodeURIComponent(trackId)}`, responseSchema: z.undefined() };
}

export type NewTerminalCardBody = Readonly<{
  theme: ThemeRgb;
  cwd?: string;
  program?: string;
  title?: string | null;
  sort?: number | null;
  env?: Readonly<Record<string, string>>;
}>;

/** Changes inventory membership only; source and terminal lifecycle remain server-owned. */
export function setTerminalWorkbenchOperation(cardId: string, inWorkbench: boolean): ApiOperation<CardWire> {
  return { method: 'PATCH', path: `/api/cards/${encodeURIComponent(cardId)}/terminal-workbench`,
    body: { in_workbench: inWorkbench }, responseSchema: cardWireSchema };
}

export function listTrackTerminalsOperation(trackId: string): ApiOperation<CardWire[]> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals`,
    responseSchema: z.union([
      z.array(terminalDtoSchema).transform((dtos) => dtos.map((dto, index) => terminalDtoToCardWire(dto, 100 + index))),
      z.array(cardWireSchema),
    ]),
  };
}

/**
 * `POST /api/v1/tracks/:id/terminals`. Keyed per add-card intent, not per call: a retry under the key joins the terminal
 * the first attempt made, where an unkeyed one would make a second terminal.
 */
export function createTerminalCardOperation(
  trackId: string,
  body: NewTerminalCardBody,
  idempotencyKey: string,
): ApiOperation<CardWire> {
  const wireBody = {
    ...(body.title !== undefined && body.title !== null ? { title: body.title } : {}),
    ...(body.program !== undefined ? { program: body.program } : {}),
    ...(body.cwd !== undefined ? { cwd: body.cwd } : {}),
    ...(body.env !== undefined ? { env: body.env } : {}),
    idempotencyKey,
  };
  return {
    method: 'POST',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals`,
    body: wireBody,
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: z.union([
      terminalDtoSchema.transform((dto) => terminalDtoToCardWire(dto)),
      cardWireSchema,
    ]),
  };
}

export function deleteTerminalOperation(trackId: string, termId: string): ApiOperation<undefined> {
  return {
    method: 'DELETE',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/terminals/${encodeURIComponent(termId)}`,
    responseSchema: z.union([terminalDtoSchema.transform(() => undefined), z.undefined()]),
  };
}

/** `theme` is required by the kernel (422 without it): the daemon answers codex's OSC 10/11 probe with these colours. */
export type NewCodexCardBody = Readonly<{
  theme: ThemeRgb;
  title?: string | null;
  cwd?: string;
  prompt?: string;
  sort?: number | null;
}>;

/** `POST /api/tracks/:id/codex-cards`, keyed per add-card intent like the terminal create. */
export function createCodexCardOperation(
  trackId: string,
  body: NewCodexCardBody,
  idempotencyKey: string,
): ApiOperation<CardWire> {
  return {
    method: 'POST',
    path: `/api/tracks/${encodeURIComponent(trackId)}/codex-cards`,
    body,
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: cardWireSchema,
  };
}

/** `theme` is required as for Codex. No `cwd`: the kernel runs the card in the track's workspace, as for a terminal (#2516). */
export type NewClaudeCardBody = Readonly<{
  theme: ThemeRgb;
  title?: string | null;
}>;

/** `POST /api/tracks/:id/claude-cards` (#2516), keyed per add-card intent like the terminal create. */
export function createClaudeCardOperation(
  trackId: string,
  body: NewClaudeCardBody,
  idempotencyKey: string,
): ApiOperation<CardWire> {
  return {
    method: 'POST',
    path: `/api/tracks/${encodeURIComponent(trackId)}/claude-cards`,
    body,
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: cardWireSchema,
  };
}

/** `POST /api/tracks/:id/cards` — direct create for kinds that own no runtime; worker kinds have atomic endpoints of their own. */
export type NewCardBody = Readonly<{
  kind: string;
  payload?: unknown;
  title?: string | null;
  sort?: number | null;
}>;

/** `POST /api/tracks/:id/cards`, keyed per add-card intent like the terminal create. */
export function createCardOperation(trackId: string, body: NewCardBody, idempotencyKey: string): ApiOperation<CardWire> {
  return {
    method: 'POST',
    path: `/api/tracks/${encodeURIComponent(trackId)}/cards`,
    body,
    headers: { 'Idempotency-Key': idempotencyKey },
    responseSchema: cardWireSchema,
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

/** The kernel refuses this for a card it owns (`deletable === false`). */
export function deleteCardOperation(cardId: string): ApiOperation<undefined> {
  return {
    method: 'DELETE',
    path: `/api/cards/${encodeURIComponent(cardId)}`,
    responseSchema: z.undefined(),
  };
}

export function overlaysByKindOperation(entityKind: 'track' | 'card'): ApiOperation<OverlayWire[]> {
  return {
    method: 'GET',
    path: `/api/v1/overlays?entity_kind=${entityKind}${entityKind === 'track' ? '&includeClosed=true' : ''}`,
    responseSchema: z.union([
      overlaysListDtoSchema.transform((dto): OverlayWire[] => {
        if (entityKind !== 'track') return [];
        return dto.tracks.map((item): OverlayWire => ({
          id: `activity-${item.trackId}`,
          plugin_id: KERNEL_OVERLAY_PLUGIN_ID,
          entity_kind: 'track',
          entity_id: item.trackId,
          kind: 'activity',
          payload: {
            schemaVersion: 4,
            working: item.isWorking,
            attention: item.hasBlockedSession || item.workspaceFailed ? 'failed'
              : item.openAskCount + item.pendingApprovalCount > 0 ? 'input' : 'none',
            activity_at_ms: null,
            notifiableSessions: item.notifiableSessions,
            items: [],
            cards: [],
          },
          updated_at: item.updatedAtMs,
        }));
      }),
      z.array(overlaySchema),
    ]),
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
