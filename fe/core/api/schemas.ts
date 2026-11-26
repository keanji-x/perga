// Zod schemas for WS `/api/events` payloads, mirroring the kernel's `Event` enum and entity types.
// On parse failure, callers log and skip dispatch — never throw.
import { z } from 'zod';

import { memoryUpdatedPayloadSchema } from '../domain/memory.js';
import { areaDtoSchema, agentSessionDtoSchema, askDtoSchema, decodePergaEventEnvelope, mailDtoSchema, readReceiptDtoSchema, sessionRuntimeStatusPayloadSchema, sessionTaskProgressPayloadSchema, sessionTextDeltaPayloadSchema, sessionThinkingDeltaPayloadSchema, sessionToolProgressPayloadSchema, sessionTurnDtoSchema, systemUpgradeStatusPayloadSchema, terminalDtoSchema, trackDtoSchema, workspaceFileOpenRequestedSchema, type PergaStreamDelta } from './perga-v1.js';
import type { ApiDecodeFailure } from './types.js';

export const agentProviderSchema = z.string().min(1);
export type AgentProvider = z.infer<typeof agentProviderSchema>;

/** Provider IDs are opaque runtime identifiers and must round-trip unchanged. */
export function normalizeWireAgentProvider(raw: unknown): AgentProvider | null {
  const parsed = agentProviderSchema.safeParse(raw);
  return parsed.success ? parsed.data : null;
}

export const areaUpdatedSchema = z.object({
  ev: z.literal('area.updated'),
  data: areaDtoSchema,
});

export const areaDeletedSchema = z.object({
  ev: z.literal('area.deleted'),
  data: z.object({ id: z.string() }),
});

export const trackUpdatedSchema = z.object({
  ev: z.literal('track.updated'),
  data: trackDtoSchema,
});

export const trackDeletedSchema = z.object({
  ev: z.literal('track.deleted'),
  data: z.object({ id: z.string(), area_id: z.string() }),
});

/** One question of an ask; the first option is the recommended one, none means a free answer. */
export const askQuestionSchema = z.object({
  title: z.string(),
  detail: z.string().optional(),
  options: z.array(z.string()),
});

export const pergaReportUpdatedSchema = z.object({
  ev: z.literal('report.updated'),
  data: z.object({
    track_id: z.string().nullable().default(null),
    docRev: z.number().optional(),
    summary: z.string().nullish(),
  }),
});

export const pergaTaskUpdatedSchema = z.object({
  ev: z.literal('task.updated'),
  data: z.object({
    track_id: z.string().nullable().default(null),
  }),
});

export const pergaSessionUpdatedSchema = z.object({
  ev: z.literal('session.updated'),
  data: z.object({
    track_id: z.string(),
    session: agentSessionDtoSchema,
  }),
});

export const pergaTurnCompletedSchema = z.object({
  ev: z.literal('turn.completed'),
  data: z.object({
    track_id: z.string().nullable().default(null),
    turn: sessionTurnDtoSchema,
  }),
});

export const pergaAskChangedSchema = z.object({
  ev: z.literal('ask.changed'),
  data: z.object({
    track_id: z.string(),
    ask: askDtoSchema,
  }),
});

export const pergaMailReceivedSchema = z.object({
  ev: z.literal('mail.received'),
  data: z.object({
    track_id: z.string(),
    mail: mailDtoSchema,
  }),
});

export const pergaTerminalChangedSchema = z.object({
  ev: z.literal('terminal.changed'),
  data: z.object({
    track_id: z.string(),
    terminal: terminalDtoSchema,
  }),
});

export const pergaSessionTextDeltaSchema = z.object({
  ev: z.literal('session.text_delta'),
  data: sessionTextDeltaPayloadSchema.extend({
    createdAtMs: z.number().default(0),
  }),
});

export const pergaSessionThinkingDeltaSchema = z.object({
  ev: z.literal('session.thinking_delta'),
  data: sessionThinkingDeltaPayloadSchema.extend({
    createdAtMs: z.number().default(0),
  }),
});

export const pergaSessionToolProgressSchema = z.object({
  ev: z.literal('session.tool_progress'),
  data: sessionToolProgressPayloadSchema.extend({
    createdAtMs: z.number().default(0),
  }),
});

/** Keep this 1:1 with Perga `/api/v1/events` and historical event kinds. */
export const wireEventSchema = z.discriminatedUnion('ev', [
  z.object({ ev: z.literal('workspace.file_open_requested'), data: workspaceFileOpenRequestedSchema }),
  z.object({ ev: z.literal('memory.updated'), data: memoryUpdatedPayloadSchema }),
  z.object({ ev: z.literal('system.upgrade_status'), data: systemUpgradeStatusPayloadSchema }),
  z.object({ ev: z.literal('track.schedules.updated'), data: z.object({ trackId: z.string() }) }),
  areaUpdatedSchema,
  areaDeletedSchema,
  trackUpdatedSchema,
  trackDeletedSchema,
  pergaReportUpdatedSchema,
  pergaTaskUpdatedSchema,
  pergaSessionUpdatedSchema,
  pergaTurnCompletedSchema,
  pergaAskChangedSchema,
  z.object({ ev: z.literal('session.state_changed'), data: z.object({ trackId: z.string(), sessionId: z.string() }) }),
  z.object({ ev: z.literal('approval.changed'), data: z.object({ trackId: z.string(), sessionId: z.string() }) }),
  z.object({ ev: z.literal('read_receipt.updated'), data: readReceiptDtoSchema }),
  z.object({ ev: z.literal('approval.audit'), data: z.undefined() }),
  pergaMailReceivedSchema,
  pergaTerminalChangedSchema,
  pergaSessionTextDeltaSchema,
  pergaSessionThinkingDeltaSchema,
  pergaSessionToolProgressSchema,
  z.object({ ev: z.literal('session.runtime_status'), data: sessionRuntimeStatusPayloadSchema }),
  z.object({ ev: z.literal('session.task_progress'), data: sessionTaskProgressPayloadSchema }),
]);

export type Area = z.infer<typeof areaDtoSchema>;

export type WireEvent = z.infer<typeof wireEventSchema>;

export type WireEventDecodeResult =
  | Readonly<{ status: 'ready'; value: WireEvent }>
  | Readonly<{ status: 'failed'; error: ApiDecodeFailure }>;

/** Unknown or malformed frames are returned as data so callers can log and skip dispatch. */
export function decodeWireEvent(input: unknown): WireEventDecodeResult {
  if (typeof input === 'object' && input !== null && 'kind' in input && !('ev' in input)) {
    const perga = decodePergaEventEnvelope(input);
    if (perga.status === 'failed') return perga;
    if (perga.kind === 'runtime-status') return { status: 'ready', value: { ev: 'session.runtime_status', data: perga.payload } };
    if (perga.kind === 'task-progress') return { status: 'ready', value: { ev: 'session.task_progress', data: perga.payload } };
    if (perga.kind === 'read-receipt') return { status: 'ready', value: { ev: 'read_receipt.updated', data: perga.payload } };
    if (perga.kind === 'session-state') return { status: 'ready', value: {
      ev: 'session.state_changed', data: { trackId: perga.trackId, sessionId: perga.sessionId },
    } };
    if (perga.kind === 'approvals') return { status: 'ready', value: {
      ev: 'approval.changed', data: { trackId: perga.trackId, sessionId: perga.sessionId },
    } };
    if (perga.kind === 'approval-audit') return { status: 'ready', value: { ev: 'approval.audit', data: undefined } };
    if (perga.kind === 'upgrade-status') return { status: 'ready', value: { ev: 'system.upgrade_status', data: perga.payload } };
    if (perga.kind === 'file-open-requested') return { status: 'ready', value: { ev: 'workspace.file_open_requested', data: perga.payload } };
    if (perga.kind === 'memory-updated') return { status: 'ready', value: { ev: 'memory.updated', data: perga.payload } };
    if (perga.kind === 'delta') {
      const delta: PergaStreamDelta = perga.delta;
      if (delta.kind === 'session.text_delta') {
        return { status: 'ready', value: { ev: 'session.text_delta', data: { sessionId: delta.sessionId, turnId: delta.turnId, delta: delta.delta, sequence: delta.sequence, createdAtMs: delta.createdAtMs } } };
      }
      if (delta.kind === 'session.thinking_delta') {
        return { status: 'ready', value: { ev: 'session.thinking_delta', data: { sessionId: delta.sessionId, turnId: delta.turnId, delta: delta.delta, createdAtMs: delta.createdAtMs } } };
      }
      return {
        status: 'ready',
        value: {
          ev: 'session.tool_progress',
          data: {
            sessionId: delta.sessionId,
            turnId: delta.turnId,
            phase: delta.phase,
            callId: delta.callId,
            name: delta.name,
            args: delta.args,
            isError: delta.isError,
            createdAtMs: delta.createdAtMs,
          },
        },
      };
    }
    const evt = perga.event;
    switch (evt.kind) {
      case 'area.updated':
        return { status: 'ready', value: { ev: 'area.updated', data: evt.payload } };
      case 'area.deleted':
        return { status: 'ready', value: { ev: 'area.deleted', data: { id: evt.payload.id } } };
      case 'track.deleted':
        return { status: 'ready', value: { ev: 'track.deleted', data: { id: evt.payload.id, area_id: evt.payload.areaId } } };
      case 'track.schedules.updated':
        return { status: 'ready', value: { ev: 'track.schedules.updated', data: evt.payload } };
      case 'track.updated':
        return { status: 'ready', value: { ev: 'track.updated', data: evt.payload } };
      case 'report.updated':
        return { status: 'ready', value: { ev: 'report.updated', data: { track_id: evt.trackId, docRev: evt.payload.docRev, summary: evt.payload.summary } } };
      case 'task.updated':
        return { status: 'ready', value: { ev: 'task.updated', data: { track_id: evt.trackId } } };
      case 'session.updated':
        return { status: 'ready', value: { ev: 'session.updated', data: { track_id: evt.trackId ?? evt.payload.trackId, session: evt.payload } } };
      case 'turn.completed':
        return { status: 'ready', value: { ev: 'turn.completed', data: { track_id: evt.trackId, turn: evt.payload } } };
      case 'ask.changed':
        return { status: 'ready', value: { ev: 'ask.changed', data: { track_id: evt.trackId ?? evt.payload.trackId, ask: evt.payload } } };
      case 'mail.received':
        return { status: 'ready', value: { ev: 'mail.received', data: { track_id: evt.trackId ?? evt.payload.toTrackId, mail: evt.payload } } };
      case 'terminal.changed':
        return { status: 'ready', value: { ev: 'terminal.changed', data: { track_id: evt.trackId ?? evt.payload.trackId, terminal: evt.payload } } };
    }
  }
  const parsed = wireEventSchema.safeParse(input);
  if (!parsed.success) {
    return {
      status: 'failed',
      error: { kind: 'decode', message: 'Wire event did not match its schema', cause: parsed.error },
    };
  }
  return { status: 'ready', value: parsed.data };
}

// Unified session DTOs are defined once in perga-v1.ts for HTTP and event consumers.
