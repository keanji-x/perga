import { trackReportReadSchema } from './report-verdicts.js';
import type { TaskPendingReason, TaskVerdict } from './report-verdicts.js';
import { type AppBlockPayload, appBlockPayloadSchema, type ChartCandlesPayload, chartCandlesPayloadSchema, type ChartSeriesPayload, chartSeriesPayloadSchema, type PreviewBlockPayload, previewBlockPayloadSchema, type ProseBlockPayload, proseBlockPayloadSchema, type TableBlockPayload, tableBlockPayloadSchema, type TaskBlockPayload, taskBlockPayloadSchema } from './report-payloads.js';
// Reading a track's report out of its cards: a sequence of typed blocks carried in the
// `track-report` card's payload. `blocks[]` is authoritative (it carries the ids every
// deep link addresses); `body` is the flat fallback. Everything here is fail-soft.

import { z } from 'zod';
import { checkpointsBlockPayloadSchema, type CheckpointsBlockPayload } from './report-checkpoints.js';
import { htmlBlockPayloadSchema, type HtmlBlockPayload } from './report-html.js';
import { objectiveBlockPayloadSchema, type ObjectiveBlockPayload } from './report-objective.js';
import { reportTaskKey, reportTaskPayload } from './report-task-declaration.js';
import { nativeViewPayloadSchema, type NativeViewPayload } from './report-view.js';
import { widgetBlockPayloadSchema, type WidgetBlockPayload } from './report-widget.js';

import { type ReportDocumentDto } from '../api/perga-v1.js';
import { normalizeWireAgentProvider } from '../api/schemas.js';
import type { ApiOperation } from '../api/types.js';
import { extractOutline, markdownExcerpt, parse, REPORT_MAX_DEPTH, reportHeadingIdPolicy } from '../markdown/public.js';
import type { CurrentTaskExecution } from './task-execution.js';
import type { CardWire } from './track.js';

/** The card kind the kernel reserves for the report. One per track, undeletable. */
export const TRACK_REPORT_CARD_KIND = 'track-report';

/**
 * A block, discriminated by `kind`, with `unsupported` as the closed default. `rev` is carried
 * only on `chart.series`, whose data request is bound to the block revision; it is a query key,
 * never a rendered value.
 */
export type ReportBlock =
  | Readonly<{ id: string; kind: 'prose'; payload: ProseBlockPayload }>
  | Readonly<{ id: string; kind: 'chart.candles'; payload: ChartCandlesPayload }>
  | Readonly<{ id: string; kind: 'chart.series'; rev: number; payload: ChartSeriesPayload }>
  | Readonly<{ id: string; kind: 'objective'; payload: ObjectiveBlockPayload }>
  | Readonly<{ id: string; kind: 'table'; payload: TableBlockPayload }>
  | Readonly<{ id: string; kind: 'view'; payload: NativeViewPayload }>
  | Readonly<{ id: string; kind: 'app'; payload: AppBlockPayload }>
  | Readonly<{ id: string; kind: 'task'; payload: TaskBlockPayload }>
  | Readonly<{ id: string; kind: 'preview'; payload: PreviewBlockPayload }>
  | Readonly<{ id: string; kind: 'html'; payload: HtmlBlockPayload }>
  | Readonly<{ id: string; kind: 'checkpoints'; payload: CheckpointsBlockPayload }>
  | Readonly<{ id: string; kind: 'widget'; payload: WidgetBlockPayload }>
  | Readonly<{ id: string; kind: 'unsupported'; declaredKind: string }>;

const blockWireSchema = z.object({
  id: z.string().min(1),
  kind: z.string(),
  /* The kernel always writes one; optional on the read because only `chart.series` consumes it. */
  rev: z.number().int().nonnegative().optional(),
  markdown: z.string().nullish(),
  payload: z.unknown(),
});

function normalizePergaTaskPayload(raw: Record<string, unknown>, blockId: string): Record<string, unknown> {
  const key = reportTaskKey(raw, blockId);
  const rawKind = typeof raw.kind === 'string'
    ? raw.kind
    : typeof raw.type === 'string'
      ? raw.type
      : 'agent';
  const provider = typeof raw.provider === 'string' ? raw.provider : undefined;
  const runtimeProvider = normalizeWireAgentProvider(rawKind === 'agent' ? provider : (provider ?? rawKind));
  const normalizedProvider = runtimeProvider?.slice(runtimeProvider.lastIndexOf(':') + 1);
  const kind = rawKind === 'terminal' || rawKind === 'command'
    ? 'terminal'
    : rawKind === 'compact'
      ? 'compact'
      : normalizedProvider === 'claude' || normalizedProvider === 'antigravity' || normalizedProvider === 'opencode'
        ? normalizedProvider
        : 'codex';
  const access = raw.access ?? raw.access_mode ?? raw.accessMode;
  const goalOrCmd = typeof raw.command === 'string'
    ? raw.command
    : typeof raw.cmd === 'string'
      ? raw.cmd
      : typeof raw.goal === 'string'
        ? raw.goal
        : typeof raw.goalOrCmd === 'string'
          ? raw.goalOrCmd
          : typeof raw.goal_or_cmd === 'string'
            ? raw.goal_or_cmd
            : '';
  const dependsOn = Array.isArray(raw.depends_on)
    ? raw.depends_on
    : Array.isArray(raw.dependsOn)
      ? raw.dependsOn
      : undefined;
  const ready = typeof raw.ready === 'boolean' ? raw.ready : false;
  if (kind === 'terminal') {
    return {
      key,
      kind: 'terminal',
      command: goalOrCmd,
      ...(access === 'read_write' ? { access: 'read_write' } : {}),
      ...(typeof raw.acceptance === 'string' ? { acceptance: raw.acceptance } : {}),
      ...(raw.gate !== undefined ? { gate: raw.gate } : {}),
      ...(dependsOn !== undefined ? { depends_on: dependsOn } : {}),
      ...(raw.context !== undefined ? { context: raw.context } : {}),
      ready,
      declared_by: 'spec',
    };
  }
  return {
    key,
    kind,
    goal: goalOrCmd,
    ...(access === 'read_only' || access === 'read_write' ? { access } : {}),
    ...(typeof raw.acceptance === 'string' ? { acceptance: raw.acceptance } : {}),
    ...(raw.gate !== undefined ? { gate: raw.gate } : {}),
    ...(dependsOn !== undefined ? { depends_on: dependsOn } : {}),
    ...(raw.context !== undefined ? { context: raw.context } : {}),
    ready,
    declared_by: 'spec',
  };
}

function resolveWirePayload(wire: z.infer<typeof blockWireSchema>): unknown {
  let payload = wire.kind === 'task' ? reportTaskPayload(wire) ?? wire.payload : wire.payload;
  if ((payload === null || payload === undefined) && typeof wire.markdown === 'string') {
    if (wire.kind === 'prose') {
      payload = { markdown: wire.markdown };
    } else if (wire.kind === 'html') {
      const trimmed = wire.markdown.trim();
      if (trimmed.startsWith('{')) {
        try { payload = JSON.parse(trimmed); } catch { payload = { html: wire.markdown }; }
      } else {
        payload = { html: wire.markdown };
      }
    } else {
      const trimmed = wire.markdown.trim();
      if (trimmed.startsWith('{')) {
        try { payload = JSON.parse(trimmed); } catch { /* fall through */ }
      }
    }
  }
  if (
    wire.markdown !== undefined
    && wire.kind === 'task'
    && typeof payload === 'object'
    && payload !== null
    && !('declared_by' in payload)
  ) {
    payload = normalizePergaTaskPayload(payload as Record<string, unknown>, wire.id);
  }
  return payload;
}

/** As a function rather than a frozen object: a module-level map of schemas would be module runtime state. */
function payloadSchemaFor(kind: string): z.ZodType | null {
  switch (kind) {
    case 'prose': return proseBlockPayloadSchema;
    case 'chart.candles': return chartCandlesPayloadSchema;
    case 'chart.series': return chartSeriesPayloadSchema;
    case 'objective': return objectiveBlockPayloadSchema;
    case 'table': return tableBlockPayloadSchema;
    case 'view': return nativeViewPayloadSchema;
    case 'app': return appBlockPayloadSchema;
    case 'task': return taskBlockPayloadSchema;
    case 'preview': return previewBlockPayloadSchema;
    case 'html': return htmlBlockPayloadSchema;
    case 'checkpoints': return checkpointsBlockPayloadSchema;
    case 'widget': return widgetBlockPayloadSchema;
    default: return null;
  }
}

/** One wire block → one renderable block, degrading to `unsupported` rather than throwing. */
function toReportBlock(wire: z.infer<typeof blockWireSchema>): ReportBlock {
  const schema = payloadSchemaFor(wire.kind);
  if (schema === null) return { id: wire.id, kind: 'unsupported', declaredKind: wire.kind };
  const parsed = schema.safeParse(resolveWirePayload(wire));
  if (!parsed.success) return { id: wire.id, kind: 'unsupported', declaredKind: wire.kind };
  if (wire.kind === 'chart.series') {
    // A series block without a revision cannot ask for its data, so it degrades like an unreadable payload.
    if (wire.rev === undefined) return { id: wire.id, kind: 'unsupported', declaredKind: wire.kind };
    return { id: wire.id, kind: 'chart.series', rev: wire.rev, payload: parsed.data as ChartSeriesPayload };
  }
  // The discriminant and its payload were validated together above; TypeScript cannot carry that pairing.
  return { id: wire.id, kind: wire.kind, payload: parsed.data } as ReportBlock;
}

/** A deliberately narrow read: `schemaVersion` stays unparsed so a newer payload stays readable. */
export const trackReportPayloadSchema = z.object({
  pergaSnapshot: z.boolean().optional(),
  docRev: z.number().int().nonnegative().optional(),
  summary: z.string().default(''),
  body: z.string().default(''),
  blocks: z.unknown().nullish(),
});

export type TrackReport = Readonly<{
  docRev?: number;
  blockRevs?: Readonly<Record<string, number>>;
  summary: string;
  /** Markdown source — the flat projection, and the only content a v1 row has. */
  body: string;
  /** `null` on a v1 row: blocks were not persisted, so `body` is all there is. */
  blocks: readonly ReportBlock[] | null;
}>;

/** The aggregate report is a document, independent of Workbench cards. */
export function readReportDocument(document: ReportDocumentDto): TrackReport | null {
  return readTrackReportPayload({ ...document, pergaSnapshot: true });
}

/** The kernel's single report card, in wire order. */
export function trackReportCardIn(cards: readonly CardWire[]): CardWire | undefined {
  return cards.find((candidate) => candidate.kind === TRACK_REPORT_CARD_KIND);
}

/**
 * The track's report, or `null` when it has none — no card, an unparseable payload, or one empty in
 * both projections.
 */
export function readTrackReport(cards: readonly CardWire[]): TrackReport | null {
  const card = trackReportCardIn(cards);
  if (card === undefined) return null;
  return readTrackReportPayload(card.payload);
}

/** Shared resource and owner cards consume the same block parser. */
export function readTrackReportPayload(payload: unknown): TrackReport | null {
  const parsed = trackReportPayloadSchema.safeParse(payload);
  if (!parsed.success) return null;
  const summary = parsed.data.summary.trim();
  const body = parsed.data.body.trim();
  const blockRevs: Record<string, number> = {};
  // One malformed block must cost only that block.
  const blocks = Array.isArray(parsed.data.blocks)
    ? parsed.data.blocks.flatMap((candidate) => {
      const wire = blockWireSchema.safeParse(candidate);
      if (!wire.success) return [];
      if (wire.data.rev !== undefined) blockRevs[wire.data.id] = wire.data.rev;
      return [toReportBlock(wire.data)];
    })
    : null;
  const isPerga = parsed.data.pergaSnapshot === true;
  const allBlankProse = blocks !== null
    && blocks.length > 0
    && blocks.every((block) => block.kind === 'prose' && block.payload.markdown.trim() === '');
  // An empty blocks array (or untouched blank Perga seed prose) is the same emptiness as a blank body.
  if (summary === '' && body === '' && (blocks === null || blocks.length === 0 || (isPerga && allBlankProse))) {
    return null;
  }
  return {
    ...(isPerga && parsed.data.docRev !== undefined ? { docRev: parsed.data.docRev, blockRevs } : {}),
    summary,
    body,
    blocks,
  };
}

/* Outline: the shape is decided by the blocks, not by the headings. */

export type ReportOutlineChild = Readonly<{ blockId: string; label: string }>;
export type ReportOutlineItem = Readonly<{
  /** The anchor to scroll to: a heading id inside a prose block, or a block id. */
  blockId: string;
  label: string;
  /** Visible prose owned by this section; empty when it has no body text. */
  excerpt: string;
  /** Continuous across blocks; `null` for a non-prose block with no section above it. */
  number: number | null;
  children: readonly ReportOutlineChild[];
}>;

/** A non-prose block's label is the first payload field that names the thing it draws; the kind is the last resort. */
function blockLabel(block: ReportBlock): string {
  if (block.kind === 'unsupported') return block.declaredKind;
  if (block.kind === 'task') return block.payload.key;
  const payload: Record<string, unknown> = block.payload;
  for (const key of ['symbol', 'src', 'caption', 'title']) {
    const value = payload[key];
    if (typeof value === 'string' && value !== '') return value;
  }
  return block.kind;
}

/**
 * Derive the outline: every H1 is a numbered top-level item, an H2 hangs beneath the H1 above
 * it (or stays top-level with no preceding H1), and a non-prose block hangs under the numbered
 * section above it or becomes an unnumbered top-level item.
 */
export function deriveReportOutline(blocks: readonly ReportBlock[] | null, readMarkdown: typeof parse = parse): ReportOutlineItem[] {
  if (blocks === null) return [];
  const outline: ReportOutlineItem[] = [];
  let sectionNumber = 0;
  let lastNumbered: { children: ReportOutlineChild[] } | null = null;
  let lastH1: { children: ReportOutlineChild[] } | null = null;

  for (const block of blocks) {
    if (block.kind === 'prose') {
      const parsed = readMarkdown(block.payload.markdown);
      if (parsed.status === 'failed') continue;
      const headings = extractOutline([{ context: { blockId: block.id }, ast: parsed.value }], {
        maxDepth: REPORT_MAX_DEPTH,
        headingId: reportHeadingIdPolicy,
        textPolicy: 'non-empty-heading-label',
        referenceText: 'visible',
        traversal: 'recursive',
      });
      const boundary = headings.find(heading => heading.depth === 1 || lastH1 === null);
      const continuation = markdownExcerpt(block.payload.markdown.slice(0, boundary?.position.start.offset), readMarkdown);
      const previous = outline.at(-1);
      if (lastNumbered !== null && previous !== undefined && continuation !== '') {
        outline[outline.length - 1] = { ...previous, excerpt: `${previous.excerpt} ${continuation}`.trim() };
      }
      for (const [index, heading] of headings.entries()) {
        const next = headings.slice(index + 1).find(candidate => candidate.depth <= heading.depth);
        const section = block.payload.markdown.slice(heading.position.end.offset, next?.position.start.offset);
        const excerpt = markdownExcerpt(section, readMarkdown);
        if (heading.depth === 2 && lastH1 !== null) {
          lastH1.children.push({ blockId: heading.id, label: heading.text });
          continue;
        }
        sectionNumber += 1;
        const children: ReportOutlineChild[] = [];
        outline.push({ blockId: heading.id, label: heading.text, excerpt, number: sectionNumber, children });
        lastNumbered = { children };
        if (heading.depth === 1) lastH1 = { children };
      }
      continue;
    }
    // `task` blocks are lifted out of the flow into the `Reference` appendix, so they are not in the outline.
    if (isTaskBlock(block)) continue;
    const child: ReportOutlineChild = { blockId: block.id, label: blockLabel(block) };
    if (lastNumbered === null) {
      outline.push({ blockId: block.id, label: child.label, excerpt: '', number: null, children: [] });
      continue;
    }
    lastNumbered.children.push(child);
  }
  return outline;
}

/* Tasks, as an inventory: derived from the declarations in the document, decorated with the
   kernel's run projection (`taskDiagnostics`) by `deriveReportTasks` below. */

/** A `task` block, including one this build could not read. One predicate, three readers. */
export function isTaskBlock(block: ReportBlock): boolean {
  return block.kind === 'task'
    || (block.kind === 'unsupported' && block.declaredKind === 'task');
}

/** `unreadable` is a task whose payload this build cannot parse: no key and no readiness, only an id. */
export type ReportTaskState = 'ready' | 'not-ready' | 'withdrawn' | 'unreadable';

/**
 * The `tasks` statuses inside the eventless window: `scheduler::mark_running` flips
 * `dispatched → running` and stamps `worker_card_id` with no event, so only those two need a
 * timer. `pending` and `verifying` are evented on every exit. An allowlist so an unknown
 * status degrades toward silence.
 */
function eventlessWindowTaskStatuses(): ReadonlySet<string> {
  return new Set(['dispatched', 'running']);
}

/**
 * Does any row this panel draws describe a run still inside the eventless window? Reads rows,
 * not verdicts: the kernel synthesises a verdict for a deleted declaration that no row shows.
 */
export function hasLiveTaskRun(rows: readonly ReportTaskRow[] | undefined): boolean {
  if (rows === undefined) return false;
  const live = eventlessWindowTaskStatuses();
  return rows.some((row) => row.status !== null && live.has(row.status));
}

/** The track's task verdicts via `GET /api/v1/tracks/{trackId}/report`. */
export function trackTaskVerdictsOperation(trackId: string): ApiOperation<TaskVerdict[]> {
  return {
    method: 'GET',
    path: `/api/v1/tracks/${encodeURIComponent(trackId)}/report`,
    responseSchema: trackReportReadSchema,
  };
}

/** The execution kinds a live task declaration can name; a verdict does not carry one. */
export type TaskWorkerKind = 'codex' | 'claude' | 'antigravity' | 'opencode' | 'terminal' | 'compact';

/**
 * One row of the panel's task inventory. The declaration word, status and kind travel separately
 * because they are rendered separately.
 */
export type ReportTaskRow = Readonly<{
  /** Newer attempt/receipt evidence, when loaded by app; never copied into report diagnostics. */
  execution?: CurrentTaskExecution;
  /** The block to reveal in the document — the detail lives there, not here. */
  blockId: string;
  key: string;
  state: ReportTaskState;
  /** The word the *declaration* carries; `null` for the ordinary case and for a row that has a `status`. */
  declaration: string | null;
  /** The kernel's `tasks` row status, or `null` when there is no run this row may report. */
  status: string | null;
  /**
   * The kernel's reason for that status, only ever set alongside a `status`; already bounded to
   * `TASK_STATUS_DETAIL_LIMIT`.
   */
  statusDetail: string | null;
  /** The worker kind, off the *declaration*; a withdrawn or unreadable block has none. */
  kind: TaskWorkerKind | null;
  /** The worker card this task is running on, or `null`. */
  workerCardId: string | null;
  /** A server-rendered, actionable pending/admission explanation. */
  pendingReason: TaskPendingReason | null;
}>;

/** The detail's only destinations are an accessible name and a `title`, neither of which a stylesheet can truncate. */
export const TASK_STATUS_DETAIL_LIMIT = 160;

/** The kernel's reason as one bounded line, or `null`; whitespace is collapsed before the bound. */
export function boundedStatusDetail(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') return null;
  const collapsed = raw.replace(/\s+/g, ' ').trim();
  if (collapsed === '') return null;
  if (collapsed.length <= TASK_STATUS_DETAIL_LIMIT) return collapsed;
  /* The bound counts UTF-16 code units; a trailing lone high surrogate is dropped so the cut never
     lands inside a character. */
  const cut = collapsed.slice(0, TASK_STATUS_DETAIL_LIMIT - 1);
  const head = /[\uD800-\uDBFF]$/.test(cut) ? cut.slice(0, -1) : cut;
  return `${head.trimEnd()}…`;
}

/** The word, from the declaration alone. `ready` is silent on purpose. */
function declarationWord(state: ReportTaskState): string | null {
  if (state === 'ready') return null;
  if (state === 'withdrawn') return 'Withdrawn';
  if (state === 'unreadable') return 'Unreadable';
  return 'Not ready';
}

/**
 * Index the verdicts by block id (identity) and by key (fallback for drifted or deleted
 * declarations). A verdict naming a block this report has is kept out of the key index, and a
 * key more than one row claims is not a usable fallback for any of them.
 */
function indexVerdicts(
  verdicts: readonly TaskVerdict[],
  rowBlockIds: ReadonlySet<string>,
  keysClaimedByManyRows: ReadonlySet<string>,
) {
  const byBlockId = new Map<string, TaskVerdict>();
  const byKey = new Map<string, TaskVerdict>();
  for (const verdict of verdicts) {
    if (verdict.blockId !== '' && !byBlockId.has(verdict.blockId)) byBlockId.set(verdict.blockId, verdict);
    const namesARow = verdict.blockId !== '' && rowBlockIds.has(verdict.blockId);
    const ambiguousRow = keysClaimedByManyRows.has(verdict.key);
    if (verdict.key !== '' && !namesARow && !ambiguousRow && !byKey.has(verdict.key)) {
      byKey.set(verdict.key, verdict);
    }
  }
  return { byBlockId, byKey };
}

/**
 * The verdict that is about *this* block, or none. A block-id hit that contradicts the declared
 * key ends the lookup without falling back to the key index. A miss renders blank on purpose.
 */
function verdictFor(
  blockId: string, declaredKey: string,
  index: ReturnType<typeof indexVerdicts>,
): TaskVerdict | undefined {
  const byId = index.byBlockId.get(blockId);
  if (byId !== undefined) {
    return declaredKey === '' || byId.key === '' || byId.key === declaredKey ? byId : undefined;
  }
  return declaredKey === '' ? undefined : index.byKey.get(declaredKey);
}

/**
 * The row's state, from the declaration alone. `tombstoned_by` is the discriminant: a live task may
 * carry `tombstone: null`.
 */
function taskRowState(block: ReportBlock): ReportTaskState {
  if (block.kind !== 'task') return 'unreadable';
  if ('tombstoned_by' in block.payload) return 'withdrawn';
  return block.payload.ready ? 'ready' : 'not-ready';
}

/**
 * The key the block itself declared, or `''` for one this build cannot read; only a declared key
 * may look a verdict up.
 */
function declaredTaskKey(block: ReportBlock): string {
  return block.kind === 'task' ? block.payload.key : '';
}

/**
 * The keys two or more rows of this render declare, tombstones included; purely syntactic so it
 * cannot drift from the kernel.
 */
function keysDeclaredByMoreThanOneRow(blocks: readonly ReportBlock[]): ReadonlySet<string> {
  const seen = new Set<string>();
  const many = new Set<string>();
  for (const block of blocks) {
    if (!isTaskBlock(block)) continue;
    const key = declaredTaskKey(block);
    if (key === '') continue;
    if (seen.has(key)) many.add(key);
    else seen.add(key);
  }
  return many;
}

/**
 * Every `task` block, in document order, as one row each, decorated with the kernel's projection.
 * Withdrawn tasks are kept.
 */
export function deriveReportTasks(
  blocks: readonly ReportBlock[] | null,
  verdicts: readonly TaskVerdict[] = [],
): ReportTaskRow[] {
  if (blocks === null) return [];
  const rowBlockIds = new Set(blocks.filter(isTaskBlock).map((block) => block.id));
  const keysClaimedByManyRows = keysDeclaredByMoreThanOneRow(blocks);
  const index = indexVerdicts(verdicts, rowBlockIds, keysClaimedByManyRows);
  const rows: ReportTaskRow[] = [];
  for (const block of blocks) {
    if (!isTaskBlock(block)) continue;
    // An unreadable task still gets a row, and its id stands in for the name.
    const isReadable = block.kind === 'task';
    const state = taskRowState(block);
    const declaredKey = declaredTaskKey(block);
    /* `kind` lives on the live declaration, not on the verdict. */
    const kind: TaskWorkerKind | null = isReadable && !('tombstoned_by' in block.payload)
      ? block.payload.kind
      : null;
    /* `key` may be empty on the wire; the block id stands in so the row always has a name. */
    const key = declaredKey === '' ? block.id : declaredKey;
    /* A withdrawn or unreadable declaration takes no runtime decoration: withdrawal does not delete
       the `tasks` row, so its verdict keeps reporting a status. */
    const decorated = state !== 'withdrawn' && state !== 'unreadable';
    const verdict = decorated ? verdictFor(block.id, declaredKey, index) : undefined;
    /* `''` is not a status: the wire types it as an optional string, and an empty one would pass every `null` gate. */
    const status = verdict?.status === undefined || verdict.status === null || verdict.status === ''
      ? null
      : verdict.status;
    const pendingReason = decorated && verdict?.pendingReason !== undefined
      ? verdict.pendingReason ?? null
      : null;
    rows.push({
      blockId: block.id,
      key,
      state,
      /* No `tasks` row means nothing has run; the declaration word stands down once there is a status. */
      declaration: status === null ? declarationWord(state) : null,
      status,
      /* Gated on `status`: the detail is a qualifier on the status word and has nowhere to attach without one. */
      statusDetail: status === null ? null : boundedStatusDetail(verdict?.statusDetail),
      kind,
      /* `''` is not a card id. */
      workerCardId: verdict?.workerCardId === undefined || verdict.workerCardId === null
        || verdict.workerCardId === '' ? null : verdict.workerCardId,
      pendingReason,
    });
  }
  return rows;
}

/* `perga://wave/<id>[#<block id>]` links, resolved here so the renderer never holds a URL. */

const TRACK_REPORT_LINK_PREFIX = 'perga://wave/';
const TRACK_REPORT_LINK_TARGET = /^([^/?#]+)(?:#([^#]+))?$/;

export function trackReportLinkUrl(trackId: string): string {
  return `${TRACK_REPORT_LINK_PREFIX}${encodeURIComponent(trackId)}`;
}

/** Block ids the kernel mints. A link whose fragment is not one of these keeps the track and drops the fragment. */
const BLOCK_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

export type ReportLinkTarget = Readonly<{ trackId: string; blockId: string | null }>;

export function parseReportLink(destination: string): ReportLinkTarget | null {
  if (!destination.startsWith(TRACK_REPORT_LINK_PREFIX)) return null;
  const match = TRACK_REPORT_LINK_TARGET.exec(destination.slice(TRACK_REPORT_LINK_PREFIX.length));
  if (match === null) return null;
  const trackId = match[1] ?? '';
  const blockId = match[2];
  if (trackId === '') return null;
  let decodedTrackId = trackId;
  try {
    decodedTrackId = decodeURIComponent(trackId);
  } catch {
    // Agent-written links must remain navigable even when an escape is malformed.
  }
  return {
    trackId: decodedTrackId,
    blockId: blockId !== undefined && BLOCK_ID_PATTERN.test(blockId) ? blockId : null,
  };
}
