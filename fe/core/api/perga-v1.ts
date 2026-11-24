// Perga /api/v1 REST and WebSocket wire contracts, 1:1 with Perga.Contracts, Perga.ReportDoc, and Perga.Kernel.
// JSON property names are camelCase; enum strings are snake_case.

import { z } from 'zod';

import { flowOutcomeSchema } from '../domain/report-flow.js';
import { memoryUpdatedPayloadSchema } from '../domain/memory.js';
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

// Historical kinds still occur on the Host wire.
export const reportBlockKindSchema = z.enum(['prose', 'task', 'html', 'table', 'view', 'checkpoints', 'widget']);
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

export const compiledTemplateManifestDtoSchema = z.object({
  id: z.string(),
  title: z.string().optional(),
  description: z.string().nullish().transform((v) => v ?? null),
  defaultPlugins: z.array(z.string()).nullish().transform((v) => v ?? []),
  inputsSchema: z.unknown().default({ type: 'object', properties: {} }),
  requireGateForReadWrite: z.boolean().nullish().transform((v) => v ?? false),
  workspaceDriver: z.string().nullish(),
  workspaceLowerCacheDirs: z.array(z.string()).nullish().transform((v) => v ?? []),
  workspaceCreateCmd: z.string().nullish(),
  workspaceCloseCmd: z.string().nullish(),
  afterWorkerDoneCmd: z.string().nullish(),
  flow: z.array(z.record(z.string(), z.unknown())).default([]),
  extends: z.string().nullish(),
  hasHeaderView: z.boolean().default(false),
  initialReportMarkdown: z.string().nullish().transform((v) => v ?? ''),
  routing: z.array(z.unknown()).nullish(),
  viewNames: z.array(z.string()).nullish(),
}).transform((raw) => ({
  ...raw,
  title: raw.title ?? raw.id,
}));
export type CompiledTemplateManifestDto = z.infer<typeof compiledTemplateManifestDtoSchema>;

export const executionTargetDtoSchema = z.object({
  id: z.string(),
  label: z.string(),
  kind: z.string(),
  isDefault: z.boolean().default(false),
  destination: z.string().nullish(),
  removable: z.boolean().default(false),
});
export type ExecutionTargetDto = z.infer<typeof executionTargetDtoSchema>;

export const areaDtoSchema = z.object({
  id: z.string(),
  name: z.string(),
  color: z.string(),
  defaultCwd: z.string().nullish(),
  folders: z.array(z.string()).default([]),
  sortOrder: z.number().default(0),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
  executionTargetId: z.string().optional(),
});
export type AreaDto = z.infer<typeof areaDtoSchema>;

export const bootstrapDtoSchema = z.object({
  version: z.string(),
  databaseId: z.string().nullish(),
  dbInstanceId: z.string().nullish(),
  authRequired: z.boolean().nullish(),
  conversationSide: z.boolean().optional(),
  providers: z.array(providerStatusDtoSchema).default([]),
  templates: z.array(compiledTemplateManifestDtoSchema).default([]),
  areas: z.array(areaDtoSchema).default([]),
  executionTargets: z.array(executionTargetDtoSchema).optional(),
});
export type BootstrapDto = z.infer<typeof bootstrapDtoSchema>;

const templateToneSchema = z.enum(['neutral', 'accent', 'success', 'warn', 'error']);
export const templateHeaderViewDtoSchema = z.object({
  badges: z.array(z.object({ label: z.string(), tone: templateToneSchema.nullish() })).nullish(),
  notice: z.object({ text: z.string(), tone: z.enum(['info', 'warn', 'error']).nullish() }).nullish(),
  metrics: z.array(z.object({
    label: z.string(), value: z.string(), delta: z.string().nullish(), tone: templateToneSchema.nullish(),
    sub: z.string().nullish(), spark: z.array(z.number()).nullish(),
  })).nullish(),
  rules: z.array(z.object({ label: z.string(), text: z.string() })).nullish(),
});

export const trackTemplateSnapshotSchema = z.object({
  snapshot: z.string().nullish().transform((v) => v ?? null),
  manifest: compiledTemplateManifestDtoSchema.nullish().transform((v) => v ?? null),
  inputs: z.unknown(), data: z.unknown(),
  headerView: templateHeaderViewDtoSchema.nullish().transform((v) => v ?? null),
});

export const trackDataPatchResultSchema = z.object({
  ok: z.literal(true),
  dataRev: z.number().int().nonnegative(),
  data: z.record(z.string(), z.unknown()),
});
export type TrackDataPatchResult = z.infer<typeof trackDataPatchResultSchema>;

export const trackDtoSchema = z.object({
  id: z.string(),
  areaId: z.string(),
  title: z.string().nullish().transform((v) => v ?? ''),
  cwd: z.string().nullish().transform((v) => v ?? ''),
  workspaceDriver: z.string().default('direct'),
  workspaceSpec: z.unknown().default({}),
  leadSessionId: z.string().nullish(),
  templateId: z.string().nullish(),
  templateInputs: z.unknown().default({}),
  data: z.record(z.string(), z.unknown()).default({}),
  dataRev: z.number().int().nonnegative().default(0),
  flowRev: z.number().int().nonnegative().default(0),
  flowSpecJson: z.string().nullish(),
  flowFileSha256: z.string().nullish(),
  templateTitle: z.string().nullish(),
  templateHeaderView: templateHeaderViewDtoSchema.nullish(),
  enabledPlugins: z.array(z.string()).default([]),
  closedAtMs: z.number().nullish(),
  pinnedAtMs: z.number().nullish(),
  sortOrder: z.number().default(0),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
  executionTargetId: z.string().optional(),
});
export type TrackDto = z.infer<typeof trackDtoSchema>;

export const gateStepSpecDtoSchema = z.object({
  name: z.string(),
  cmd: z.string(),
});
export type GateStepSpecDto = z.infer<typeof gateStepSpecDtoSchema>;

export const gateSpecDtoSchema = z.object({
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
  role: z.string().nullish(),
  access: accessModeSchema.default('read_write'),
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
  outcome: flowOutcomeSchema.optional(),
  outcomeSummary: z.string().nullish(),
  triggerRunKey: z.string().nullish(),
  archivedAtMs: z.number().int().nullish(),
  sessionId: z.string().nullish(),
  runCount: z.number().int().default(0),
  workerResult: z.unknown().nullish(),
  meta: z.unknown().nullish(),
  gateVerdict: z.unknown().nullish(),
  startedAtMs: z.number().nullish(),
  finishedAtMs: z.number().nullish(),
  createdAtMs: z.number(),
  updatedAtMs: z.number(),
});
export type TaskDto = z.infer<typeof taskDtoSchema>;

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
  dataRev: z.number().int().nonnegative().nullish(),
  summary: z.string().nullish().transform((v) => v ?? ''),
  renderedMarkdown: z.string().nullish().transform((v) => v ?? ''),
  blocks: z.array(reportBlockDtoSchema).nullish().transform((v) => v ?? []),
  tasks: z.array(taskDtoSchema).nullish().transform((v) => v ?? []),
  anchor: readAnchorDtoSchema.or(z.string()).nullish(),
  diagnostics: z.array(validationDiagnosticDtoSchema).nullish().transform((v) => v ?? []),
});
export type ReportReadResultDto = z.infer<typeof reportReadResultDtoSchema>;

export const agentSessionDtoSchema = z.object({
  id: z.string(),
  trackId: z.string(),
  isLead: z.boolean().default(false),
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
  lastNotifiableTurnAtMs: z.number().nullish().transform(v => v ?? null),
  parentSessionId: z.string().nullish(), spawnDepth: z.number().int().default(0),
  sourceSessionId: z.string().nullish(),
  configVersion: z.number().int().default(0), notifyParent: z.string().default('none'),
  systemPrompt: z.string().nullish(), unknownReason: z.string().nullish(), unknownTurnId: z.string().nullish(),
  unknownLastError: z.string().nullish(), reconnectInvalidated: z.boolean().default(false),
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
  resolvedAtMs: z.number().nullish(),
});
export type SessionInputReceipt = z.infer<typeof sessionInputReceiptSchema>;

export const sessionCreationReceiptSchema = z.object({
  session: agentSessionDtoSchema,
  firstInput: sessionInputReceiptSchema.nullish(),
  requestKey: z.string().nullish(),
}).refine(result => result.firstInput == null || result.firstInput.sessionId === result.session.id);

export type SessionCreationReceipt = z.infer<typeof sessionCreationReceiptSchema>;

export const ATTACHABLE_IMAGE_TYPES = Object.freeze(['image/png', 'image/jpeg', 'image/gif', 'image/webp'] as const);
export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
export const MAX_TOTAL_ATTACHMENT_BYTES = 25 * 1024 * 1024;
export const MAX_ATTACHMENTS_PER_MESSAGE = 8;

const attachmentMimeByExtension = Object.freeze({
  png: 'image/png', jpg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp',
});
export const messageAttachmentDtoSchema = z.object({
  id: z.string().regex(/^[0-9a-f]{64}\.(png|jpg|gif|webp)$/),
  contentType: z.enum(ATTACHABLE_IMAGE_TYPES),
  size: z.number().int().positive().max(MAX_ATTACHMENT_BYTES),
  url: z.string().regex(/^\/api\/v1\/tracks\/[A-Za-z0-9_-]+\/media\/[0-9a-f]{64}\.(png|jpg|gif|webp)$/),
}).refine(attachment => attachment.url.endsWith(`/${attachment.id}`), { path: ['url'] })
  .refine(attachment => {
    const extension = attachment.id.slice(65) as keyof typeof attachmentMimeByExtension;
    return attachment.contentType === attachmentMimeByExtension[extension];
  }, { path: ['contentType'] });

export const sessionItemDtoSchema = z.object({
  id: z.number().int(),
  sessionId: z.string(),
  turnId: z.string().nullable(),
  kind: z.enum(['user', 'thinking', 'assistant_text', 'tool_call', 'tool_result', 'turn_outcome', 'turn_error', 'epoch_boundary']).or(z.string()),
  payload: z.object({ attachments: z.array(messageAttachmentDtoSchema).optional(), requestKey: z.string().optional() }).passthrough(),
  createdAtMs: z.number(),
  messageStatus: z.enum(['queued', 'sending', 'processing', 'completed', 'failed', 'unconfirmed']).nullish(),
});
export type SessionItemDto = z.infer<typeof sessionItemDtoSchema>;

export const askQuestionDtoSchema = z.object({
  question: z.string(),
  options: z.array(z.string()).nullish().transform((v) => v ?? []),
  isMultiSelect: z.boolean().nullish().transform((v) => v ?? false),
});
export type AskQuestionDto = z.infer<typeof askQuestionDtoSchema>;

export const approvalChoiceDtoSchema = z.object({
  id: z.string(),
  label: z.string(),
  isApproval: z.boolean(),
});
export type ApprovalChoiceDto = z.infer<typeof approvalChoiceDtoSchema>;

export const createSessionRequestSchema = z.object({
  title: z.string().nullish(), provider: z.string().nullish(), model: z.string().nullish(), reasoningEffort: z.string().nullish(),
  permissionMode: z.string().nullish(), systemPrompt: z.string().nullish(), prompt: z.string().nullish(),
  notifyParent: z.enum(['none', 'turn_end']).nullish(),
  sourceSessionId: z.string().nullish(), sourceExcerpt: z.string().nullish(),
}).strict();
export const updateSessionRequestSchema = z.object({
  expectedConfigVersion: z.number().int().nullish(), title: z.string().nullish(), archived: z.boolean().nullish(),
  provider: z.string().nullish(), model: z.string().nullish(), reasoningEffort: z.string().nullish(), permissionMode: z.string().nullish(),
}).strict();
export const multipartMessagePayloadSchema = z.object({
  content: z.string().nullish(), mode: z.enum(['default', 'steer', 'queue']).optional(), turnId: z.string().nullish(),
  expectedEpoch: z.number().int().nullish(),
}).strict();
export const sendSessionMessageRequestSchema = z.object({
  ...multipartMessagePayloadSchema.shape,
  messageId: z.string().nullish(), requestKey: z.string().nullish(),
}).strict();
export const sessionControlRequestSchema = z.object({
  action: z.enum(['stop', 'reconnect', 'reset_context', 'compact']), turnId: z.string().nullish(),
  expectedEpoch: z.number().int().nullish(),
}).strict();
export const respondInteractionRequestSchema = z.object({ executionId: z.string().nullish(), optionId: z.string().nullish(), answers: z.unknown().optional() }).strict();
export type CreateSessionRequest = z.infer<typeof createSessionRequestSchema>;
export type UpdateSessionRequest = z.infer<typeof updateSessionRequestSchema>;
export type SendSessionMessageRequest = z.infer<typeof sendSessionMessageRequestSchema>;
export type SessionControlRequest = z.infer<typeof sessionControlRequestSchema>;
export type RespondInteractionRequest = z.infer<typeof respondInteractionRequestSchema>;
export const agentRuntimeTaskSchema = z.object({
  taskId: z.string(), taskType: z.string().nullish(), nativeType: z.string().nullish(),
  description: z.string().nullish(), toolCallId: z.string().nullish(), childSessionId: z.string().nullish(),
  background: z.boolean().nullish(), ambient: z.boolean().nullish(), status: z.string().nullish(),
  error: z.string().nullish(), summary: z.string().nullish(), outputFile: z.string().nullish(),
  seq: z.number().nullish(), ts: z.number().nullish(),
});
const agentStopEvidenceSchema = z.object({ pendingAbortIds: z.array(z.string()), abortedOnExit: z.boolean() });
export const agentRuntimeStatusSchema = z.object({
  kind: z.string(), phase: z.string().nullish(), toolName: z.string().nullish(), toolCallId: z.string().nullish(),
  writing: z.boolean().nullish(), sinceSeq: z.number().nullish(), requestId: z.string().nullish(),
  inputId: z.string().nullish(), released: z.boolean().nullish(),
  lastTurnOutcome: z.object({ kind: z.string(), failure: z.string().nullish(), reason: z.string().nullish(),
    status: z.number().nullish(), credential: z.string().nullish(), resetsAt: z.string().nullish(),
    rateLimitType: z.string().nullish(), structuredOutput: z.unknown().optional() }).nullish(),
  idleActivity: z.object({ since: z.number(), lastEventAt: z.number() }).nullish(),
  pendingPrompt: z.object({ inputId: z.string(), sinceSeq: z.number(), requestId: z.string().nullish(),
    stop: agentStopEvidenceSchema.nullish() }).nullish(),
  stop: agentStopEvidenceSchema.nullish(),
  compaction: z.object({ requestId: z.string(), started: z.boolean().nullish() }).nullish(),
});
export const sessionRuntimeStatusPayloadSchema = z.object({ sessionId: z.string(), status: agentRuntimeStatusSchema.nullish() });
export const sessionTaskProgressPayloadSchema = z.object({ sessionId: z.string(), task: agentRuntimeTaskSchema });
export type SessionRuntimeStatusPayload = z.infer<typeof sessionRuntimeStatusPayloadSchema>;
export type SessionTaskProgressPayload = z.infer<typeof sessionTaskProgressPayloadSchema>;
export type AgentRuntimeStatusDto = z.infer<typeof agentRuntimeStatusSchema>;
export type AgentRuntimeTaskDto = z.infer<typeof agentRuntimeTaskSchema>;

export const sessionSummarySchema = z.object({
  session: agentSessionDtoSchema, state: z.enum(['idle', 'running', 'stopping', 'compacting', 'unknown']),
  taskId: z.string().nullish(), readonlyReason: z.enum(['archived', 'track_closed', 'waiting_task', 'task_verifying', 'task_finished']).nullable(), workerPaused: z.boolean(), lastTurn: sessionTurnDtoSchema.nullish(), pendingInteractionCount: z.number().int(),
});
export type SessionSummary = z.infer<typeof sessionSummarySchema>;
export const sessionMessageReceiptSchema = z.object({
  messageId: z.string(), sessionId: z.string(), turnId: z.string().nullish(), delivery: z.string(), replay: z.boolean().default(false),
});
export const sessionInteractionResultSchema = z.object({ id: z.string(), kind: z.string() });
export const sessionActivitySchema = z.object({
  turnId: z.string().nullish(), sequence: z.number(), reset: z.boolean(), text: z.string(), truncated: z.boolean(),
  events: z.array(z.object({ sequence: z.number(), kind: z.string(), payload: z.unknown(), createdAtMs: z.number() })),
});
export type SessionActivity = z.infer<typeof sessionActivitySchema>;
export const sessionCapabilitiesSchema = z.object({
  models: z.array(z.object({ id: z.string(), name: z.string(), reasoningEfforts: z.array(z.string()), defaultReasoningEffort: z.string().nullish() })),
  defaultModel: z.string().nullish(), defaultReasoningEffort: z.string().nullish(), permissionModes: z.array(permissionModeOptionDtoSchema),
  supportsNativeSteer: z.boolean(), supportsSessionResume: z.boolean(), supportsCompact: z.boolean(),
});
export type SessionCapabilities = z.infer<typeof sessionCapabilitiesSchema>;
export const pendingInteractionSchema = z.object({
  id: z.string(), kind: z.string(), sessionId: z.string(), turnId: z.string().nullish(), executionId: z.string().nullish(),
  title: z.string(), options: z.array(approvalChoiceDtoSchema).nullish(),
  questions: z.array(askQuestionDtoSchema).nullish(),
  canRespond: z.boolean(), createdAtMs: z.number(), toolName: z.string().nullish(), arguments: z.unknown().nullish(),
});
export type PendingInteraction = z.infer<typeof pendingInteractionSchema>;
export const sessionItemsPageDtoSchema = z.object({
  items: z.array(sessionItemDtoSchema).nullish().transform((v) => v ?? []),
  activeTurn: sessionTurnDtoSchema.nullish(),
  state: z.enum(['idle', 'running', 'stopping', 'compacting', 'unknown']),
  readonlyReason: z.enum(['archived', 'track_closed', 'waiting_task', 'task_verifying', 'task_finished']).nullable(),
  canReopen: z.boolean(),
  workerPaused: z.boolean(),
  lastTurn: sessionTurnDtoSchema.nullish().transform(value => value ?? null),
  unknownDetail: z.object({ reason: z.string(), detail: z.string(), canReconnect: z.boolean(),
    canResetContext: z.boolean(), recovering: z.boolean(), lastError: z.string().nullable() }).nullable(),
  queuedMessages: z.array(z.object({ id: z.string(), text: z.string(), queuedAtMs: z.number(),
    state: z.enum(['waiting', 'steering', 'sending', 'processing', 'unconfirmed']),
    attachments: z.array(messageAttachmentDtoSchema).nullish() })).optional(),
  attachmentsSupported: z.boolean(),
  canSteer: z.boolean().optional(),
  queueHandoffIds: z.array(z.string()).optional(),
  session: agentSessionDtoSchema,
  hasMoreBefore: z.boolean().optional(),
  activity: sessionActivitySchema.nullish(),
  capabilities: sessionCapabilitiesSchema.nullish(),
  pendingInteractions: z.array(pendingInteractionSchema).nullish(),
  taskBinding: z.object({ task: taskDtoSchema, phase: z.string(), waitingReason: z.string().nullish() }).nullish(),
  permissionModes: z.array(permissionModeOptionDtoSchema).nullish(),
  runtimeStatus: agentRuntimeStatusSchema.nullish(),
  runtimeTasks: z.array(agentRuntimeTaskSchema).nullish(),
  turns: z.array(sessionTurnDtoSchema).nullish(),
});
export type SessionItemsPageDto = z.infer<typeof sessionItemsPageDtoSchema>;

export const approvalDtoSchema = z.object({
  id: z.string(),
  executionId: z.string(),
  sessionId: z.string(),
  turnId: z.string().nullish(),
  title: z.string(),
  options: z.array(approvalChoiceDtoSchema),
  source: z.string().default('policy'), toolName: z.string().nullish(), arguments: z.unknown().nullish(), delegateSessionId: z.string().nullish(),
  createdAtMs: z.number(),
});
export type ApprovalDto = z.infer<typeof approvalDtoSchema>;

export const askDtoSchema = z.object({
  targetSessionId: z.string().nullish(),
  delivery: z.enum(['wake', 'hold']),
  sessionId: z.string().nullish(),
  turnId: z.string().nullish(),
  id: z.string(),
  trackId: z.string(),
  status: askStatusSchema.or(z.string()),
  questions: z.array(askQuestionDtoSchema),
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

export const dbMaintenanceStatsDtoSchema = z.object({
  prunedEvents: z.number().int(), thinnedRevisions: z.number().int(), compactedItems: z.number().int(),
  reclaimedPages: z.number().int(), compactedTasks: z.number().int(), reclaimedBlobs: z.number().int(),
});
export type DbMaintenanceStatsDto = z.infer<typeof dbMaintenanceStatsDtoSchema>;

export const terminalDtoSchema = z.object({
  id: z.string(),
  trackId: z.string(),
  sessionId: z.string().nullish(),
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
  pendingApprovals: z.array(approvalDtoSchema).optional(),
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
  isDir: z.boolean(),
  isSymlink: z.boolean().default(false),
});
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

export const notifiableSessionWatermarkSchema = z.object({ sessionId: z.string(), lastNotifiableTurnAtMs: z.number() });
export type NotifiableSessionWatermark = z.infer<typeof notifiableSessionWatermarkSchema>;
export const readReceiptDtoSchema = z.object({
  kind: z.enum(['track', 'conversation']), targetId: z.string(),
  readThroughMs: z.number().int().nonnegative(), manualUnread: z.boolean(),
});
export const readReceiptsResponseDtoSchema = z.object({ baselineMs: z.number().int().nonnegative(), receipts: z.array(readReceiptDtoSchema) });

export const trackOverlayItemDtoSchema = z.object({
  trackId: z.string(),
  areaId: z.string(),
  isWorking: z.boolean().default(false),
  openAskCount: z.number().int().default(0),
  pendingApprovalCount: z.number().int().default(0),
  hasBlockedSession: z.boolean().default(false),
  workspaceFailed: z.boolean().default(false),
  notifiableSessions: z.array(notifiableSessionWatermarkSchema).nullish().transform(v => v ?? []),
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
  'track.schedules.updated',
  'report.updated',
  'task.updated',
  'session.updated',
  'turn.completed',
  'ask.changed',
  'approval.decided',
  'approval.delivery',
  'mail.received',
  'terminal.changed',
]);
export type PergaPersistentEventKind = z.infer<typeof pergaPersistentEventKindSchema>;

export const pergaTransientEventKindSchema = z.enum([
  'session.runtime_status',
  'session.task_progress',
  'system.upgrade_status',
  'session.text_delta',
  'session.thinking_delta',
  'session.tool_progress',
  'approval.changed',
  'session.state_changed',
  'read_receipt.updated',
  'memory.updated',
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
  | Readonly<{ kind: 'track.schedules.updated'; eventId: number; areaId: string | null; trackId: string; payload: { trackId: string }; createdAtMs: number }>
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
  | Readonly<{ status: 'ready'; kind: 'runtime-status'; payload: z.infer<typeof sessionRuntimeStatusPayloadSchema> }>
  | Readonly<{ status: 'ready'; kind: 'task-progress'; payload: z.infer<typeof sessionTaskProgressPayloadSchema> }>
  | Readonly<{ status: 'ready'; kind: 'read-receipt'; payload: z.infer<typeof readReceiptDtoSchema> }>
  | Readonly<{ status: 'ready'; kind: 'session-state'; trackId: string; sessionId: string }>
  | Readonly<{ status: 'ready'; kind: 'approvals'; trackId: string; sessionId: string }>
  | Readonly<{ status: 'ready'; kind: 'approval-audit' }>
  | Readonly<{ status: 'ready'; kind: 'upgrade-status'; payload: SystemUpgradeStatusPayload }>
  | Readonly<{ status: 'ready'; kind: 'memory-updated'; payload: z.output<typeof memoryUpdatedPayloadSchema> }>
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
  if (kind === 'memory.updated') {
    const parsed = memoryUpdatedPayloadSchema.safeParse(payload);
    return parsed.success ? { status: 'ready', kind: 'memory-updated', payload: parsed.data }
      : { status: 'failed', error: { kind: 'decode', message: 'Invalid memory.updated payload', cause: parsed.error } };
  }
  if (kind === 'session.runtime_status') {
    const parsed = sessionRuntimeStatusPayloadSchema.safeParse(payload);
    return parsed.success ? { status: 'ready', kind: 'runtime-status', payload: parsed.data }
      : { status: 'failed', error: { kind: 'decode', message: 'Invalid runtime status', cause: parsed.error } };
  }
  if (kind === 'session.task_progress') {
    const parsed = sessionTaskProgressPayloadSchema.safeParse(payload);
    return parsed.success ? { status: 'ready', kind: 'task-progress', payload: parsed.data }
      : { status: 'failed', error: { kind: 'decode', message: 'Invalid runtime task', cause: parsed.error } };
  }
  if (kind === 'read_receipt.updated') {
    const parsed = readReceiptDtoSchema.safeParse(payload);
    if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid read receipt', cause: parsed.error } };
    return { status: 'ready', kind: 'read-receipt', payload: parsed.data };
  }
  if (kind === 'session.state_changed') {
    const parsed = z.object({ sessionId: z.string() }).safeParse(payload);
    if (trackId == null || !parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid session state change' } };
    return { status: 'ready', kind: 'session-state', trackId, sessionId: parsed.data.sessionId };
  }
  if (kind === 'approval.changed') {
    const parsed = z.object({ sessionId: z.string() }).safeParse(payload);
    if (trackId == null || !parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid approval change' } };
    return { status: 'ready', kind: 'approvals', trackId, sessionId: parsed.data.sessionId };
  }
  if (kind === 'approval.decided' || kind === 'approval.delivery') return { status: 'ready', kind: 'approval-audit' };
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
    case 'track.schedules.updated': {
      const parsed = z.object({ trackId: z.string().min(1) }).safeParse(payload);
      if (!parsed.success) return { status: 'failed', error: { kind: 'decode', message: 'Invalid track.schedules.updated payload', cause: parsed.error } };
      return { status: 'ready', kind: 'persistent', event: { kind, eventId: resolvedId, areaId: resolvedArea, trackId: parsed.data.trackId, payload: parsed.data, createdAtMs } };
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
