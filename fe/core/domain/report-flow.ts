import { z } from 'zod';

export const FLOW_OUTCOMES = Object.freeze(['none', 'ok', 'attention', 'blocked'] as const);
export const FLOW_OUTCOME_CONDITIONS = Object.freeze(['ok', 'attention', 'blocked', 'any'] as const);
export const FLOW_NODE_STATUSES = Object.freeze([
  'pending',
  'waiting',
  'running',
  'verifying',
  'done',
  'failed',
  'canceled',
] as const);
export const FLOW_DO_KINDS = Object.freeze(['run', 'wake', 'watch', 'track'] as const);
export const FLOW_WHEN_KINDS = Object.freeze(['immediate', 'after', 'cron', 'at'] as const);

export const flowOutcomeSchema = z.enum(FLOW_OUTCOMES);
export type FlowOutcome = z.infer<typeof flowOutcomeSchema>;

export const flowOutcomeConditionSchema = z.enum(FLOW_OUTCOME_CONDITIONS);
export type FlowOutcomeCondition = z.infer<typeof flowOutcomeConditionSchema>;

export const flowNodeStatusSchema = z.enum(FLOW_NODE_STATUSES);
export type FlowNodeStatus = z.infer<typeof flowNodeStatusSchema>;

export const flowDoKindSchema = z.enum(FLOW_DO_KINDS);
export type FlowDoKind = z.infer<typeof flowDoKindSchema>;

export const flowWhenKindSchema = z.enum(FLOW_WHEN_KINDS);
export type FlowWhenKind = z.infer<typeof flowWhenKindSchema>;

export const flowDependencyProjectionSchema = z.object({
  node: z.string(),
  on: flowOutcomeConditionSchema.default('ok'),
});
export type FlowDependencyProjection = z.infer<typeof flowDependencyProjectionSchema>;

export const flowGateCheckProjectionSchema = z.object({
  label: z.string(),
  status: z.enum(['pass', 'fail', 'running']),
  detail: z.string().nullish(),
  command: z.string().nullish(),
  output: z.string().nullish(),
});
export type FlowGateCheckProjection = z.infer<typeof flowGateCheckProjectionSchema>;

export const flowWatchProgressSchema = z.object({
  probeKind: z.enum(['prChecks', 'cmd']),
  label: z.string(),
  passedChecks: z.number().int().nonnegative().nullish(),
  totalChecks: z.number().int().nonnegative().nullish(),
  lastCheckedAtMs: z.number().nullish(),
  startedAtMs: z.number().nullish(),
  timeoutAtMs: z.number().nullish(),
  detail: z.string().nullish(),
});
export type FlowWatchProgress = z.infer<typeof flowWatchProgressSchema>;

export const flowNodeProjectionSchema = z.object({
  id: z.string(),
  title: z.string(),
  source: z.enum(['agent', 'user', 'template']).default('agent'),
  whenKind: flowWhenKindSchema.default('immediate'),
  after: z.array(flowDependencyProjectionSchema).default([]),
  cron: z.string().nullish(),
  tz: z.string().nullish(),
  at: z.string().nullish(),
  doKind: flowDoKindSchema.default('run'),
  role: z.string().nullish(),
  access: z.enum(['read_only', 'read_write']).nullish(),
  targetSession: z.string().nullish(),
  provider: z.string().nullish(),
  model: z.string().nullish(),
  reasoningEffort: z.string().nullish(),
  permissionMode: z.string().nullish(),
  prompt: z.string().nullish(),
  cmd: z.string().nullish(),
  gate: z.string().nullish(),
  canceled: z.boolean().default(false),
  status: flowNodeStatusSchema.default('pending'),
  outcome: flowOutcomeSchema.default('none'),
  outcomeSummary: z.string().nullish(),
  runCount: z.number().int().nonnegative().default(0),
  sessionId: z.string().nullish(),
  childTrackId: z.string().nullish(),
  startedAtMs: z.number().nullish(),
  finishedAtMs: z.number().nullish(),
  nextFireAtMs: z.number().nullish(),
  lastFiredAtMs: z.number().nullish(),
  firedCount: z.number().int().nonnegative().default(0),
  waitingOn: z.array(z.string()).default([]),
  gates: z.array(flowGateCheckProjectionSchema).default([]),
  watchProgress: flowWatchProgressSchema.nullish(),
  repeat: z.boolean().default(false),
});
export type FlowNodeProjection = z.infer<typeof flowNodeProjectionSchema>;

export const flowArchivedNodeProjectionSchema = z.object({
  id: z.string(),
  title: z.string(),
  doKind: flowDoKindSchema.default('run'),
  status: flowNodeStatusSchema.default('done'),
  outcome: flowOutcomeSchema.default('none'),
  outcomeSummary: z.string().nullish(),
  runCount: z.number().int().nonnegative().default(0),
  sessionId: z.string().nullish(),
  childTrackId: z.string().nullish(),
  model: z.string().nullish(),
  finishedAtMs: z.number().nullish(),
  archivedAtMs: z.number(),
});
export type FlowArchivedNodeProjection = z.infer<typeof flowArchivedNodeProjectionSchema>;

export const flowProjectionSchema = z.object({
  rev: z.number().int().nonnegative().default(0),
  filePath: z.string().nullish(),
  nodes: z.array(flowNodeProjectionSchema).default([]),
  archived: z.array(flowArchivedNodeProjectionSchema).default([]),
});
export type FlowProjection = z.infer<typeof flowProjectionSchema>;

export const flowPlanDiffSchema = z.object({
  added: z.array(z.string()).default([]),
  updated: z.array(z.string()).default([]),
  canceled: z.array(z.string()).default([]),
  archived: z.array(z.string()).default([]),
  unchanged: z.array(z.string()).default([]),
});
export type FlowPlanDiff = z.infer<typeof flowPlanDiffSchema>;

export const flowValidationErrorSchema = z.object({
  path: z.string(),
  message: z.string(),
  suggestion: z.string().nullish(),
});
export type FlowValidationError = z.infer<typeof flowValidationErrorSchema>;

export const flowCommitResultSchema = z.object({
  ok: z.boolean(),
  dryRun: z.boolean(),
  flowRev: z.number().int().nonnegative(),
  plan: flowPlanDiffSchema,
  diagnostics: z.array(flowValidationErrorSchema).default([]),
  flow: flowProjectionSchema,
});
export type FlowCommitResult = z.infer<typeof flowCommitResultSchema>;
