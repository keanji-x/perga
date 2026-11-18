import { z } from 'zod';
import { workerDispatchReceiptSchema, workerSettingsSchema, type WorkerActivity, type WorkerDispatchReceipt, type WorkerSettings } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

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

export function updateWorkerSettingsOperation(sessionId: string, body: WorkerSettings & { attemptId: string }): ApiOperation<WorkerSettings> {
  return { method: 'PATCH', path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/settings`, body,
    responseSchema: workerSettingsSchema };
}

export type WorkerMessageMode = 'steer' | 'queue';
export type WorkerMessageBody = Readonly<{ attemptId: string; content: string } & (
  { mode: 'steer'; turnId: string } | { mode: 'queue' } | { mode?: undefined }
)>;

const workerMessageReceiptSchema = z.object({ turnId: z.string().nullish(), inputId: z.string(),
  delivery: z.enum(['queued', 'dispatching', 'delivered', 'rejected', 'unknown', 'interrupted']) });

export function sendWorkerMessageOperation(cardId: string, key: string, body: WorkerMessageBody): ApiOperation<z.infer<typeof workerMessageReceiptSchema>> {
  return { method: 'POST', path: `/api/v1/sessions/${encodeURIComponent(cardId)}/messages`,
    headers: { 'Idempotency-Key': key }, body, responseSchema: workerMessageReceiptSchema };
}

const toolProgressSchema = z.object({ callId: z.string().optional(), name: z.string().nullish(), phase: z.string().optional(),
  args: z.unknown().optional(), output: z.string().nullish(), isError: z.boolean().optional(), preview: z.string().optional() });

export function workerToolActivity(events: WorkerActivity['events']) {
  const names = new Map<string, string>();
  return events.flatMap(event => {
    if (event.kind !== 'session.tool_progress') return [];
    const parsed = toolProgressSchema.safeParse(event.payload);
    if (!parsed.success) return [];
    const { callId, name, phase, args, output, isError, preview } = parsed.data;
    if (callId !== undefined && name != null) names.set(callId, name);
    return [{ sequence: event.sequence,
      title: name ?? (callId === undefined ? undefined : names.get(callId)) ?? 'Tool call',
      status: preview !== undefined ? 'Truncated' : isError === true ? 'Failed' : phase === 'completed' ? 'Completed' : 'Started',
      detail: preview ?? output ?? (args == null ? undefined : JSON.stringify(args, null, 2)),
    }];
  });
}
