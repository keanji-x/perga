import { describe, expect, it } from 'vitest';

import { agentSessionDtoSchema } from '../api/perga-v1.js';
import type { HarnessPhaseTag } from '../api/generated/wire.js';

import {
  agentSessionDtoToConversation,
  CONVERSATION_CREATE_TEXT,
  CONVERSATION_NAME_MAX,
  conversationCreateFailure,
  conversationName, conversationNameFrom,
  createSerialWriter, createTrackConversationOperation,
  isOptimisticConversationTurn, isQueuedConversationTurn, kernelQueuesInput,
  mergeTranscript,
  plannerQueueWriteFailure,
  retireFollowedThoughts,
  sendPlannerInputOperation,
  trackConversationCardId,
  type Conversation, type ConversationTurn, type OptimisticConversationTurn,
  type TranscriptEntry
} from './conversation.js';

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return { provider: 'codex' as const,
    id: 'c1', trackId: 'w1', trackTitle: 'Ship the rewrite', title: null,
    kind: 'codex', state: 'idle', updatedAt: 0, turns: 0,
    ...overrides,
  };
}

describe('conversationName', () => {
  it('prefers the conversation\'s own name', () => {
    expect(conversationName(conversation({ title: 'Why the resolver drops a hop' })))
      .toBe('Why the resolver drops a hop');
  });

  it('falls back to the kind, never to the track', () => {
    const nameless = conversation({ trackTitle: 'Ship the rewrite' });
    expect(conversationName(nameless)).toBe('Codex');
    expect(conversationName(nameless)).not.toBe('Ship the rewrite');
  });
});

it('recognizes only local pending user messages and their queue feedback', () => {
  const echo: OptimisticConversationTurn = { id: 'echo', author: 'you', text: 'same', atMs: 1, localSend: true, queued: true };
  const stored: ConversationTurn = { id: 'stored', author: 'you', text: 'same', atMs: 1 };
  expect(isOptimisticConversationTurn(echo)).toBe(true);
  expect(isOptimisticConversationTurn(stored)).toBe(false);
  expect(isQueuedConversationTurn(echo)).toBe(true);
  const unqueued: OptimisticConversationTurn = { ...echo, queued: false };
  expect(isQueuedConversationTurn(unqueued)).toBe(false);
  expect(isQueuedConversationTurn(stored)).toBe(false);
});

/*
 * `HarnessState::can_issue_turn()` accepts only `Idle` and `TurnCompleted`; `resumed` reads
 * wrong at a glance but does not issue.
 */
describe('kernelQueuesInput', () => {
  /* The table is bound to the enum by the compiler; both columns are `can_issue_turn()` read by a person. */
  const QUEUES: Readonly<Record<HarnessPhaseTag, boolean>> = Object.freeze({
    idle: false,
    turn_completed: false,
    pending_thread_start: true,
    issuing_turn: true,
    compacting: true,
    issuing_interrupt: true,
    turn_running: true,
    resumed: true,
    wedged: true,
  } satisfies Record<HarnessPhaseTag, boolean>);

  it.each(Object.entries(QUEUES))('%s queues: %s', (phase, queues) => {
    expect(kernelQueuesInput(phase as HarnessPhaseTag)).toBe(queues);
  });

  it('reads an unknown phase as queueing', () => {
    expect(kernelQueuesInput(null)).toBe(true);
  });
});

describe('conversationNameFrom', () => {
  it('takes the first line, not the first paragraph', () => {
    expect(conversationNameFrom('Fix the resolver\n\nHere is the stack trace:\n  at walk()'))
      .toBe('Fix the resolver');
  });

  it('truncates to one name-length, ellipsis included in the budget', () => {
    const name = conversationNameFrom('x'.repeat(200));
    expect(name).toHaveLength(CONVERSATION_NAME_MAX);
    expect(name?.endsWith('…')).toBe(true);
  });

  it('leaves a name that already fits exactly alone', () => {
    const exact = 'y'.repeat(CONVERSATION_NAME_MAX);
    expect(conversationNameFrom(exact)).toBe(exact);
  });

  it.each([['empty', ''], ['whitespace', '   \n  ']])('has no name for a %s message', (_label, text) => {
    expect(conversationNameFrom(text)).toBeNull();
  });
});

describe('track conversations', () => {
  const row = agentSessionDtoSchema.parse({
    id: 'card-3', trackId: 'track-1', title: null, isLead: false, provider: 'codex',
    status: 'starting', contextUsage: null, createdAtMs: 1, updatedAtMs: 7,
  });

  it('maps session identity, role, provider and timestamps into a unified conversation', () => {
    expect(agentSessionDtoToConversation(row)).toEqual({
      id: 'card-3', trackId: 'track-1', title: null, kind: 'agent', isLead: false,
      provider: 'codex', state: 'starting', updatedAt: 7, archivedAt: null, lastTurnCompletedAt: null,
    });
    expect(agentSessionDtoToConversation({ ...row, isLead: true, provider: 'acp:opencode', status: 'queued' }))
      .toMatchObject({ kind: 'agent', isLead: true, provider: 'opencode', state: 'turn_pending' });
  });

  it('requires the declared provider on the session DTO', () => {
    const missing: Partial<typeof row> = { ...row };
    delete missing.provider;
    expect(agentSessionDtoSchema.safeParse(missing).success).toBe(false);
  });

  it.each([
    [{ kind: 'http', status: 409, code: 'idempotency_key_exhausted', message: 'key exhausted' }, 'exhausted'],
    [{ kind: 'http', status: 409, code: 'idempotency_key_reused', message: 'already used with different payload' }, 'stale-payload'],
    [{ kind: 'http', status: 409, code: 'conflict', message: 'card already exists' }, 'exists'],
    [{ kind: 'http', status: 404, code: 'not_found', message: 'track not found' }, 'gone'],
    [{ kind: 'http', status: 400, code: 'bad_request', message: 'text must not be blank' }, 'blocked'],
    [{ kind: 'http', status: 503, code: 'service_unavailable', message: 'try later' }, 'retry'],
    [{ kind: 'transport', message: 'request failed' }, 'retry'],
  ] as const)('classifies conversation create failure %o as %s', (failure, expected) => {
    expect(conversationCreateFailure(failure).kind).toBe(expected);
  });

  /* #2131 S7: the draft's sentence is the class's. An ambiguous create says only that it is unconfirmed; any other
     answer says the server's reason, or the refusal when it gave none. No transport text, no connection sentence. */
  it.each([
    [{ kind: 'transport', message: 'Transport request failed' }, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'decode', message: 'API response did not match its schema' }, CONVERSATION_CREATE_TEXT.unknown],
    [null, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'unauthorized', status: 401, code: 'unauthorized', message: 'signed out' }, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'http', status: 500, code: 'internal', message: 'db exploded' }, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'http', status: 503, code: 'service_unavailable', message: 'try later' }, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'http', status: 409, code: 'idempotency_key_concurrent', message: 'in flight' }, CONVERSATION_CREATE_TEXT.unknown],
    [{ kind: 'http', status: 400, code: 'bad_request', message: 'text must not be blank' }, 'text must not be blank'],
    [{ kind: 'http', status: 400, code: 'bad_request', message: '' }, CONVERSATION_CREATE_TEXT.refused],
    [{ kind: 'http', status: 409, code: 'conflict', message: 'card already exists' }, 'card already exists'],
    [{ kind: 'http', status: 409, code: 'idempotency_key_reused', message: 'key reused' }, 'key reused'],
    [{ kind: 'http', status: 409, code: 'idempotency_key_exhausted', message: 'key exhausted' }, 'key exhausted'],
  ] as const)('says %o as %s', (failure, text) => {
    expect(conversationCreateFailure(failure).message).toBe(text);
  });

  it('leaves the track title absent rather than inventing one, and names the row Assistant', () => {
    const conversation = agentSessionDtoToConversation(row);
    expect(conversationName(conversation)).toBe('Assistant');
    expect(Object.hasOwn(conversation, 'trackTitle')).toBe(false);
    expect(Object.hasOwn(conversation, 'turns')).toBe(false);
  });

  it('posts the first message to the track, carrying the key as a header', () => {
    const operation = createTrackConversationOperation('track 1', 'hello', 'key-a', 'claude', { model: null, reasoning_effort: null });
    expect(operation.method).toBe('POST');
    expect(operation.path).toBe('/api/v1/tracks/track%201/sessions');
    expect(operation.body).toEqual({ title: 'hello', prompt: 'hello', provider: 'claude' });
    expect(operation.headers).toEqual({ 'Idempotency-Key': 'key-a' });
    const firstInput = { inputId: 'input', turnId: 'turn', sessionId: row.id,
      deliveryState: 'accepted', turnStatus: 'queued' };
    expect(operation.responseSchema.parse({ session: { ...row, trackId: 'track 1' }, firstInput }).conversation.kind).toBe('agent');
    expect(operation.responseSchema.safeParse({ session: row }).success).toBe(false);
    expect(operation.responseSchema.safeParse({ session: row, firstInput }).success).toBe(false);
  });

  it('sends a planner message under the key its retries reuse', () => {
    const operation = sendPlannerInputOperation('card 1', 'hello', ['image.png'], 'key-a');
    expect(operation.responseSchema.safeParse({
      id: 'trn_old', sessionId: 'card 1', status: 'completed', startedAtMs: 1,
    }).success).toBe(false);
    expect(operation.method).toBe('POST');
    expect(operation.path).toBe('/api/v1/sessions/card%201/messages');
    expect(operation.body).toStrictEqual({ content: 'hello', mode: 'default' });
    expect(operation.headers).toEqual({ 'Idempotency-Key': 'key-a' });
    expect(sendPlannerInputOperation('card 1', 'hello', [], 'key-a').body)
      .toStrictEqual({ content: 'hello', mode: 'default' });
  });

  /* A golden: the value is copied from the server's own test of `conversation_keys.rs`. */
  it('derives the same card id the server does, from its own namespace', () => {
    expect(trackConversationCardId('track-1', 'key-a')).toBe('conv-55cef7267426fe78493bdd46ca6b1220');
    expect(trackConversationCardId('track-1', 'key-b')).not.toBe(trackConversationCardId('track-1', 'key-a'));
    expect(trackConversationCardId('track-2', 'key-a')).not.toBe(trackConversationCardId('track-1', 'key-a'));
  });
});

describe('mergeTranscript', () => {
  const thought = {
    id: 'thought', author: 'activity' as const, verb: 'Thought', target: null,
    state: 'done' as const, durationMs: null, detail: null, tool: null, atMs: 1,
  };
  const echo: ConversationTurn = { id: 'echo', author: 'you', text: 'next', atMs: 2 };

  it('drops a completed tail thought when an echo follows it', () => {
    expect(mergeTranscript([thought], [echo])).toEqual([echo]);
  });

  it('keeps a completed tail thought until an echo exists', () => {
    expect(mergeTranscript([thought], [])).toEqual([thought]);
  });

  it('retires an empty thought below an outcome when an echo or a persisted message follows', () => {
    const outcome: TranscriptEntry = { id: 'outcome', author: 'turn', turnId: 'turn', status: 'interrupted', elapsedMs: null, atMs: 2 };
    const server = [thought, outcome];
    expect(mergeTranscript(server, [echo])).toEqual([outcome, echo]);
    expect(retireFollowedThoughts([...server, echo])).toEqual([outcome, echo]);
  });
});

/* A stale entry can be rewritten against the revision that beat you; a gone one cannot. */
describe('plannerQueueWriteFailure', () => {
  const stale = (body: unknown) => plannerQueueWriteFailure(
    { kind: 'http' as const, status: 409, code: 'planner_input_stale', message: 'stale', body },
    'fallback',
  );

  it('reads the winning text and revision out of a 409', () => {
    expect(stale({
      error: 'stale', code: 'planner_input_stale', entry_id: 'e1', text: 'what won', rev: 6,
    })).toEqual({ kind: 'stale', entryId: 'e1', text: 'what won', rev: 6 });
  });

  it('falls back to a plain failure when a 409 body is unreadable', () => {
    expect(stale({ code: 'planner_input_stale' })).toEqual({ kind: 'failed', message: 'stale' });
    expect(stale('not an object')).toEqual({ kind: 'failed', message: 'stale' });
  });

  it('reads a 404 as the entry having left the queue', () => {
    expect(plannerQueueWriteFailure(
      { kind: 'http', status: 404, code: 'not_found', message: 'gone', body: null }, 'fallback',
    )).toEqual({ kind: 'gone' });
  });

  it('reads the steer refusal as the entry still waiting, and nothing else as it', () => {
    expect(plannerQueueWriteFailure({
      kind: 'http', status: 409, code: 'planner_steer_no_running_turn', message: 'no turn',
      body: { error: 'no turn', code: 'planner_steer_no_running_turn', entry_id: 'e1', phase: 'idle' },
    }, 'fallback')).toEqual({ kind: 'not_running' });
    expect(plannerQueueWriteFailure({
      kind: 'http', status: 409, code: 'conflict', message: 'shutting down',
      body: { error: 'shutting down', code: 'conflict' },
    }, 'fallback')).toEqual({ kind: 'failed', message: 'shutting down' });
  });

  it('reads a timed-out steer as unanswered, distinct from the entry still waiting', () => {
    expect(plannerQueueWriteFailure({
      kind: 'http', status: 409, code: 'planner_steer_unknown_outcome', message: 'timed out',
      body: {
        error: 'codex did not answer in time', code: 'planner_steer_unknown_outcome',
        entry_id: 'e1', phase: 'turn_running',
      },
    }, 'fallback')).toEqual({ kind: 'unanswered' });
    expect(plannerQueueWriteFailure({
      kind: 'http', status: 500, code: 'planner_steer_unknown_outcome', message: 'boom',
      body: { error: 'boom', code: 'planner_steer_unknown_outcome' },
    }, 'fallback')).toEqual({ kind: 'failed', message: 'boom' });
  });

  it('reports anything else as a failure in the server\'s words, or the fallback when it gave none', () => {
    expect(plannerQueueWriteFailure(
      { kind: 'http', status: 500, code: 'internal', message: 'boom', body: null }, 'fallback',
    )).toEqual({ kind: 'failed', message: 'boom' });
    expect(plannerQueueWriteFailure(
      { kind: 'http', status: 500, code: 'internal', message: '', body: null }, 'fallback',
    )).toEqual({ kind: 'failed', message: 'fallback' });
    expect(plannerQueueWriteFailure(
      { kind: 'unauthorized', status: 401, code: 'unauthorized', message: 'signed out' }, 'fallback',
    )).toEqual({ kind: 'failed', message: 'fallback' });
    expect(plannerQueueWriteFailure({ kind: 'transport', message: 'dropped' }, 'fallback'))
      .toEqual({ kind: 'failed', message: 'fallback' });
    expect(plannerQueueWriteFailure(null, 'fallback')).toEqual({ kind: 'failed', message: 'fallback' });
  });
});

/* `PUT /planner/model` consults codex's catalog before it writes, so back-to-back requests can land out of order. */
describe('createSerialWriter', () => {
  function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>((r) => { resolve = r; });
    return { promise, resolve };
  }

  it('never has two writes in flight at once', async () => {
    const gates = [deferred<string>(), deferred<string>()];
    const started: string[] = [];
    let live = 0;
    let maxLive = 0;
    const write = createSerialWriter(async (name: string) => {
      started.push(name);
      live += 1;
      maxLive = Math.max(maxLive, live);
      const value = await gates[started.length - 1].promise;
      live -= 1;
      return value;
    });

    const first = write('a');
    const second = write('b');
    expect(started).toEqual(['a']);
    gates[0].resolve('a-done');
    await Promise.resolve();
    gates[1].resolve('b-done');
    await Promise.all([first, second]);

    expect(started).toEqual(['a', 'b']);
    expect(maxLive).toBe(1);
  });

  it('lets the last intent be the one that lands', async () => {
    const gate = deferred<string>();
    const committed: string[] = [];
    const write = createSerialWriter(async (name: string) => {
      if (name === 'a') await gate.promise;
      committed.push(name);
      return name;
    });

    const first = write('a');
    const second = write('b');
    gate.resolve('go');
    await Promise.all([first, second]);

    expect(committed).toEqual(['a', 'b']);
    expect(committed.at(-1)).toBe('b');
  });

  it('collapses superseded intents instead of replaying every one', async () => {
    const gate = deferred<string>();
    const committed: string[] = [];
    const write = createSerialWriter(async (name: string) => {
      if (name === 'a') await gate.promise;
      committed.push(name);
      return name;
    });

    const all = [write('a'), write('b'), write('c'), write('d')];
    gate.resolve('go');
    await Promise.all(all);

    expect(committed).toEqual(['a', 'd']);
  });

  it('still sends the intent that superseded a failed write', async () => {
    const gate = deferred<string>();
    const attempted: string[] = [];
    const write = createSerialWriter(async (name: string) => {
      attempted.push(name);
      if (name === 'a') {
        await gate.promise;
        throw new Error('offline');
      }
      return name;
    });

    const chain = write('a');
    void write('b');
    gate.resolve('go');
    await expect(chain).resolves.toBe('b');
    expect(attempted).toEqual(['a', 'b']);
  });

  it('cannot resurrect a stranded intent onto a later write', async () => {
    const gate = deferred<string>();
    const committed: string[] = [];
    let failNext = true;
    const write = createSerialWriter(async (name: string) => {
      if (name === 'a') {
        await gate.promise;
        if (failNext) throw new Error('offline');
      }
      committed.push(name);
      return name;
    });

    const chain = write('a');
    void write('b');
    gate.resolve('go');
    await chain.catch(() => undefined);
    failNext = false;

    await write('c');
    expect(committed).toEqual(['b', 'c']);
    expect(committed.at(-1)).toBe('c');
  });

  it('reports a lone failure and stays usable afterwards', async () => {
    let fail = true;
    const write = createSerialWriter((name: string) =>
      fail ? Promise.reject(new Error('offline')) : Promise.resolve(name));

    await expect(write('a')).rejects.toThrow('offline');
    fail = false;
    await expect(write('b')).resolves.toBe('b');
  });

  it('accepts new work again after the queue drains', async () => {
    const committed: string[] = [];
    const write = createSerialWriter((name: string) => {
      committed.push(name);
      return Promise.resolve(name);
    });
    await write('a');
    await write('b');
    expect(committed).toEqual(['a', 'b']);
  });
});


it('retains session archive metadata in the conversation', () => {
  const row = agentSessionDtoSchema.parse({ id: 'card', trackId: 'track', isLead: false,
    provider: 'claude', status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2, archivedAtMs: 42 });
  expect(agentSessionDtoToConversation(row).archivedAt).toBe(42);
});
