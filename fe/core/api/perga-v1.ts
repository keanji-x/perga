// Perga /api/v1 REST and WebSocket wire contracts, 1:1 with Perga.Contracts, Perga.ReportDoc, and Perga.Kernel.
// JSON property names are camelCase; enum strings are snake_case.

import { z } from 'zod';

import type { ApiDecodeFailure } from './types.js';

export const systemInfoDtoSchema = z.object({
  releaseId: z.string(),
  webBuildId: z.string(),
  hostBuildId: z.string(),
  bridgeBuildId: z.string(),
  schemaVersion: z.number().int().nonnegative(),
  draining: z.boolean(),
  activeDispatches: z.number().int().nonnegative(),
});
export type SystemInfoDto = z.infer<typeof systemInfoDtoSchema>;
export const systemDrainRequestSchema = z.object({ draining: z.boolean() });
export const systemDrainResponseSchema = z.object({
  draining: z.boolean(), activeDispatches: z.number().int().nonnegative(),
});
export const systemReloadRequestSchema = z.object({ releaseId: z.string(), webBuildId: z.string() });
export const systemUpgradeReceiptSchema = z.object({
  jobId: z.string(), oldReleaseId: z.string().nullable(), newReleaseId: z.string().nullable(),
  status: z.string(), error: z.string().nullable(), rollbackError: z.string().nullable(),
  notifyTrackId: z.string().nullable(), finishedAt: z.number(), changed: z.array(z.string()),
});
export type SystemUpgradeReceipt = z.infer<typeof systemUpgradeReceiptSchema>;
export const systemUpgradeStatusPayloadSchema = z.object({
  draining: z.boolean(), activeDispatches: z.number().int().nonnegative(),
  receipt: systemUpgradeReceiptSchema.nullish(),
});

export type SystemUpgradeStatusPayload = z.infer<typeof systemUpgradeStatusPayloadSchema>;

export const accessModeSchema = z.enum(['read_only', 'read_write']);
export type AccessMode = z.infer<typeof accessModeSchema>;

export const taskRunStatusSchema = z.enum([
  'draft',
  'pending',
  'running',
  'verifying',
  'done',
  'failed',
  'canceled',
]);
export type TaskRunStatus = z.infer<typeof taskRunStatusSchema>;

export const semanticVerdictSchema = z.enum(['none', 'accepted', 'rejected']);
export type SemanticVerdict = z.infer<typeof semanticVerdictSchema>;

export const reportBlockKindSchema = z.enum(['prose', 'task', 'html', 'table', 'view']);
export type ReportBlockKind = z.infer<typeof reportBlockKindSchema>;

export const askStatusSchema = z.enum(['open', 'answered', 'withdrawn']);
export type AskStatus = z.infer<typeof askStatusSchema>;

export const terminalStatusSchema = z.enum(['running', 'exited']);
export type TerminalStatus = z.infer<typeof terminalStatusSchema>;

export const providerModelEffortDtoSchema = z.object({
  reasoningEffort: z.string(),
  description: z.string().nullish().transform((v) => v ?? ''),
});
export type ProviderModelEffortDto = z.infer<typeof providerModelEffortDtoSchema>;

export const providerModelInfoDtoSchema = z.object({
  model: z.string(),
  displayName: z.string(),
  description: z.string().nullish().transform((v) => v ?? ''),
  isDefault: z.boolean().default(false),
  defaultReasoningEffort: z.string().nullish().transform((v) => v ?? null),
  supportedReasoningEfforts: z.array(providerModelEffortDtoSchema).nullish().transform((v) => v ?? []),
});
export type ProviderModelInfoDto = z.infer<typeof providerModelInfoDtoSchema>;
export const permissionModeOptionDtoSchema = z.object({
  id: z.string(),
  label: z.string(),
  description: z.string(),
  tone: z.enum(['neutral', 'warning', 'danger']).optional().default('neutral'),
  isDefault: z.boolean().optional().default(false),
});
export type PermissionModeOptionDto = z.infer<typeof permissionModeOptionDtoSchema>;

export const providerStatusDtoSchema = z.object({
  provider: z.string(),
  available: z.boolean(),
  version: z.string().nullish(),
  reason: z.string().nullish(),
  models: z.array(z.string()).nullish().transform((v) => v ?? []),
  modelCatalog: z.array(providerModelInfoDtoSchema).nullish().transform((v) => v ?? []),
  permissionModes: z.array(permissionModeOptionDtoSchema).nullish().transform((v) => v ?? []),
});
export type ProviderStatusDto = z.infer<typeof providerStatusDtoSchema>;

export const sectionRuleDtoSchema = z.object({
  h1: z.string(),
  omitIfEmpty: z.boolean().nullish().transform((v) => v ?? false),
  editableBy: z.array(z.string()).nullish(),
});
export type SectionRuleDto = z.infer<typeof sectionRuleDtoSchema>;

export const scheduleSpecDtoSchema = z.object({
  id: z.string(),
  cron: z.string(),
  timezone: z.string(),
  staticWakePrompt: z.string().nullish(),
});
export type ScheduleSpecDto = z.infer<typeof scheduleSpecDtoSchema>;

export const compiledTemplateManifestDtoSchema = z.object({
  id: z.string(),
  title: z.string().optional(),
  name: z.string().optional(),
  description: z.string().nullish().transform((v) => v ?? null),
  defaultPlugins: z.array(z.string()).nullish().transform((v) => v ?? []),
  inputsSchema: z.unknown().default({ type: 'object', properties: {} }),
  sections: z.array(sectionRuleDtoSchema).nullish().transform((v) => v ?? []),
  maxProseChars: z.number().int().nullish(),
  requireGateForReadWrite: z.boolean().nullish().transform((v) => v ?? false),
  workspaceDriver: z.string().nullish(),
  workspaceLowerCacheDirs: z.array(z.string()).nullish().transform((v) => v ?? []),
  workspaceCreateCmd: z.string().nullish(),
  workspaceCloseCmd: z.string().nullish(),
  afterWorkerDoneCmd: z.string().nullish(),
  schedules: z.array(scheduleSpecDtoSchema).nullish().transform((v) => v ?? []),
  initialReportMarkdown: z.string().nullish().transform((v) => v ?? ''),
}).transform((raw) => ({
  ...raw,
  title: raw.title ?? raw.name ?? raw.id,
}));
export type CompiledTemplateManifestDto = z.infer<typeof compiledTemplateManifestDtoSchema>;

export const areaDtoSchema = z.object({
  id: z.string(),
  name: z.string(),
  color: z.string(),
  defaultCwd: z.string().nullish(),
  folders: z.array(z.string()).default([]),
  sortOrder: z.number().default(0),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
});
export type AreaDto = z.infer<typeof areaDtoSchema>;

export const bootstrapDtoSchema = z.object({
  version: z.string(),
  databaseId: z.string().nullish(),
  dbInstanceId: z.string().nullish(),
  authRequired: z.boolean().nullish(),
  providers: z.array(providerStatusDtoSchema).default([]),
  templates: z.array(compiledTemplateManifestDtoSchema).default([]),
  areas: z.array(areaDtoSchema).default([]),
});
export type BootstrapDto = z.infer<typeof bootstrapDtoSchema>;

export const trackDtoSchema = z.object({
  id: z.string(),
  areaId: z.string(),
  parentTrackId: z.string().nullish(),
  parentAttemptId: z.string().nullish(),
  title: z.string().nullish().transform((v) => v ?? ''),
  cwd: z.string().nullish().transform((v) => v ?? ''),
  workspaceDriver: z.string().default('direct'),
  workspaceSpec: z.unknown().default({}),
  leadSessionId: z.string().nullish(),
  templateId: z.string().nullish(),
  templateInputs: z.unknown().default({}),
  templateState: z.unknown().default({}),
  enabledPlugins: z.array(z.string()).default([]),
  closedAtMs: z.number().nullish(),
  pinnedAtMs: z.number().nullish(),
  sortOrder: z.number().default(0),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
});
export type TrackDto = z.infer<typeof trackDtoSchema>;

export const gateStepSpecDtoSchema = z.object({
  name: z.string(),
  cmd: z.string(),
});
export type GateStepSpecDto = z.infer<typeof gateStepSpecDtoSchema>;

export const gateSpecDtoSchema = z.object({
  cwd: z.string().nullish(),
  timeoutSecs: z.number().nullish(),
  steps: z.array(gateStepSpecDtoSchema).default([]),
});
export type GateSpecDto = z.infer<typeof gateSpecDtoSchema>;

export const gateStepOutcomeDtoSchema = z.object({
  name: z.string(),
  cmd: z.string().nullish(),
  exitCode: z.number(),
  passed: z.boolean().optional(),
  durationMs: z.number().default(0),
  stdout: z.string().nullish(),
  stderr: z.string().nullish(),
  outputTail: z.string().nullish().transform((v) => v ?? ''),
});
export type GateStepOutcomeDto = z.infer<typeof gateStepOutcomeDtoSchema>;

export const gateVerdictDtoSchema = z.object({
  passed: z.boolean(),
  steps: z.array(gateStepOutcomeDtoSchema).default([]),
});
export type GateVerdictDto = z.infer<typeof gateVerdictDtoSchema>;

export const taskDtoSchema = z.object({
  id: z.string(),
  trackId: z.string(),
  blockId: z.string(),
  taskKey: z.string(),
  generation: z.number().int().default(1),
  kind: z.string(),
  provider: z.string().nullish(),
  access: accessModeSchema.optional(),
  accessMode: accessModeSchema.optional(),
  ready: z.boolean().default(false),
  dependsOn: z.array(z.string()).nullish().transform((v) => v ?? []),
  goalOrCmd: z.string().nullish().transform((v) => v ?? ''),
  context: z.unknown().nullish(),
  acceptance: z.string().nullish(),
  gate: gateSpecDtoSchema.nullish(),
  status: taskRunStatusSchema.or(z.string()),
  statusDetail: z.string().nullish(),
  semanticVerdict: semanticVerdictSchema.default('none'),
  semanticNote: z.string().nullish(),
  latestAttemptId: z.string().nullish(),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
}).transform((raw) => ({
  ...raw,
  access: raw.access ?? raw.accessMode ?? 'read_write',
}));
export type TaskDto = z.infer<typeof taskDtoSchema>;

export const workerDispatchReceiptSchema = z.object({
  task: taskDtoSchema, sessionId: z.string().nullish(), requestKey: z.string(),
});
export type WorkerDispatchReceipt = z.infer<typeof workerDispatchReceiptSchema>;

export const taskAttemptDtoSchema = z.object({
  attemptId: z.string(),
  taskId: z.string(),
  trackId: z.string(),
  sessionId: z.string().nullish(),
  status: z.string(),
  workerResult: z.unknown().nullish(),
  meta: z.unknown().nullish(),
  workspaceLease: z.unknown().nullish(),
  gateVerdict: z.unknown().nullish(),
  startedAtMs: z.number(),
  finishedAtMs: z.number().nullish(),
});
export type TaskAttemptDto = z.infer<typeof taskAttemptDtoSchema>;

export const reportBlockDtoSchema = z.object({
  id: z.string(),
  rev: z.number().int().default(1),
  kind: reportBlockKindSchema.or(z.string()),
  sectionH1: z.string().nullish(),
  markdown: z.string().nullish().transform((v) => v ?? ''),
  payload: z.unknown().nullish(),
});
export type ReportBlockDto = z.infer<typeof reportBlockDtoSchema>;

export const reportDocumentDtoSchema = z.object({
  docRev: z.number().int().default(0),
  summary: z.string().nullish().transform((v) => v ?? ''),
  leadingComment: z.string().nullish().transform((v) => v ?? ''),
  blocks: z.array(reportBlockDtoSchema).nullish().transform((v) => v ?? []),
});
export type ReportDocumentDto = z.infer<typeof reportDocumentDtoSchema>;

export const readAnchorDtoSchema = z.object({
  docRev: z.number().int(),
  knownBlockRevs: z.record(z.string(), z.number().int()).default({}),
  wholeDocumentRead: z.boolean().default(true),
});
export type ReadAnchorDto = z.infer<typeof readAnchorDtoSchema>;

export const validationDiagnosticDtoSchema = z.object({
  severity: z.string().default('error'),
  blockId: z.string().nullish(),
  code: z.string(),
  message: z.string(),
});
export type ValidationDiagnosticDto = z.infer<typeof validationDiagnosticDtoSchema>;

export const reportReadResultDtoSchema = z.object({
  docRev: z.number().int().default(0),
  summary: z.string().nullish().transform((v) => v ?? ''),
  renderedMarkdown: z.string().nullish().transform((v) => v ?? ''),
  blocks: z.array(reportBlockDtoSchema).nullish().transform((v) => v ?? []),
  tasks: z.array(taskDtoSchema).nullish().transform((v) => v ?? []),
  anchor: readAnchorDtoSchema.or(z.string()).nullish(),
  diagnostics: z.array(validationDiagnosticDtoSchema).nullish().transform((v) => v ?? []),
});
export type ReportReadResultDto = z.infer<typeof reportReadResultDtoSchema>;

export const updateSessionModelRequestSchema = z.object({
  model: z.string().nullable().optional(),
  reasoningEffort: z.string().nullable().optional(),
});
export type UpdateSessionModelRequest = z.infer<typeof updateSessionModelRequestSchema>;

export const agentSessionDtoSchema = z.object({
  id: z.string(),
  trackId: z.string(),
  isLead: z.boolean().default(false),
  boundTaskId: z.string().nullish(),
  boundAttemptId: z.string().nullish(),
  title: z.string().nullish(),
  provider: z.string(),
  model: z.string().nullish(),
  reasoningEffort: z.string().nullish(),
  nativeThreadId: z.string().nullish(),
  epoch: z.number().int().default(1),
  status: z.string().default('idle'),
  contextUsage: z.object({
    usedTokens: z.number().int().nonnegative(),
    windowTokens: z.number().int().positive().nullable(),
    observedAtMs: z.number().int(),
    ratio: z.number().min(0).max(1).nullable(),
  }).nullable(),
  archivedAtMs: z.number().nullish(),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
  permissionMode: z.string().nullish(),
});
export type AgentSessionDto = z.infer<typeof agentSessionDtoSchema>;

export const sessionTurnDtoSchema = z.object({
  id: z.string(),
  sessionId: z.string(),
  turnIndex: z.number().int().default(0),
  epoch: z.number().int().default(1),
  triggerKind: z.string().default('user'),
  input: z.unknown().nullish(),
  userItemId: z.number().int().nullish(),
  status: z.string(),
  usage: z.unknown().nullish(),
  errorText: z.string().nullish(),
  startedAtMs: z.number().nullable(),
  completedAtMs: z.number().nullish(),
});
export type SessionTurnDto = z.infer<typeof sessionTurnDtoSchema>;

export const sessionInputReceiptSchema = z.object({
  inputId: z.string(),
  messageId: z.string().optional(),
  turnId: z.string(),
  sessionId: z.string(),
  deliveryState: z.enum(['accepted', 'dispatching', 'delivered', 'rejected', 'unknown']),
  turnStatus: z.enum(['queued', 'running', 'completed', 'failed', 'interrupted', 'unknown']),
  statusUrl: z.string(),
  resolvedAtMs: z.number().nullish(),
});
export type SessionInputReceipt = z.infer<typeof sessionInputReceiptSchema>;

export const sessionCreationReceiptSchema = z.object({
  session: agentSessionDtoSchema,
  firstInput: sessionInputReceiptSchema.nullish(),
}).refine(result => result.firstInput == null || result.firstInput.sessionId === result.session.id);

export type SessionCreationReceipt = z.infer<typeof sessionCreationReceiptSchema>;

export const sessionItemDtoSchema = z.object({
  id: z.number().int(),
  sessionId: z.string(),
  turnId: z.string().nullish().transform((v) => v ?? ''),
  kind: z.enum(['user', 'thinking', 'assistant_text', 'tool_call', 'tool_result', 'turn_outcome', 'turn_error']).or(z.string()),
  payload: z.unknown(),
  createdAtMs: z.number(),
  messageStatus: z.enum(['queued', 'sending', 'processing', 'completed', 'failed', 'unconfirmed']).nullish(),
});
export type SessionItemDto = z.infer<typeof sessionItemDtoSchema>;

export const workerSettingsSchema = z.object({
  version: z.number().int(), model: z.string().nullish(), reasoningEffort: z.string().nullish(),
  approvalMode: z.enum(['ask', 'deny']),
});
export type WorkerSettings = z.infer<typeof workerSettingsSchema>;

const workerProviderConfigurationSchema = z.object({
  models: z.array(z.object({ id: z.string(), name: z.string(), reasoningEfforts: z.array(z.string()),
    defaultReasoningEffort: z.string().nullish() })),
  defaultModel: z.string().nullish(),
  defaultReasoningEffort: z.string().nullish(),
  sessionReasoningEfforts: z.array(z.string()).nullish(),
});

export const workerActivitySchema = z.object({
  task: taskDtoSchema,
  attemptId: z.string(),
  phase: z.string(),
  nativeSteer: z.boolean(),
  openAsks: z.array(z.lazy(() => askDtoSchema)),
  sequence: z.number(),
  reset: z.boolean(),
  text: z.string(),
  textTruncated: z.boolean(),
  nextSettings: workerSettingsSchema,
  activeSettings: workerSettingsSchema.nullish(),
  canChangeModel: z.boolean(),
  reasoningEfforts: z.array(z.string()),
  providerConfiguration: workerProviderConfigurationSchema.nullish(),
  approvalAuthorizationConfigured: z.boolean().optional(),
  lastError: z.string().nullish(),
  events: z.array(z.object({ sequence: z.number(), kind: z.string(), payload: z.unknown(), createdAtMs: z.number() })),
  messages: z.array(z.object({ id: z.string(), author: z.string(), content: z.string(),
    turnId: z.string().nullish(), delivery: z.string(), createdAtMs: z.number() })),
});
export type WorkerActivity = z.infer<typeof workerActivitySchema>;
export const providerSessionResumeSchema = z.object({
  turnId: z.string().min(1),
  state: z.enum(['available', 'resuming', 'paused']),
  canStartFresh: z.boolean().optional(),
});
export type ProviderSessionResume = z.infer<typeof providerSessionResumeSchema>;

export const sessionItemsPageDtoSchema = z.object({
  items: z.array(sessionItemDtoSchema).nullish().transform((v) => v ?? []),
  activeTurn: sessionTurnDtoSchema.nullish(),
  unknownInput: sessionInputReceiptSchema.nullable(),
  queuedMessages: z.array(z.object({ id: z.string(), text: z.string(), queuedAtMs: z.number(),
    state: z.enum(['waiting', 'steering', 'sending', 'processing', 'unconfirmed']) })).optional(),
  canSteer: z.boolean().optional(),
  queueHandoffIds: z.array(z.string()).optional(),
  resume: providerSessionResumeSchema.nullish(),
  session: agentSessionDtoSchema,
  hasMoreBefore: z.boolean().optional(),
  worker: workerActivitySchema.nullish(),
});
export type SessionItemsPageDto = z.infer<typeof sessionItemsPageDtoSchema>;

export const askQuestionDtoSchema = z.object({
  question: z.string(),
  options: z.array(z.string()).nullish().transform((v) => v ?? []),
  isMultiSelect: z.boolean().nullish().transform((v) => v ?? false),
});
export type AskQuestionDto = z.infer<typeof askQuestionDtoSchema>;

export const askDtoSchema = z.object({
  delivery: z.enum(['wake', 'hold']),
  sessionId: z.string().nullish(),
  turnId: z.string().nullish(),
  id: z.string(),
  trackId: z.string(),
  status: askStatusSchema.or(z.string()),
  questions: z.unknown(),
  answer: z.unknown().nullish(),
  createdAtMs: z.number(),
  answeredAtMs: z.number().nullish(),
});
export type AskDto = z.infer<typeof askDtoSchema>;

export const mailDtoSchema = z.object({
  id: z.string(),
  replyToMailId: z.string().nullish(),
  fromTrackId: z.string(),
  toTrackId: z.string(),
  summary: z.string(),
  body: z.string(),
  readAtMs: z.number().nullish(),
  createdAtMs: z.number(),
});
export type MailDto = z.infer<typeof mailDtoSchema>;

export const terminalDtoSchema = z.object({
  id: z.string(),
  trackId: z.string(),
  attemptId: z.string().nullish(),
  idempotencyKey: z.string().nullish(),
  title: z.string().nullish().transform((v) => v ?? 'Terminal'),
  program: z.string().nullish(),
  cwd: z.string().nullish().transform((v) => v ?? ''),
  status: terminalStatusSchema.or(z.string()),
  exitCode: z.number().int().nullish(),
  createdAtMs: z.number(),
  exitedAtMs: z.number().nullish(),
});
export type TerminalDto = z.infer<typeof terminalDtoSchema>;

export const trackAggregateSnapshotDtoSchema = z.object({
  track: trackDtoSchema,
  report: reportDocumentDtoSchema,
  tasks: z.array(taskDtoSchema).default([]),
  sessions: z.array(agentSessionDtoSchema).default([]),
  openAsks: z.array(askDtoSchema).default([]),
  templateSnapshotSource: z.string().nullish(),
  workspacePreparation: z.enum(['pending', 'failed', 'unknown']).nullish(),
});
export type TrackAggregateSnapshotDto = z.infer<typeof trackAggregateSnapshotDtoSchema>;

export const trackWorkspaceOptionsDtoSchema = z.object({
  defaultId: z.string().min(1).nullable(),
  options: z.array(z.object({ id: z.string().min(1), label: z.string(), description: z.string() })).min(1),
}).refine((result) => result.defaultId === null || result.options.some((option) => option.id === result.defaultId));
export type TrackWorkspaceOptionsDto = z.infer<typeof trackWorkspaceOptionsDtoSchema>;

export const hostDirEntryDtoSchema = z.object({
  name: z.string(),
  isDir: z.boolean().optional(),
  is_dir: z.boolean().optional(),
  isSymlink: z.boolean().optional(),
  is_symlink: z.boolean().optional(),
}).transform((raw) => ({
  name: raw.name,
  isDir: raw.isDir ?? raw.is_dir ?? false,
  isSymlink: raw.isSymlink ?? raw.is_symlink ?? false,
}));
export type HostDirEntryDto = z.infer<typeof hostDirEntryDtoSchema>;

export const hostDirListDtoSchema = z.object({
  path: z.string(),
  parent: z.string().nullable().default(null),
  entries: z.array(hostDirEntryDtoSchema).default([]),
});
export type HostDirListDto = z.infer<typeof hostDirListDtoSchema>;

export const workspaceFileEntryDtoSchema = z.object({
  name: z.string(),
  relativePath: z.string(),
  isDirectory: z.boolean(),
  sizeBytes: z.number().default(0),
});
export type WorkspaceFileEntryDto = z.infer<typeof workspaceFileEntryDtoSchema>;

export const workspaceFileResultDtoSchema = z.object({
  relativePath: z.string(),
  isDirectory: z.boolean(),
  isBinary: z.boolean().default(false),
  content: z.string().nullish(),
  truncated: z.boolean().default(false),
  entries: z.array(workspaceFileEntryDtoSchema).nullish(),
});
export type WorkspaceFileResultDto = z.infer<typeof workspaceFileResultDtoSchema>;

export const vcsDiffFileDtoSchema = z.object({
  path: z.string(),
  oldPath: z.string().nullish(),
  status: z.string().default('modified'),
  patch: z.string().nullish(),
  headText: z.string().nullish(),
  workingText: z.string().nullish(),
  isBinary: z.boolean().default(false),
});
export type VcsDiffFileDto = z.infer<typeof vcsDiffFileDtoSchema>;

export const diffSummaryDtoSchema = z.object({
  diffText: z.string().default(''),
  truncated: z.boolean().default(false),
  changedFiles: z.array(z.string()).default([]),
  files: z.array(vcsDiffFileDtoSchema).optional(),
});
export type DiffSummaryDto = z.infer<typeof diffSummaryDtoSchema>;

export const trackOverlayItemDtoSchema = z.object({
  trackId: z.string(),
  areaId: z.string().nullish(),
  isWorking: z.boolean().default(false),
  openAskCount: z.number().int().default(0),
  hasFailedTask: z.boolean().default(false),
  unreadMailCount: z.number().int().default(0),
  updatedAtMs: z.number().default(0),
});
export type TrackOverlayItemDto = z.infer<typeof trackOverlayItemDtoSchema>;

export const overlaysListDtoSchema = z.object({
  tracks: z.array(trackOverlayItemDtoSchema).default([]),
});
export type OverlaysListDto = z.infer<typeof overlaysListDtoSchema>;

export const pergaPersistentEventKindSchema = z.enum([
  'area.updated',
  'area.deleted',
  'track.updated',
  'track.deleted',
  'report.updated',
  'task.updated',
  'session.updated',
  'turn.completed',
  'ask.changed',
  'mail.received',
  'terminal.changed',
]);
export type PergaPersistentEventKind = z.infer<typeof pergaPersistentEventKindSchema>;

export const pergaTransientEventKindSchema = z.enum([
  'system.upgrade_status',
  'session.text_delta',
  'session.thinking_delta',
  'session.tool_progress',
]);
export type PergaTransientEventKind = z.infer<typeof pergaTransientEventKindSchema>;

export const sessionTextDeltaPayloadSchema = z.object({
  sessionId: z.string(),
  turnId: z.string(),
  delta: z.string(),
  sequence: z.number().int().positive().optional(),
});
export type SessionTextDeltaPayload = z.infer<typeof sessionTextDeltaPayloadSchema>;

export const sessionThinkingDeltaPayloadSchema = z.object({
  sessionId: z.string(),
  turnId: z.string(),
  delta: z.string(),
});
export type SessionThinkingDeltaPayload = z.infer<typeof sessionThinkingDeltaPayloadSchema>;

export const sessionToolProgressPayloadSchema = z.object({
  sessionId: z.string(),
  turnId: z.string(),
  phase: z.enum(['started', 'completed']).or(z.string()),
  callId: z.string(),
  name: z.string().nullish(),
  args: z.unknown().nullish(),
  isError: z.boolean().nullish(),
});
export type SessionToolProgressPayload = z.infer<typeof sessionToolProgressPayloadSchema>;

export type PergaStreamDelta =
  | Readonly<{ kind: 'session.text_delta'; sessionId: string; turnId: string; delta: string; sequence?: number; createdAtMs: number }>
  | Readonly<{ kind: 'session.thinking_delta'; sessionId: string; turnId: string; delta: string; createdAtMs: number }>
  | Readonly<{
    kind: 'session.tool_progress';
    sessionId: string;
    turnId: string;
    phase: string;
    callId: string;
    name: string | null;
    args: unknown;
    isError: boolean;
    createdAtMs: number;
  }>;

export type PergaPersistentEvent =
  | Readonly<{ kind: 'area.updated'; eventId: number; areaId: string | null; trackId: string | null; payload: AreaDto; createdAtMs: number }>
  | Readonly<{ kind: 'area.deleted'; eventId: number; areaId: string | null; trackId: string | null; payload: Readonly<{ id: string }>; createdAtMs: number }>
  | Readonly<{ kind: 'track.updated'; eventId: number; areaId: string | null; trackId: string | null; payload: TrackDto; createdAtMs: number }>
  | Readonly<{ kind: 'track.deleted'; eventId: number; areaId: string | null; trackId: string | null; payload: Readonly<{ id: string; areaId: string }>; createdAtMs: number }>
  | Readonly<{ kind: 'report.updated'; eventId: number; areaId: string | null; trackId: string | null; payload: Readonly<{ docRev?: number; summary?: string | null }>; createdAtMs: number }>
  | Readonly<{ kind: 'task.updated'; eventId: number; areaId: string | null; trackId: string | null; payload: Readonly<Record<string, never>>; createdAtMs: number }>
  | Readonly<{ kind: 'session.updated'; eventId: number; areaId: string | null; trackId: string | null; payload: AgentSessionDto; createdAtMs: number }>
  | Readonly<{ kind: 'turn.completed'; eventId: number; areaId: string | null; trackId: string | null; payload: SessionTurnDto; createdAtMs: number }>
  | Readonly<{ kind: 'ask.changed'; eventId: number; areaId: string | null; trackId: string | null; payload: AskDto; createdAtMs: number }>
  | Readonly<{ kind: 'mail.received'; eventId: number; areaId: string | null; trackId: string | null; payload: MailDto; createdAtMs: number }>
  | Readonly<{ kind: 'terminal.changed'; eventId: number; areaId: string | null; trackId: string | null; payload: TerminalDto; createdAtMs: number }>;

export type PergaEventDecodeResult =
  | Readonly<{ status: 'ready'; kind: 'upgrade-status'; payload: SystemUpgradeStatusPayload }>
  | Readonly<{ status: 'ready'; kind: 'persistent'; event: PergaPersistentEvent }>
  | Readonly<{ status: 'ready'; kind: 'delta'; delta: PergaStreamDelta }>
  | Readonly<{ status: 'failed'; error: ApiDecodeFailure }>;

const eventEnvelopeBaseSchema = z.object({
  eventId: z.number().int().nullish(),
  areaId: z.string().nullish(),
  trackId: z.string().nullish(),
  kind: z.string(),
  payload: z.unknown(),
  createdAtMs: z.number().default(0),
});

export function decodePergaEventEnvelope(input: unknown): PergaEventDecodeResult {
  const base = eventEnvelopeBaseSchema.safeParse(input);
  if (!base.success) {
    return {
      status: 'failed',
      error: { kind: 'decode', message: 'Perga event envelope did not match base schema', cause: base.error },
    };
  }
  const { eventId, areaId, trackId, kind, payload, createdAtMs } = base.data;
  if (kind === 'system.upgrade_status') {
    const parsed = systemUpgradeStatusPayloadSchema.safeParse(payload);
    if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid system.upgrade_status payload', cause: parsed.error } };
    return { status: 'ready', kind: 'upgrade-status', payload: parsed.data };
  }
  if (kind === 'session.text_delta') {
    const parsed = sessionTextDeltaPayloadSchema.safeParse(payload);
    if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid session.text_delta payload', cause: parsed.error } };
    return { status: 'ready', kind: 'delta', delta: { kind: 'session.text_delta', ...parsed.data, createdAtMs } };
  }
  if (kind === 'session.thinking_delta') {
    const parsed = sessionThinkingDeltaPayloadSchema.safeParse(payload);
    if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid session.thinking_delta payload', cause: parsed.error } };
    return { status: 'ready', kind: 'delta', delta: { kind: 'session.thinking_delta', ...parsed.data, createdAtMs } };
  }
  if (kind === 'session.tool_progress') {
    const parsed = sessionToolProgressPayloadSchema.safeParse(payload);
    if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid session.tool_progress payload', cause: parsed.error } };
    return {
      status: 'ready',
      kind: 'delta',
      delta: {
        kind: 'session.tool_progress',
        sessionId: parsed.data.sessionId,
        turnId: parsed.data.turnId,
        phase: parsed.data.phase,
        callId: parsed.data.callId,
        name: parsed.data.name ?? null,
        args: parsed.data.args ?? null,
        isError: parsed.data.isError === true,
        createdAtMs,
      },
    };
  }

  const resolvedId = eventId ?? 0;
  const resolvedArea = areaId ?? null;
  const resolvedTrack = trackId ?? null;
  switch (kind) {
    case 'area.updated': {
      const parsed = areaDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid area.updated payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea ?? parsed.data.id, trackId: resolvedTrack, payload: parsed.data, createdAtMs } };
    }
    case 'area.deleted': {
      const parsed = z.object({ id: z.string().optional(), areaId: z.string().optional() }).safeParse(payload ?? {});
      const id = (parsed.success ? (parsed.data.id ?? parsed.data.areaId) : undefined) ?? resolvedArea;
      if (!id) return { status: 'failed', error: { kind: 'decode', message: 'Invalid area.deleted payload', cause: payload } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: id, trackId: resolvedTrack, payload: { id }, createdAtMs } };
    }
    case 'track.updated': {
      const parsed = trackDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid track.updated payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea ?? parsed.data.areaId, trackId: resolvedTrack ?? parsed.data.id, payload: parsed.data, createdAtMs } };
    }
    case 'track.deleted': {
      const parsed = z.object({ id: z.string().optional(), trackId: z.string().optional(), areaId: z.string().optional(), area_id: z.string().optional() }).safeParse(payload ?? {});
      const id = (parsed.success ? (parsed.data.id ?? parsed.data.trackId) : undefined) ?? resolvedTrack;
      const deletedAreaId = (parsed.success ? (parsed.data.areaId ?? parsed.data.area_id) : undefined) ?? resolvedArea ?? '';
      if (!id) return { status: 'failed', error: { kind: 'decode', message: 'Invalid track.deleted payload', cause: payload } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: deletedAreaId || null, trackId: id, payload: { id, areaId: deletedAreaId }, createdAtMs } };
    }
    case 'report.updated': {
      const parsed = z.object({ docRev: z.number().optional(), summary: z.string().nullish() }).passthrough().safeParse(payload ?? {});
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid report.updated payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack, payload: parsed.data, createdAtMs } };
    }
    case 'task.updated':
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack, payload: {}, createdAtMs } };
    case 'session.updated': {
      const parsed = agentSessionDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid session.updated payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack ?? parsed.data.trackId, payload: parsed.data, createdAtMs } };
    }
    case 'turn.completed': {
      const parsed = sessionTurnDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid turn.completed payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack, payload: parsed.data, createdAtMs } };
    }
    case 'ask.changed': {
      const parsed = askDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid ask.changed payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack ?? parsed.data.trackId, payload: parsed.data, createdAtMs } };
    }
    case 'mail.received': {
      const parsed = mailDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid mail.received payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack ?? parsed.data.toTrackId, payload: parsed.data, createdAtMs } };
    }
    case 'terminal.changed': {
      const parsed = terminalDtoSchema.safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid terminal.changed payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: resolvedTrack ?? parsed.data.trackId, payload: parsed.data, createdAtMs } };
    }
    default:
      return { status: 'failed', error: { kind: 'decode', message: `Unknown Perga event kind: ${kind}`, cause: input } };
  }
}
