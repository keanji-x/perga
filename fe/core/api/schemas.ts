import { trackDtoToWire } from './track-projection.js';
// Zod schemas for WS `/api/events` payloads, mirroring the kernel's `Event` enum and entity types.
// On parse failure, callers log and skip dispatch — never throw.
import { z } from 'zod';

import {
agentSessionDtoSchema,
askDtoSchema,
decodePergaEventEnvelope,
mailDtoSchema,
readReceiptDtoSchema,
sessionTextDeltaPayloadSchema,
sessionThinkingDeltaPayloadSchema,
sessionToolProgressPayloadSchema,
sessionTurnDtoSchema,
systemUpgradeStatusPayloadSchema,
terminalDtoSchema,
trackDtoSchema,
type PergaStreamDelta,
} from './perga-v1.js';
import type { ApiDecodeFailure } from './types.js';

/** `model::AreaKind`. Defaults to `'user'` so legacy wire payloads parse. */
export const areaKindSchema = z.enum(['user', 'system']).default('user');

/** `model::Area` — area metadata row. */
export const areaSchema = z.object({
  id: z.string(),
  name: z.string(),
  color: z.string(),
  sort: z.number(),
  kind: areaKindSchema,
  default_template_id: z.string().nullable().default(null),
  default_cwd: z.string().nullable().default(null),
  execution_target_id: z.string().optional(),
  created_at: z.number(),
  updated_at: z.number(),
});

/** Required server projection, shared by HTTP, events and the domain reader. */
export const workspaceIsolationSchema = z.enum(['managed', 'worktree', 'none']);
export const trackWorkspaceSchema = z.object({
  isolation: workspaceIsolationSchema,
  kind: z.enum(['managed', 'attached']),
  path: z.string(),
  frozen_at: z.number().nullable(),
  worktree: z.string().optional(),
});

const trackObjectSchema = z.object({
  id: z.string(),
  area_id: z.string(),
  title: z.string(),
  sort: z.number(),
  pinned_at: z.number().nullable().default(null),
  /** Unix-ms time the track was closed, or `null` while it is open; a pre-#1876 frame has none and reads as open. */
  closed_at: z.number().nullable().default(null),
  admin_granted_at: z.number().nullable().default(null),
  /** Defaulted to `""` for legacy replay payloads; production rows always carry an absolute path. */
  cwd: z.string().default(''),
  templateId: trackDtoSchema.shape.templateId,
  templateTitle: trackDtoSchema.shape.templateTitle,
  templateInputs: trackDtoSchema.shape.templateInputs.optional(),
  templateHeaderView: trackDtoSchema.shape.templateHeaderView,
  template_id: z.string().nullable().default(null),
  plugin_scope: z.string().nullable().default(null),
  purpose: z.string().nullable().default(null),
  /** Opaque bound-template input JSON; the frontend never interprets it. */
  template_input: z.unknown().default(null),
  /** Server-owned provenance; both are `null` together for tracks that came from anywhere else. */
  recipe_id: z.string().nullable().default(null),
  recipe_revision: z.number().nullable().default(null),
  /** Server-owned provenance of a Track another Track's Planner created; both `null` together otherwise. */
  creator_track_id: z.string().nullable().default(null),
  creator_key: z.string().nullable().default(null),
  /** The typed workspace; `cwd` above is a projection of `workspace.path`. */
  workspace: trackWorkspaceSchema,
  created_at: z.number(),
  updated_at: z.number(),
});

export const trackSchema = trackObjectSchema;

export const runtimeKindSchema = z.enum(['terminal', 'managed-assistant']);

export const agentProviderSchema = z.enum(['codex', 'claude', 'opencode', 'grok', 'antigravity']);
export type AgentProvider = z.infer<typeof agentProviderSchema>;

/** Normalizes Perga wire provider IDs (`"acp:antigravity"`, `"acp:opencode"`) into canonical frontend `AgentProvider` values. */
export function normalizeWireAgentProvider(raw: unknown): AgentProvider | null {
  const candidate = typeof raw === 'string' && raw.startsWith('acp:') ? raw.slice(4) : raw;
  const parsed = agentProviderSchema.safeParse(candidate);
  return parsed.success ? parsed.data : null;
}

/** Maps canonical frontend `AgentProvider` values to Perga wire provider IDs (`"acp:antigravity"`, `"acp:opencode"`). */
export function toWireAgentProvider(provider: AgentProvider): string {
  if (provider === 'antigravity' || provider === 'opencode') return `acp:${provider}`;
  return provider;
}

export const workerSessionStateSchema = z.enum([
  'starting',
  'running',
  'idle',
  'turn_pending',
  'failed',
  'exited',
  'superseded',
]);
export type WorkerSessionState = z.infer<typeof workerSessionStateSchema>;

export const cardRuntimeViewSchema = z.object({
  worker_session_id: z.string(),
  kind: runtimeKindSchema,
  status: workerSessionStateSchema,
  // Older servers and persisted Card event snapshots do not carry activity time.
  updated_at_ms: z.number().optional(),
  provider: agentProviderSchema.optional(),
  terminal_id: z.string().optional(),
  thread_id: z.string().optional(),
  session_id: z.string().optional(),
  source: z.string().optional(),
  thread_status: z.string().optional(),
  // Persisted Card snapshots and cards without a completed turn omit it.
  last_turn_completed_ms: z.number().optional(),
  // #2516: a Claude card's child records the version it started on; every other runtime omits it.
  claude_version: z.string().optional(),
});
export type CardRuntimeView = z.infer<typeof cardRuntimeViewSchema>;

/** `model::Card` — card row. `payload` is opaque `serde_json::Value`. */
export const cardSchema = z.object({
  id: z.string(),
  track_id: z.string(),
  kind: z.string(),
  // Option<String> is omitted (rather than serialized as null) on the wire.
  title: z.string().optional(),
  sort: z.number(),
  // serde_json::Value on the wire: arbitrary JSON. Kernel never inspects.
  payload: z.unknown(),
  runtime: cardRuntimeViewSchema.optional(),
  // Wire payloads from older servers may omit the field; `.default(true)` mirrors the kernel default.
  deletable: z.boolean().default(true),
  created_at: z.number(),
  updated_at: z.number(),
});

/** `model::Overlay` — plugin overlay row. `payload` is opaque JSON. */
export const overlaySchema = z.object({
  id: z.string(),
  plugin_id: z.string(),
  // Documented as "track" | "card" but kept open for forward-compat.
  entity_kind: z.string(),
  entity_id: z.string(),
  kind: z.string(),
  payload: z.unknown(),
  updated_at: z.number(),
});

export const areaUpdatedSchema = z.object({
  ev: z.literal('area.updated'),
  data: areaSchema,
});

export const areaDeletedSchema = z.object({
  ev: z.literal('area.deleted'),
  data: z.object({ id: z.string() }),
});

export const trackUpdatedSchema = z.object({
  ev: z.literal('track.updated'),
  data: trackObjectSchema.extend({
      agent_message: z.string().optional(),
    }),
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
]);

export type Area = z.infer<typeof areaSchema>;

export type WireEvent = z.infer<typeof wireEventSchema>;

export type WireEventDecodeResult =
  | Readonly<{ status: 'ready'; value: WireEvent }>
  | Readonly<{ status: 'failed'; error: ApiDecodeFailure }>;

/** Unknown or malformed frames are returned as data so callers can log and skip dispatch. */
export function decodeWireEvent(input: unknown): WireEventDecodeResult {
  if (typeof input === 'object' && input !== null && 'kind' in input && !('ev' in input)) {
    const perga = decodePergaEventEnvelope(input);
    if (perga.status === 'failed') return perga;
    if (perga.kind === 'read-receipt') return { status: 'ready', value: { ev: 'read_receipt.updated', data: perga.payload } };
    if (perga.kind === 'session-state') return { status: 'ready', value: {
      ev: 'session.state_changed', data: { trackId: perga.trackId, sessionId: perga.sessionId },
    } };
    if (perga.kind === 'approvals') return { status: 'ready', value: {
      ev: 'approval.changed', data: { trackId: perga.trackId, sessionId: perga.sessionId },
    } };
    if (perga.kind === 'approval-audit') return { status: 'ready', value: { ev: 'approval.audit', data: undefined } };
    if (perga.kind === 'upgrade-status') return { status: 'ready', value: { ev: 'system.upgrade_status', data: perga.payload } };
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
      case 'area.updated': {
        const a = evt.payload;
        return {
          status: 'ready',
          value: {
            ev: 'area.updated',
            data: {
              id: a.id,
              name: a.name,
              color: a.color,
              sort: a.sortOrder,
              kind: 'user',
              default_template_id: null,
              default_cwd: a.defaultCwd ?? null,
              ...(a.executionTargetId !== undefined ? { execution_target_id: a.executionTargetId } : {}),
              created_at: a.createdAtMs,
              updated_at: a.updatedAtMs,
            },
          },
        };
      }
      case 'area.deleted':
        return { status: 'ready', value: { ev: 'area.deleted', data: { id: evt.payload.id } } };
      case 'track.deleted':
        return { status: 'ready', value: { ev: 'track.deleted', data: { id: evt.payload.id, area_id: evt.payload.areaId } } };
      case 'track.schedules.updated':
        return { status: 'ready', value: { ev: 'track.schedules.updated', data: evt.payload } };
      case 'track.updated':
        return { status: 'ready', value: { ev: 'track.updated', data: trackDtoToWire(evt.payload) } };
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
