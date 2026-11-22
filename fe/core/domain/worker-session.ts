import { z } from 'zod';
import { type SessionActivity, type SessionCapabilities, sessionCreationReceiptSchema } from '../api/perga-v1.js';
import type { ApiOperation } from '../api/types.js';
import type { ModelCatalog } from './conversation-models.js';

/** A task-bound provider can declare a different model catalog from the installation. */
export function sessionModelCatalog(config: SessionCapabilities): ModelCatalog | null {
  const efforts = (choices: readonly string[]) => choices.map(reasoning_effort => ({ reasoning_effort, description: null }));
  const defaultModel = config.defaultModel ?? null;
  const models = config.models ?? [];
  return {
    models: models.map(model => ({
      id: model.id, model: model.id, display_name: model.name, description: '', resolved_model: null, is_default: model.id === defaultModel,
      supported_reasoning_efforts: efforts(model.reasoningEfforts),
      default_reasoning_effort: model.id === defaultModel || models.length === 0
        ? config.defaultReasoningEffort ?? null : model.defaultReasoningEffort ?? null,
    })),
    default: {
      model: defaultModel, reasoning_effort: config.defaultReasoningEffort ?? null,
      supported_reasoning_efforts: efforts(models.find(model => model.id === defaultModel)?.reasoningEfforts ?? []),
    },
    default_source: defaultModel === null ? 'unknown' : 'config_read',
    source: 'live',
    fetched_at_ms: null,
  };
}

const toolProgressSchema = z.object({ callId: z.string().optional(), name: z.string().nullish(), phase: z.string().optional(),
  args: z.unknown().optional(), output: z.string().nullish(), isError: z.boolean().optional(), preview: z.string().optional() });

/** One tool per call in the running turn. The most recent event may only carry completion. */
export function workerToolActivity(events: SessionActivity['events']) {
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

export type WorkerDispatchOptions = Readonly<{
  access?: 'read_only' | 'read_write'; acceptance?: string; dependsOn?: readonly string[]; gate?: string; provider?: string; model?: string;
}>;

/** A task is optional configuration of the same session creation primitive. */
export function workerDispatchOperation(trackId: string, key: string, goal: string, options: WorkerDispatchOptions = {}): ApiOperation<z.infer<typeof sessionCreationReceiptSchema>> {
  const { provider, model, ...task } = options;
  return { method: 'POST', path: `/api/v1/tracks/${encodeURIComponent(trackId)}/sessions`,
    headers: { 'Idempotency-Key': key }, body: { prompt: goal, provider, model, task }, responseSchema: sessionCreationReceiptSchema };
}
