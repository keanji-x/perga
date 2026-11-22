import { z } from 'zod';
import { isCalendarDate } from './report-date.js';
import { sameOriginPath } from './report-src.js';
import { inlineTableBlockPayloadSchema, max2048CodePoints } from './report-table.js';

/* Per-kind block payloads. The bounds mirror the kernel's own validators, so a payload the
   kernel accepts is exactly a payload this renders. */

export const proseBlockPayloadSchema = z.object({ markdown: z.string() });

/** One candle: `[ts_ms, open, high, low, close, volume?]`. */
export const candleTupleSchema = z.tuple([
  z.number(), z.number(), z.number(), z.number(), z.number(), z.number().nullish(),
]);

export const chartCandlesPayloadSchema = z.strictObject({
  symbol: max2048CodePoints(z.string().min(1)),
  period: z.enum(['day', 'week', 'month']).nullish(),
  /** Data is inlined; range switching is a pure client-side filter. */
  candles: z.array(candleTupleSchema).min(2).max(5000),
  overlays: z.array(z.enum(['ma20', 'ma60'])).nullish(),
  caption: max2048CodePoints(z.string()).nullish(),
});

/**
 * A live `table` names where its rows come from: the overlay written by `<plugin_id>` under
 * `<overlay_kind>`. A shape check only — the plugin need not be installed.
 */
export const LIVE_TABLE_SOURCE_PATTERN = /^neige:\/\/plugin\/[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;

/** The rows-carrying form — and the shape a live table's overlay must hold. */
export const liveTableBlockPayloadSchema = z.strictObject({
  source: max2048CodePoints(z.string().regex(LIVE_TABLE_SOURCE_PATTERN,
    'must be neige://plugin/<plugin_id>/<overlay_kind>')),
  caption: max2048CodePoints(z.string()).nullish(),
});

/* chart.series: a chart that names its data instead of carrying it. Like the kernel, `as_of`
   is never compared with today — a cutoff in the future is a frozen block the renderer must draw. */

/** `^[A-Z]{2,8}:[A-Za-z0-9._-]{1,32}$` — venue prefix and symbol. */
export const CHART_SERIES_ASSET_PATTERN = /^[A-Z]{2,8}:[A-Za-z0-9._-]{1,32}$/;

export const chartSeriesPayloadSchema = z.strictObject({
  source: max2048CodePoints(z.string().regex(LIVE_TABLE_SOURCE_PATTERN,
    'must be neige://plugin/<plugin_id>/<tool>')),
  series: z.array(max2048CodePoints(z.string().regex(CHART_SERIES_ASSET_PATTERN,
    'must be a venue-qualified asset id such as US:NVDA'))).min(1).max(8),
  field: z.enum(['close', 'open', 'high', 'low', 'volume']).nullish(),
  range: z.enum(['1M', '3M', '6M', '1Y', '2Y', '5Y']).nullish(),
  period: z.enum(['day', 'week', 'month']).nullish(),
  view: z.enum(['line', 'normalized', 'bar', 'candles']).nullish(),
  as_of: max2048CodePoints(z.string()).nullish(),
  overlays: z.array(z.enum(['ma20', 'ma60'])).nullish(),
  caption: max2048CodePoints(z.string()).nullish(),
})
  .refine((chart) => new Set(chart.series).size === chart.series.length,
    { message: 'series must not repeat an asset' })
  .refine((chart) => chart.view !== 'candles' || (chart.series.length === 1 && chart.field == null),
    { message: 'view candles takes exactly one series and no field' })
  .refine((chart) => chart.overlays == null || chart.view == null || chart.view === 'line' || chart.view === 'candles',
    { message: 'overlays apply only to view line|candles' })
  .refine((chart) => chart.as_of == null || isCalendarDate(chart.as_of),
    { message: 'as_of must be a calendar date in YYYY-MM-DD form' })
  .refine((chart) => !(chart.range === '1M' && chart.period === 'month'),
    { message: 'range 1M cannot hold two complete month periods' });

/* Live first: both members are strict, so order only picks which error a malformed payload reports. */
export const tableBlockPayloadSchema = z.union([
  liveTableBlockPayloadSchema,
  inlineTableBlockPayloadSchema,
]);

export const appBlockPayloadSchema = z.strictObject({
  src: sameOriginPath(),
  title: max2048CodePoints(z.string()).nullish(),
  height: z.number().min(120).max(2000).nullish(),
});

/**
 * `preview` (#1780): a dev server the Planner added with `neige_preview_add`, named by
 * its `key` (the tool's `preview_id`). `path` follows the `app` block's `src` rules; it is opened on the preview's own port.
 */
const PREVIEW_KEY_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

export const previewBlockPayloadSchema = z.strictObject({
  key: z.string().regex(PREVIEW_KEY_PATTERN),
  title: max2048CodePoints(z.string()).nullish(),
  path: sameOriginPath().nullish(),
  height: z.number().min(120).max(2000).nullish(),
});

const taskGateStepSchema = z.strictObject({ name: z.string(), cmd: z.string() });

const taskGateSchema = z.preprocess(
  (raw) => {
    if (typeof raw === 'object' && raw !== null && !Array.isArray(raw) && 'timeoutSecs' in raw && !('timeout_secs' in raw)) {
      const { timeoutSecs, ...rest } = raw as Record<string, unknown>;
      return { ...rest, timeout_secs: timeoutSecs };
    }
    return raw;
  },
  z.strictObject({
    cwd: z.string().nullish(),
    timeout_secs: z.number().int().nullish(),
    steps: z.array(taskGateStepSchema),
  }),
);

function liveTaskSharedShape() {
  return {
    acceptance: z.string().nullish(),
    gate: taskGateSchema.nullish(),
    no_gate_reason: z.string().nullish(),
    depends_on: z.array(z.string()).nullish(),
    priority: z.number().int().nullish(),
    cwd: z.string().nullish(),
    context: z.unknown().nullish(),
    refs: z.array(z.string()).nullish(),
    ready: z.boolean(),
    declared_by: z.enum(['spec', 'user']),
    released_by_user: z.boolean().nullish(),
    spawn: z.enum(['in-wave', 'sub-track']).nullish(),
    tombstone: z.null().nullish(),
  };
}

const agentTaskBlockPayloadSchema = z.strictObject({
  key: z.string(),
  kind: z.enum(['codex', 'claude', 'antigravity', 'opencode']),
  goal: z.string(),
  /** The kernel owns contract registration; the reader preserves its declared name. */
  result_contract: z.string().optional(),
  access: z.enum(['read_only', 'read_write']).nullish(),
  head: z.string().nullish(),
  base: z.string().nullish(),
  start: z.enum(['checkout', 'upstream']).nullish(),
  ...liveTaskSharedShape(),
});

const terminalTaskBlockPayloadSchema = z.strictObject({
  key: z.string(),
  kind: z.literal('terminal'),
  command: z.string(),
  access: z.literal('read_write').nullish(),
  start: z.literal('checkout').nullish(),
  ...liveTaskSharedShape(),
});

/** Read-only compatibility for older report payloads; new writes must use `command` for terminal tasks. */
const legacyTerminalTaskBlockPayloadSchema = z.strictObject({
  key: z.string(),
  kind: z.literal('terminal'),
  goal: z.string(),
  access: z.literal('read_write').nullish(),
  start: z.literal('checkout').nullish(),
  ...liveTaskSharedShape(),
}).transform(({ goal, ...payload }) => ({ ...payload, command: goal }));

const compactTaskBlockPayloadSchema = z.strictObject({
  key: z.string(),
  kind: z.literal('compact'),
  context: z.string(),
  ready: z.boolean(),
  declared_by: z.literal('spec'),
  released_by_user: z.boolean().nullish(),
  tombstone: z.null().nullish(),
});

const liveTaskBlockPayloadSchema = z.union([
  compactTaskBlockPayloadSchema,
  agentTaskBlockPayloadSchema,
  terminalTaskBlockPayloadSchema,
  legacyTerminalTaskBlockPayloadSchema,
]);

/** A withdrawn task keeps its key and both attributions; other reports may cite its block id. */
const tombstoneTaskBlockPayloadSchema = z.strictObject({
  key: z.string(),
  tombstone: z.strictObject({ reason: z.string().nullish() }),
  declared_by: z.enum(['spec', 'user']),
  tombstoned_by: z.enum(['spec', 'user']),
});

export const taskBlockPayloadSchema = z.preprocess((value: unknown) => {
  // Read historical report/event payloads; new wire declarations use sub-track.
  if (typeof value === 'object' && value !== null && 'spawn' in value && value.spawn === 'sub-wave') {
    return { ...value, spawn: 'sub-track' };
  }
  return value;
}, z.union([
  liveTaskBlockPayloadSchema,
  tombstoneTaskBlockPayloadSchema,
]));

export type ProseBlockPayload = z.infer<typeof proseBlockPayloadSchema>;
export type ChartCandlesPayload = z.infer<typeof chartCandlesPayloadSchema>;
export type ChartSeriesPayload = z.infer<typeof chartSeriesPayloadSchema>;
export type TableBlockPayload = z.infer<typeof tableBlockPayloadSchema>;
export type InlineTableBlockPayload = z.infer<typeof inlineTableBlockPayloadSchema>;
export type LiveTableBlockPayload = z.infer<typeof liveTableBlockPayloadSchema>;

/** Narrows a table payload to its live form. */
export function isLiveTablePayload(payload: TableBlockPayload): payload is LiveTableBlockPayload {
  return 'source' in payload;
}
export type AppBlockPayload = z.infer<typeof appBlockPayloadSchema>;
export type TaskBlockPayload = z.infer<typeof taskBlockPayloadSchema>;
export type PreviewBlockPayload = z.infer<typeof previewBlockPayloadSchema>;
