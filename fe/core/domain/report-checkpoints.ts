import { z } from 'zod';
import { max2048CodePoints } from './report-table.js';

export const CHECKPOINT_STATUSES = Object.freeze(['done', 'running', 'pending', 'blocked'] as const);
export type CheckpointStatus = (typeof CHECKPOINT_STATUSES)[number];

export const CHECKPOINT_CHECK_STATUSES = Object.freeze(['pass', 'running', 'fail', 'pending'] as const);
export type CheckpointCheckStatus = (typeof CHECKPOINT_CHECK_STATUSES)[number];

export const CHECKPOINT_ASSIGN_KINDS = Object.freeze(['model', 'command'] as const);
export type CheckpointAssignKind = (typeof CHECKPOINT_ASSIGN_KINDS)[number];

export const CHECKPOINT_LAYOUTS = Object.freeze(['stepper', 'chronicle'] as const);
export type CheckpointLayout = (typeof CHECKPOINT_LAYOUTS)[number];

export const CHECKPOINT_EXPECT_STATUSES = Object.freeze(['pending', 'beat', 'inline', 'miss'] as const);
export type CheckpointExpectStatus = (typeof CHECKPOINT_EXPECT_STATUSES)[number];

export const CHECKPOINT_METRIC_DIRECTIONS = Object.freeze(['lte', 'gte'] as const);
export type CheckpointMetricDirection = (typeof CHECKPOINT_METRIC_DIRECTIONS)[number];

export const CHECKPOINT_METRIC_STATUSES = Object.freeze(['met', 'on_track', 'at_risk', 'pending'] as const);
export type CheckpointMetricStatus = (typeof CHECKPOINT_METRIC_STATUSES)[number];

const checkpointRuleSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  text: max2048CodePoints(z.string().min(1)),
});

const checkpointCheckSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  status: z.enum(CHECKPOINT_CHECK_STATUSES),
  detail: max2048CodePoints(z.string()).nullish(),
  command: max2048CodePoints(z.string()).nullish(),
  output: max2048CodePoints(z.string()).nullish(),
});

const checkpointTriggerSchema = z.strictObject({
  trigger: max2048CodePoints(z.string().min(1)),
  action: max2048CodePoints(z.string().min(1)),
  assignKind: z.enum(CHECKPOINT_ASSIGN_KINDS).nullish(),
  assign: max2048CodePoints(z.string().min(1)),
});

const checkpointExpectationSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  expected: max2048CodePoints(z.string().min(1)),
  actual: max2048CodePoints(z.string()).nullish(),
  status: z.enum(CHECKPOINT_EXPECT_STATUSES).nullish(),
});

const checkpointRangePointSchema = z.strictObject({
  value: z.number().finite(),
  display: max2048CodePoints(z.string().min(1)).nullish(),
  note: max2048CodePoints(z.string().min(1)),
  emphasis: z.boolean().nullish(),
});

const checkpointRangeSchema = z.strictObject({
  unit: max2048CodePoints(z.string()).nullish(),
  current: z.strictObject({
    value: z.number().finite(),
    display: max2048CodePoints(z.string().min(1)).nullish(),
    label: max2048CodePoints(z.string()).nullish(),
    note: max2048CodePoints(z.string()).nullish(),
  }).nullish(),
  points: z.array(checkpointRangePointSchema).min(2).max(8),
});

const checkpointMetricPointSchema = z.strictObject({
  date: max2048CodePoints(z.string().min(1)),
  value: z.number().finite(),
  note: max2048CodePoints(z.string()).nullish(),
});

export const checkpointMetricSchema = z.strictObject({
  code: max2048CodePoints(z.string().min(1)).nullish(),
  label: max2048CodePoints(z.string().min(1)),
  tag: max2048CodePoints(z.string()).nullish(),
  current: max2048CodePoints(z.string()).nullish(),
  status: z.enum(CHECKPOINT_METRIC_STATUSES).nullish(),
  updatedAt: max2048CodePoints(z.string()).nullish(),
  evidence: max2048CodePoints(z.string()).nullish(),
  unit: max2048CodePoints(z.string()).nullish(),
  target: z.number().finite().nullish(),
  direction: z.enum(CHECKPOINT_METRIC_DIRECTIONS).nullish(),
  targetLabel: max2048CodePoints(z.string()).nullish(),
  points: z.array(checkpointMetricPointSchema).min(2).max(90).nullish(),
});

export const checkpointItemSchema = z.strictObject({
  id: z.string().min(1).max(120),
  title: max2048CodePoints(z.string().min(1)),
  status: z.enum(CHECKPOINT_STATUSES),
  statusLabel: max2048CodePoints(z.string()).nullish(),
  group: max2048CodePoints(z.string()).nullish(),
  date: max2048CodePoints(z.string()).nullish(),
  category: max2048CodePoints(z.string()).nullish(),
  verdict: max2048CodePoints(z.string()).nullish(),
  triggerAt: max2048CodePoints(z.string()).nullish(),
  assignKind: z.enum(CHECKPOINT_ASSIGN_KINDS).nullish(),
  assign: max2048CodePoints(z.string()).nullish(),
  role: z.string().max(64).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  bullets: z.array(max2048CodePoints(z.string().min(1))).max(12).nullish(),
  docPath: max2048CodePoints(z.string().min(1)).nullish(),
  docLabel: max2048CodePoints(z.string().min(1)).nullish(),
  sessionId: z.string().min(1).max(160).nullish(),
  taskKey: z.string().min(1).max(160).nullish(),
  workerLabel: max2048CodePoints(z.string().min(1)).nullish(),
  model: z.string().min(1).max(160).nullish(),
  ruleBadge: max2048CodePoints(z.string().min(1)).nullish(),
  dependsOn: z.array(max2048CodePoints(z.string().min(1))).max(16).nullish(),
  constraint: max2048CodePoints(z.string().min(1)).nullish(),
  blockedReason: max2048CodePoints(z.string().min(1)).nullish(),
  expectations: z.array(checkpointExpectationSchema).max(12).nullish(),
  checks: z.array(checkpointCheckSchema).max(16).nullish(),
  triggers: z.array(checkpointTriggerSchema).max(16).nullish(),
});

export const checkpointsBlockPayloadSchema = z.strictObject({
  title: max2048CodePoints(z.string()).nullish(),
  layout: z.enum(CHECKPOINT_LAYOUTS).nullish(),
  tier: max2048CodePoints(z.string()).nullish(),
  range: checkpointRangeSchema.nullish(),
  metrics: z.array(checkpointMetricSchema).min(1).max(12).nullish(),
  rules: z.array(checkpointRuleSchema).max(16).nullish(),
  triggers: z.array(checkpointTriggerSchema).max(16).nullish(),
  items: z.array(checkpointItemSchema).min(1).max(100),
});

export type CheckpointRule = z.infer<typeof checkpointRuleSchema>;
export type CheckpointCheck = z.infer<typeof checkpointCheckSchema>;
export type CheckpointTrigger = z.infer<typeof checkpointTriggerSchema>;
export type CheckpointExpectation = z.infer<typeof checkpointExpectationSchema>;
export type CheckpointRangePoint = z.infer<typeof checkpointRangePointSchema>;
export type CheckpointRange = z.infer<typeof checkpointRangeSchema>;
export type CheckpointMetricPoint = z.infer<typeof checkpointMetricPointSchema>;
export type CheckpointMetric = z.infer<typeof checkpointMetricSchema>;
export type CheckpointItem = z.infer<typeof checkpointItemSchema>;
export type CheckpointsBlockPayload = z.infer<typeof checkpointsBlockPayloadSchema>;
