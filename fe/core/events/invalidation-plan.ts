import type { PergaStreamDelta } from "../api/perga-v1.js";
import type { Area,WireEvent } from "../api/schemas.js";
import * as eventKeys from './query-keys.js';

export type EventKind = WireEvent['ev'];
export type EventOf<K extends EventKind> = Extract<WireEvent, { ev: K }>;
export type QueryKey = readonly unknown[];

export type CacheWrite = Readonly<{ key: QueryKey; mode: 'replace-existing-area'; value: Area }>
  | Readonly<{ key: QueryKey; mode: 'reset-query' }>
  | Readonly<{ key: QueryKey; mode: 'session-delta'; delta: PergaStreamDelta }>
  | Readonly<{ key: QueryKey; mode: 'session-turn-completed'; turnId: string }>
  | Readonly<{ mode: 'area-deleted'; areaId: string }>;
export type InvalidationPlan = Readonly<{
  invalidate: readonly QueryKey[];
  remove: readonly QueryKey[];
  writeThrough: readonly CacheWrite[];
}>;
export type InvalidationContext = Readonly<{
  findTrackOwningCard(cardId: string): string | null;
}>;

type PlannedPolicy<K extends EventKind = EventKind> = Readonly<{
  type: 'plan';
  plan(event: EventOf<K>, context: InvalidationContext): InvalidationPlan;
}>;
type NoopPolicy = Readonly<{ type: 'noop'; reason: string }>;
export type InvalidationPolicy<K extends EventKind = EventKind> = PlannedPolicy<K> | NoopPolicy;
type PolicyMap = { readonly [K in EventKind]: InvalidationPolicy<K> };

const emptyContext: InvalidationContext = Object.freeze({ findTrackOwningCard: () => null });

export function noop(reason: string): NoopPolicy {
  if (reason.length === 0) throw new TypeError('A no-op invalidation policy requires a reason');
  return { type: 'noop', reason };
}

function plan<K extends EventKind>(
  create: (event: EventOf<K>, context: InvalidationContext) => InvalidationPlan,
): PlannedPolicy<K> {
  return { type: 'plan', plan: create };
}

function result(
  invalidate: readonly QueryKey[],
  remove: readonly QueryKey[] = [],
  writeThrough: readonly CacheWrite[] = [],
): InvalidationPlan {
  return { invalidate, remove, writeThrough };
}

function taskVerdicts(trackId: string | null): readonly QueryKey[] {
  return [trackId === null ? eventKeys.trackReportPrefix() : eventKeys.trackReport(trackId)];
}

/**
 * Keyed by track; the bare prefix only when the track cannot be resolved (a key with no
 * active observer only marks entries stale, so that fallback is cheap).
 */
function trackSnapshots(trackId: string | null): QueryKey {
  return trackId === null ? eventKeys.trackDetailPrefix() : eventKeys.trackDetail(trackId);
}

/** Session metadata lives in the aggregate. Detached stores do not fan out globally. */
export function conversationInputMetadata(trackId: string | null): readonly QueryKey[] {
  return trackId === null ? [] : [eventKeys.trackDetail(trackId)];
}

export function defineInvalidationPolicies<T extends PolicyMap>(value: T): T {
  return value;
}

function policies(): PolicyMap {
  return defineInvalidationPolicies({
'area.updated': plan((event) => result(
    [eventKeys.areas()],
    [],
    [{ key: eventKeys.areas(), mode: 'replace-existing-area', value: event.data }],
  )),
'area.deleted': plan((event) => result([eventKeys.areas(), eventKeys.overlaysByKind('track'), eventKeys.trackReportPrefix()], [],
    [{ mode: 'area-deleted', areaId: event.data.id }])),
'track.updated': plan((event) => result([
    eventKeys.overlaysByKind('track'),
    eventKeys.tracksInArea(event.data.area_id), eventKeys.trackDetail(event.data.id), eventKeys.trackTemplate(event.data.id),
    eventKeys.trackReportPrefix(),
    eventKeys.trackViews(event.data.id),
  ])),
  'track.schedules.updated': plan((event) => result([
    eventKeys.trackSchedules(event.data.trackId),
    eventKeys.trackViews(event.data.trackId),
  ])),
  'track.deleted': plan((event) => result(
    [
      eventKeys.tracksInArea(event.data.area_id), eventKeys.overlaysByKind('track'),
      eventKeys.trackReportPrefix(),
    ],
    [eventKeys.trackDetail(event.data.id), eventKeys.trackReport(event.data.id), eventKeys.trackViews(event.data.id)],
  )),
'report.updated': plan((event) => result([
    ...taskVerdicts(event.data.track_id ?? null),
    ...(event.data.track_id
      ? [eventKeys.trackDetail(event.data.track_id), eventKeys.trackViews(event.data.track_id)]
      : [eventKeys.trackViewsPrefix()]),
  ])),
'task.updated': plan((event) => result([
    eventKeys.overlaysByKind('track'),
    ...taskVerdicts(event.data.track_id ?? null),
    ...(event.data.track_id
      ? [eventKeys.trackDetail(event.data.track_id), eventKeys.trackViews(event.data.track_id)]
      : [eventKeys.trackViewsPrefix()]),
  ])),
'system.upgrade_status': noop('The upgrade store consumes system status independently of query caches.'),
'session.runtime_status': plan((event) => result([eventKeys.plannerRun(event.data.sessionId)])),
'session.task_progress': plan((event) => result([eventKeys.plannerRun(event.data.sessionId)])),
'session.updated': plan((event) => {
    const sessionId = event.data.session?.id;
    return result([
      eventKeys.overlaysByKind('track'),
      trackSnapshots(event.data.track_id ?? null),
      ...(sessionId ? [eventKeys.plannerRun(sessionId), eventKeys.transcript(sessionId)] : []),
    ]);
  }),
'turn.completed': plan((event, context) => {
    const sessionId = event.data.turn?.sessionId;
    const turnId = event.data.turn?.id;
    const trackId = event.data.track_id ?? (sessionId ? context.findTrackOwningCard(sessionId) : null);
    return result([
      eventKeys.overlaysByKind('track'),
      ...(sessionId ? [eventKeys.plannerRun(sessionId), eventKeys.transcript(sessionId), eventKeys.modelCatalogForCard(sessionId)] : []),
      ...(trackId === null ? [eventKeys.trackDetailPrefix()] : [
        eventKeys.trackDetail(trackId),
      ]),
    ], [], sessionId && turnId ? [{
      key: eventKeys.sessionLive(sessionId),
      mode: 'session-turn-completed',
      turnId,
    }] : []);
  }),
'session.state_changed': plan((event) => result([
    eventKeys.overlaysByKind('track'),
    eventKeys.plannerRun(event.data.sessionId),
  ])),
'approval.changed': plan((event) => result([
    eventKeys.overlaysByKind('track'),
    eventKeys.trackDetail(event.data.trackId),
    eventKeys.trackLists(),
    eventKeys.plannerRun(event.data.sessionId),
    eventKeys.transcript(event.data.sessionId),
  ])),
'read_receipt.updated': plan(() => result([eventKeys.readReceipts()])),
'approval.audit': plan(() => result([])),
'ask.changed': plan((event) => result([
    eventKeys.overlaysByKind('track'),
    eventKeys.trackDetail(event.data.track_id),
    eventKeys.trackLists(),
  ])),
'mail.received': plan((event) => result([
    eventKeys.trackDetail(event.data.track_id),
  ])),
'terminal.changed': plan((event) => result([
    eventKeys.trackDetail(event.data.track_id),
    eventKeys.trackTerminals(event.data.track_id),
  ])),
'session.text_delta': plan((event) => result([], [], [{
    key: eventKeys.sessionLive(event.data.sessionId),
    mode: 'session-delta',
    delta: {
      kind: 'session.text_delta',
      sessionId: event.data.sessionId,
      turnId: event.data.turnId,
      delta: event.data.delta,
      sequence: event.data.sequence,
      createdAtMs: event.data.createdAtMs,
    },
  }])),
'session.thinking_delta': plan((event) => result([], [], [{
    key: eventKeys.sessionLive(event.data.sessionId),
    mode: 'session-delta',
    delta: {
      kind: 'session.thinking_delta',
      sessionId: event.data.sessionId,
      turnId: event.data.turnId,
      delta: event.data.delta,
      createdAtMs: event.data.createdAtMs,
    },
  }])),
'session.tool_progress': plan((event) => result([], [], [{
    key: eventKeys.sessionLive(event.data.sessionId),
    mode: 'session-delta',
    delta: {
      kind: 'session.tool_progress',
      sessionId: event.data.sessionId,
      turnId: event.data.turnId,
      phase: event.data.phase,
      callId: event.data.callId,
      name: event.data.name ?? null,
      args: event.data.args ?? null,
      isError: event.data.isError === true,
      createdAtMs: event.data.createdAtMs,
    },
  }]))
});
}

export function invalidationPlanFor(
  event: WireEvent,
  context: InvalidationContext = emptyContext,
): InvalidationPlan {
  const policy: InvalidationPolicy | undefined = policies()[event.ev];
  if (policy === undefined) return result([]);
  if (policy.type === 'noop') return result([]);
  return policy.plan(event, context);
}
