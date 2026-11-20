import { z } from 'zod';
import { workerDispatchReceiptSchema, workerSettingsSchema, type WorkerActivity, type WorkerDispatchReceipt, type WorkerSettings } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import { DEPLOYMENT_OWNER_USER_ID } from '../api/auth.js';
import type { ModelCatalog, TranscriptEntry } from './conversation.js';

export type WorkerDispatchOptions = Readonly<{
  access?: 'read_only' | 'read_write';
  acceptance?: string;
  dependsOn?: readonly string[];
  gate?: string;
  provider?: string;
  model?: string;
}>;

export function workerDispatchOperation(trackId: string, key: string, goal: string, options?: WorkerDispatchOptions): ApiOperation<WorkerDispatchReceipt> {
  return { method: 'POST', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/dispatch`,
    headers: { 'Idempotency-Key': key }, body: { goal, ...options }, responseSchema: workerDispatchReceiptSchema };
}

export function updateWorkerSettingsOperation(sessionId: string, body: WorkerSettings & { attemptId: string }, approvalKey?: string): ApiOperation<WorkerSettings> {
  return { method: 'PATCH', path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/settings`, body,
    ...(approvalKey === undefined ? {} : { redirect: 'error', headers: { 'X-Perga-Worker-Approval-Key': approvalKey } }),
    responseSchema: workerSettingsSchema };
}

export type WorkerMessageMode = 'steer' | 'queue';
export type WorkerMessageBody = Readonly<{ attemptId: string; content: string } & (
  { mode: 'steer'; turnId: string } | { mode: 'queue' } | { mode?: undefined }
)>;

const workerMessageReceiptSchema = z.object({ turnId: z.string().nullish(), inputId: z.string(),
  delivery: z.enum(['queued', 'dispatching', 'delivered', 'rejected', 'unknown', 'interrupted', 'withdrawn']), messageId: z.string().optional() });

export function sendWorkerMessageOperation(cardId: string, key: string, body: WorkerMessageBody): ApiOperation<z.infer<typeof workerMessageReceiptSchema>> {
  return { method: 'POST', path: `/api/v1/sessions/${encodeURIComponent(cardId)}/messages`,
    headers: { 'Idempotency-Key': key }, body, responseSchema: workerMessageReceiptSchema };
}

/** A task-bound provider can declare a different model catalog from the installation. */
export function workerModelCatalog(worker: WorkerActivity): ModelCatalog | null {
  const config = worker.providerConfiguration;
  if (config == null && worker.reasoningEfforts.length === 0) return null;
  const efforts = (choices: readonly string[]) => choices.map(reasoning_effort => ({ reasoning_effort, description: null }));
  const defaultModel = config?.defaultModel ?? null;
  const models = config?.models ?? [];
  const current = worker.nextSettings.model;
  const currentEfforts = config?.sessionReasoningEfforts ?? worker.reasoningEfforts;
  const currentModel = current != null && !models.some(model => model.id === current) && currentEfforts.length > 0
    ? [{ id: current, name: current, reasoningEfforts: currentEfforts, defaultReasoningEffort: null }]
    : [];
  return {
    models: [...models, ...currentModel].map(model => ({
      id: model.id, model: model.id, display_name: model.name, description: '', resolved_model: null, is_default: model.id === defaultModel,
      supported_reasoning_efforts: efforts(config?.sessionReasoningEfforts ?? model.reasoningEfforts),
      default_reasoning_effort: model.id === defaultModel || models.length === 0
        ? config?.defaultReasoningEffort ?? null : model.defaultReasoningEffort ?? null,
    })),
    default: {
      model: defaultModel, reasoning_effort: config?.defaultReasoningEffort ?? null,
      supported_reasoning_efforts: efforts(config?.sessionReasoningEfforts
        ?? models.find(model => model.id === defaultModel)?.reasoningEfforts ?? worker.reasoningEfforts),
    },
    default_source: defaultModel === null ? 'unknown' : 'config_read',
    source: 'live',
    fetched_at_ms: null,
  };
}

/** Receipts without a transcript row still give a queued message a visible final state. */
export function withTaskMessageFailures(
  entries: readonly TranscriptEntry[], messages: WorkerActivity['messages'], viewerId: string | null,
): readonly TranscriptEntry[] {
  const failures = messages.filter(message => message.delivery === 'rejected' || message.delivery === 'interrupted');
  if (failures.length === 0) return entries;
  const displayed = new Set(entries.map(entry => entry.id));
  const next = [...entries];
  for (const message of failures) {
    const id = `human:${message.id}`;
    if (displayed.has(id)) continue;
    displayed.add(id);
    const owner = message.author === 'owner:installation';
    const at = next.findLastIndex(entry => entry.atMs <= message.createdAtMs) + 1;
    next.splice(at, 0, {
      id,
      author: !owner ? 'unknown' : viewerId === DEPLOYMENT_OWNER_USER_ID ? 'you' : 'human',
      ...(owner ? { human: { userId: DEPLOYMENT_OWNER_USER_ID, displayName: 'Owner', messageId: message.id, revision: 1 } } : {}),
      text: message.content,
      atMs: message.createdAtMs,
      messageStatus: 'failed',
      deliveryError: message.delivery === 'rejected' ? 'The message was rejected.' : 'Delivery was interrupted.',
    });
  }
  return next.length === entries.length ? entries : next;
}

const toolProgressSchema = z.object({ callId: z.string().optional(), name: z.string().nullish(), phase: z.string().optional(),
  args: z.unknown().optional(), output: z.string().nullish(), isError: z.boolean().optional(), preview: z.string().optional() });

/** One tool per call in the running turn. The most recent event may only carry completion. */
export function workerToolActivity(events: WorkerActivity['events']) {
  const tools = new Map<string, {
    callId: string; name: string | null; phase: string; isError: boolean; args: unknown; output: string | null; atMs: number;
  }>();
  for (const event of events) {
    if (event.kind !== 'session.tool_progress') continue;
    const parsed = toolProgressSchema.safeParse(event.payload);
    if (!parsed.success) continue;
    const { callId, name, phase, args, output, isError, preview } = parsed.data;
    const id = callId ?? `event-${event.sequence}`;
    const prior = tools.get(id);
    tools.set(id, {
      callId: id,
      name: name ?? prior?.name ?? null,
      phase: phase ?? prior?.phase ?? (preview === undefined ? 'started' : 'completed'),
      isError: isError ?? prior?.isError ?? false,
      args: args ?? prior?.args ?? null,
      output: preview ?? output ?? prior?.output ?? null,
      atMs: prior?.atMs ?? event.createdAtMs,
    });
  }
  return tools;
}
