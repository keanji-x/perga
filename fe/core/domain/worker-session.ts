import { workerDispatchReceiptSchema, workerSettingsSchema, type WorkerDispatchReceipt, type WorkerSettings } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';

export function workerDispatchOperation(trackId: string, key: string, goal: string): ApiOperation<WorkerDispatchReceipt> {
  return { method: 'POST', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/tasks/dispatch`,
    headers: { 'Idempotency-Key': key }, body: { goal }, responseSchema: workerDispatchReceiptSchema };
}

export function updateWorkerSettingsOperation(sessionId: string, body: WorkerSettings & { attemptId: string }): ApiOperation<WorkerSettings> {
  return { method: 'PATCH', path: `/api/v1/sessions/${encodeURIComponent(sessionId)}/settings`, body,
    responseSchema: workerSettingsSchema };
}
