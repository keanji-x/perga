import { z } from 'zod';
import {
  EDITORIAL_CHECK_STATUSES,
  EDITORIAL_STATUSES,
  builtinTableViewSchema,
  max2048CodePoints,
  type BuiltinTableView,
} from './report-editorial.js';
import { builtinListViewSchema } from './report-widget.js';

const LEGACY_ASSIGN_KINDS = Object.freeze(['model', 'command'] as const);
const LEGACY_EXPECT_STATUSES = Object.freeze(['pending', 'beat', 'inline', 'miss'] as const);
const LEGACY_METRIC_DIRECTIONS = Object.freeze(['lte', 'gte'] as const);
const LEGACY_METRIC_STATUSES = Object.freeze(['met', 'on_track', 'at_risk', 'pending'] as const);

const LEGACY_VERDICT_TONE = Object.freeze({
  beat: 'good',
  超预期: 'good',
  inline: 'accent',
  符合预期: 'accent',
  miss: 'bad',
  低于预期: 'bad',
  pending: 'neutral',
  待兑现: 'neutral',
} as const);

const LEGACY_EXPECT_LABEL = Object.freeze({
  pending: '待兑现',
  beat: '超预期',
  inline: '符合预期',
  miss: '低于预期',
} as const);

const legacyCheckSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_CHECK_STATUSES),
  detail: max2048CodePoints(z.string()).nullish(),
  command: max2048CodePoints(z.string()).nullish(),
  output: max2048CodePoints(z.string()).nullish(),
});

const legacyExpectationSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  expected: max2048CodePoints(z.string().min(1)),
  actual: max2048CodePoints(z.string()).nullish(),
  status: z.enum(LEGACY_EXPECT_STATUSES).nullish(),
});

const legacyMetricPointSchema = z.strictObject({
  date: max2048CodePoints(z.string().min(1)),
  value: z.number().finite(),
  note: max2048CodePoints(z.string()).nullish(),
});

const legacyMetricSchema = z.strictObject({
  code: max2048CodePoints(z.string().min(1)).nullish(),
  label: max2048CodePoints(z.string().min(1)),
  tag: max2048CodePoints(z.string()).nullish(),
  current: max2048CodePoints(z.string()).nullish(),
  status: z.enum(LEGACY_METRIC_STATUSES).nullish(),
  updatedAt: max2048CodePoints(z.string()).nullish(),
  evidence: max2048CodePoints(z.string()).nullish(),
  unit: max2048CodePoints(z.string()).nullish(),
  target: z.number().finite().nullish(),
  direction: z.enum(LEGACY_METRIC_DIRECTIONS).nullish(),
  targetLabel: max2048CodePoints(z.string()).nullish(),
  points: z.array(legacyMetricPointSchema).min(2).max(90).nullish(),
});

const legacyTableViewSchema = z.strictObject({
  metrics: z.array(legacyMetricSchema).max(12),
  title: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
});

const legacyListItemSchema = z.strictObject({
  id: z.string().min(1).max(120),
  title: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_STATUSES),
  statusLabel: max2048CodePoints(z.string()).nullish(),
  group: max2048CodePoints(z.string()).nullish(),
  date: max2048CodePoints(z.string()).nullish(),
  verdict: max2048CodePoints(z.string()).nullish(),
  triggerAt: max2048CodePoints(z.string()).nullish(),
  assignKind: z.enum(LEGACY_ASSIGN_KINDS).nullish(),
  assign: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  bullets: z.array(max2048CodePoints(z.string().min(1))).max(12).nullish(),
  docLabel: max2048CodePoints(z.string().min(1)).nullish(),
  sessionId: z.string().min(1).max(160).nullish(),
  taskKey: z.string().min(1).max(160).nullish(),
  workerLabel: max2048CodePoints(z.string().min(1)).nullish(),
  model: z.string().min(1).max(160).nullish(),
  blockedReason: max2048CodePoints(z.string().min(1)).nullish(),
  expectations: z.array(legacyExpectationSchema).max(12).nullish(),
  checks: z.array(legacyCheckSchema).max(16).nullish(),
});

const legacyListViewSchema = z.strictObject({
  items: z.array(legacyListItemSchema).max(100),
  title: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  groups: z.array(max2048CodePoints(z.string().min(1))).max(100).nullish(),
  metrics: z.array(legacyMetricSchema).min(1).max(12).nullish(),
});

function toneFromVerdict(verdict: string | null | undefined) {
  if (verdict == null) return undefined;
  return Object.hasOwn(LEGACY_VERDICT_TONE, verdict)
    ? LEGACY_VERDICT_TONE[verdict as keyof typeof LEGACY_VERDICT_TONE]
    : undefined;
}

function formatLegacyMetricValue(value: number, unit: string | null | undefined): string {
  const formatted = Number.isInteger(value) ? String(value) : value.toFixed(1);
  if (unit == null || unit === '') return formatted;
  return unit === '%' ? `${formatted}%` : `${formatted} ${unit}`;
}

function legacyMetricsToTable(
  metrics: readonly z.infer<typeof legacyMetricSchema>[],
  title?: string | null,
  summary?: string | null,
): BuiltinTableView {
  return {
    ...(title != null ? { title } : {}),
    ...(summary != null ? { summary } : {}),
    columns: [
      { key: 'result', label: 'Key Result' },
      { key: 'current', label: '当前' },
      { key: 'target', label: '目标' },
      { key: 'updatedAt', label: '更新时间', align: 'right' },
    ],
    rows: metrics.map((metric) => {
      const latest = metric.points?.[metric.points.length - 1];
      const currentText = metric.current ?? (latest != null ? formatLegacyMetricValue(latest.value, metric.unit) : '—');
      const meetsTarget = latest == null || metric.target == null || metric.direction == null
        ? true
        : metric.direction === 'lte'
          ? latest.value <= metric.target
          : latest.value >= metric.target;
      const status = metric.status ?? (latest != null ? (meetsTarget ? 'met' : 'at_risk') : 'pending');
      const target = metric.targetLabel ?? (
        metric.target != null && metric.direction != null
          ? `${metric.direction === 'lte' ? '≤' : '≥'} ${formatLegacyMetricValue(metric.target, metric.unit)}`
          : null
      );
      return {
        result: {
          text: metric.label,
          ...(metric.code ? { code: metric.code } : {}),
          ...(metric.tag ? { tag: metric.tag } : {}),
          markdown: true,
        },
        current: {
          text: currentText,
          tone: status === 'met'
            ? 'good'
            : status === 'on_track'
              ? 'accent'
              : status === 'at_risk'
                ? 'warn'
                : 'neutral',
          check: status === 'met',
        },
        target,
        updatedAt: metric.updatedAt ?? latest?.date ?? null,
      };
    }),
  };
}

/**
 * Parses a `builtin:table` view payload against the current strict contract,
 * falling back only for frozen v1 template snapshots that emitted `{ metrics }`.
 */
export function parseBuiltinTableView(raw: unknown) {
  const strict = builtinTableViewSchema.safeParse(raw);
  if (strict.success) return strict;
  const legacy = legacyTableViewSchema.safeParse(raw);
  if (!legacy.success) return strict;
  const adapted = builtinTableViewSchema.safeParse(
    legacyMetricsToTable(legacy.data.metrics, legacy.data.title, legacy.data.summary),
  );
  return adapted.success ? adapted : strict;
}

/**
 * Parses a `builtin:list` view payload against the current strict contract,
 * falling back only for frozen v1 template snapshots that emitted legacy fields
 * such as `verdict`, `triggerAt`, `assign`, `blockedReason`, `metrics`, or `expectations`.
 */
export function parseBuiltinListView(raw: unknown) {
  const strict = builtinListViewSchema.safeParse(raw);
  if (strict.success) return { ...strict, legacyMetricsTable: undefined as BuiltinTableView | undefined };
  const legacy = legacyListViewSchema.safeParse(raw);
  if (!legacy.success) return strict;
  const legacyMetricsTable = legacy.data.metrics && legacy.data.metrics.length > 0
    ? builtinTableViewSchema.safeParse(legacyMetricsToTable(legacy.data.metrics))
    : null;
  if (legacyMetricsTable && !legacyMetricsTable.success) return strict;
  const adapted = builtinListViewSchema.safeParse({
    title: legacy.data.title,
    summary: legacy.data.summary,
    groups: legacy.data.groups,
    items: legacy.data.items.map((item) => {
      const combinedSubtitle = [item.triggerAt, item.docLabel]
        .filter((part): part is string => Boolean(part))
        .join(' · ');
      const subtitle = combinedSubtitle === ''
        ? null
        : Array.from(combinedSubtitle).length <= 2048
          ? combinedSubtitle
          : (item.triggerAt ?? item.docLabel ?? null);
      const expectations = item.expectations ?? [];
      const hasActual = expectations.some((row) => Boolean(row.actual));
      const badge = item.verdict ?? item.statusLabel ?? null;
      return {
        id: item.id,
        title: item.title,
        status: item.status,
        date: item.date,
        subtitle,
        badge,
        tone: toneFromVerdict(item.verdict),
        meta: item.workerLabel ?? item.model ?? item.assign ?? null,
        sessionId: item.sessionId,
        taskKey: item.taskKey,
        group: item.group,
        summary: item.summary,
        bullets: item.bullets,
        note: item.blockedReason,
        checks: item.checks,
        table: expectations.length === 0 ? null : {
          columns: [
            { key: 'metric', label: '指标' },
            { key: 'expected', label: '事前预期' },
            ...(hasActual ? [{ key: 'actual', label: '实际兑现' }] : []),
            { key: 'status', label: '判定' },
          ],
          rows: expectations.map((row) => ({
            metric: row.label,
            expected: row.expected,
            actual: row.actual,
            status: row.status == null ? null : {
              text: LEGACY_EXPECT_LABEL[row.status],
              tone: LEGACY_VERDICT_TONE[row.status],
            },
          })),
        },
      };
    }),
  });
  return adapted.success
    ? { ...adapted, legacyMetricsTable: legacyMetricsTable?.success ? legacyMetricsTable.data : undefined }
    : strict;
}
