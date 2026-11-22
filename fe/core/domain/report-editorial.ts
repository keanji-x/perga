import { z } from 'zod';
export function max2048CodePoints(schema: z.ZodString) {
  return schema.refine(value => Array.from(value).length <= 2048, 'Must contain at most 2048 Unicode code points');
}

export const EDITORIAL_STATUSES = Object.freeze(['done', 'running', 'pending', 'blocked'] as const);
export type EditorialListStatus = (typeof EDITORIAL_STATUSES)[number];

export const EDITORIAL_CHECK_STATUSES = Object.freeze(['pass', 'running', 'fail', 'pending'] as const);
export type EditorialCheckStatus = (typeof EDITORIAL_CHECK_STATUSES)[number];

export const EDITORIAL_ASSIGN_KINDS = Object.freeze(['model', 'command'] as const);
export type EditorialAssignKind = (typeof EDITORIAL_ASSIGN_KINDS)[number];

export const EDITORIAL_EXPECT_STATUSES = Object.freeze(['pending', 'beat', 'inline', 'miss'] as const);
export type EditorialExpectationStatus = (typeof EDITORIAL_EXPECT_STATUSES)[number];

export const EDITORIAL_METRIC_DIRECTIONS = Object.freeze(['lte', 'gte'] as const);
export type EditorialMetricDirection = (typeof EDITORIAL_METRIC_DIRECTIONS)[number];

export const EDITORIAL_METRIC_STATUSES = Object.freeze(['met', 'on_track', 'at_risk', 'pending'] as const);
export type EditorialMetricStatus = (typeof EDITORIAL_METRIC_STATUSES)[number];

const editorialCheckSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_CHECK_STATUSES),
  detail: max2048CodePoints(z.string()).nullish(),
  command: max2048CodePoints(z.string()).nullish(),
  output: max2048CodePoints(z.string()).nullish(),
});

const editorialExpectationSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  expected: max2048CodePoints(z.string().min(1)),
  actual: max2048CodePoints(z.string()).nullish(),
  status: z.enum(EDITORIAL_EXPECT_STATUSES).nullish(),
});

const editorialMetricPointSchema = z.strictObject({
  date: max2048CodePoints(z.string().min(1)),
  value: z.number().finite(),
  note: max2048CodePoints(z.string()).nullish(),
});

export const editorialMetricSchema = z.strictObject({
  code: max2048CodePoints(z.string().min(1)).nullish(),
  label: max2048CodePoints(z.string().min(1)),
  tag: max2048CodePoints(z.string()).nullish(),
  current: max2048CodePoints(z.string()).nullish(),
  status: z.enum(EDITORIAL_METRIC_STATUSES).nullish(),
  updatedAt: max2048CodePoints(z.string()).nullish(),
  evidence: max2048CodePoints(z.string()).nullish(),
  unit: max2048CodePoints(z.string()).nullish(),
  target: z.number().finite().nullish(),
  direction: z.enum(EDITORIAL_METRIC_DIRECTIONS).nullish(),
  targetLabel: max2048CodePoints(z.string()).nullish(),
  points: z.array(editorialMetricPointSchema).min(2).max(90).nullish(),
});

export const editorialItemSchema = z.strictObject({
  id: z.string().min(1).max(120),
  title: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_STATUSES),
  statusLabel: max2048CodePoints(z.string()).nullish(),
  group: max2048CodePoints(z.string()).nullish(),
  date: max2048CodePoints(z.string()).nullish(),
  verdict: max2048CodePoints(z.string()).nullish(),
  triggerAt: max2048CodePoints(z.string()).nullish(),
  assignKind: z.enum(EDITORIAL_ASSIGN_KINDS).nullish(),
  assign: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  bullets: z.array(max2048CodePoints(z.string().min(1))).max(12).nullish(),
  docLabel: max2048CodePoints(z.string().min(1)).nullish(),
  sessionId: z.string().min(1).max(160).nullish(),
  taskKey: z.string().min(1).max(160).nullish(),
  workerLabel: max2048CodePoints(z.string().min(1)).nullish(),
  model: z.string().min(1).max(160).nullish(),
  blockedReason: max2048CodePoints(z.string().min(1)).nullish(),
  expectations: z.array(editorialExpectationSchema).max(12).nullish(),
  checks: z.array(editorialCheckSchema).max(16).nullish(),
});

export type EditorialCheck = z.infer<typeof editorialCheckSchema>;
export type EditorialExpectation = z.infer<typeof editorialExpectationSchema>;
export type EditorialMetricPoint = z.infer<typeof editorialMetricPointSchema>;
export type EditorialMetric = z.infer<typeof editorialMetricSchema>;
export type EditorialListItem = z.infer<typeof editorialItemSchema>;
