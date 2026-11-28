import { describe,it,expect } from 'vitest';
import { buildConversationTranscript, conversationItemsOperation, conversationRunOperation } from './conversation-display.js';
describe('system wake projection', () => {
  it.each([
    ['system', undefined, 'system'],
    ['system', 'system_mail', 'system_mail'],
    ['system', 'user', 'system'],
    ['system', 'invalid', 'system'],
    ['wake', 'system_report_edited', 'system_report_edited'],
    ['scheduler', undefined, 'system'],
  ])('renders %s with presentation %s as a System observation', (triggerKind, presentation, expected) => {
    const page = conversationItemsOperation('session', null).responseSchema.parse({
      session: { id: 'session', trackId: 'track', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 1 },
      state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false, items: [{ id: 1, sessionId: 'session', turnId: 'turn', kind: 'user',
        payload: { content: 'Wake notice', triggerKind, presentation }, createdAtMs: 2 }],
    });
    expect(page.items[0].source).toEqual({ kind: 'system', observation_id: '1', presentation: expected,
      text: 'Wake notice', created_at_ms: 2 });
  });
});

it('rejects old run objects and raw arrays at the canonical run boundary', () => {
  const operation = conversationRunOperation('a/b');
  expect(operation.path).toBe('/api/v1/sessions/a%2Fb/items?afterId=0&limit=1');
  for (const value of [[], { card_id: 'card', phase: 'idle' }]) expect(operation.responseSchema.safeParse(value).success).toBe(false);
});


it('preserves a nullable epoch boundary as a visible memory divider', () => {
  const page = conversationItemsOperation('session', null).responseSchema.parse({
    session: { id: 'session', trackId: 'track', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 2 },
    state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false,
    items: [{ id: 2, sessionId: 'session', turnId: null, kind: 'epoch_boundary',
      payload: { epoch: 2, reason: 'reset_context' }, createdAtMs: 2 }],
  });
  expect(buildConversationTranscript(page.items, null)).toMatchObject([
    { id: 'system:epoch-2', author: 'system', text: 'Context reset · Epoch 2' },
  ]);
});

it('retains archived interrupted inputs in order before the reset boundary', () => {
  const content = 'Original\n  exact text <@person>'; const wake = '{"event":"mail","text":"Wake\\nbody"}';
  const page = conversationItemsOperation('session', null).responseSchema.parse({
    session: { id: 'session', trackId: 'track', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 2 },
    state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false,
    items: [
      { id: 1, sessionId: 'session', turnId: null, kind: 'user', payload: { v: 1, content, inputId: 'input', epoch: 1, status: 'interrupted', archived: true, reason: 'session_reset' }, createdAtMs: 1 },
      { id: 2, sessionId: 'session', turnId: null, kind: 'system', payload: { v: 1, content: wake, wakeId: 'wake', wakeKind: 'mail', epoch: 1, status: 'interrupted', archived: true, reason: 'session_reset' }, createdAtMs: 2 },
      { id: 3, sessionId: 'session', turnId: null, kind: 'epoch_boundary', payload: { epoch: 2, reason: 'reset_context' }, createdAtMs: 3 },
    ],
  });
  expect(buildConversationTranscript(page.items, null)).toMatchObject([
    { author: 'human', text: content, deliveryError: 'Interrupted · Unsent' },
    { author: 'system', text: wake, label: 'Interrupted · Unsent' },
    { id: 'system:epoch-3', text: 'Context reset · Epoch 2' },
  ]);
});

it('keeps an exhausted wake visible with its original payload and failure reason', () => {
  const content = '{"event":"task_settled"}';
  const page = conversationItemsOperation('session', null).responseSchema.parse({
    session: { id: 'session', trackId: 'track', provider: 'codex', contextUsage: null, createdAtMs: 1, updatedAtMs: 2 },
    state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false,
    items: [{ id: 1, sessionId: 'session', turnId: null, kind: 'system', payload: { v: 1, content, wakeId: 'wake', wakeKind: 'task_settled', status: 'interrupted', archived: true, reason: 'max_attempts_exceeded' }, createdAtMs: 1 }],
  });
  expect(buildConversationTranscript(page.items, null)).toMatchObject([
    { author: 'system', text: content, label: 'Wake delivery failed · Retry limit reached' },
  ]);
});

const pergaPage = (items: unknown[], extra = {}) => ({
  session: { id: 'session', trackId: 'track', provider: 'codex', nativeThreadId: 'native-thread', epoch: 2,
    contextUsage: null, createdAtMs: 1, updatedAtMs: 10 },
  state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false, items, ...extra,
});

it.each(['loaded', 'refreshed', 'cleared'])('projects %s memory context as a quiet system observation', (mode) => {
  const content = mode === 'cleared' ? 'Memory is now empty (previous entries were removed).' : `Memory context ${mode} (2 entries).`;
  const page = conversationItemsOperation('session', null).responseSchema.parse(pergaPage([
    { id: 1, sessionId: 'session', turnId: 'turn', kind: 'system', createdAtMs: 2,
      payload: { v: 1, kind: 'memory_context', mode, entryCount: mode === 'cleared' ? 0 : 2, content } },
  ]));
  expect(buildConversationTranscript(page.items, null)).toMatchObject([
    { id: 'system:1', author: 'system', label: `Memory context ${mode}`, text: content, quiet: true },
  ]);
});
const item = (id: number, kind: string, payload: unknown, turnId: string | null = 'turn') =>
  ({ id, sessionId: 'session', turnId, kind, payload, createdAtMs: id });

it('attributes a historical local-owner message to the verified owner', () => {
  const page = conversationItemsOperation('session', null).responseSchema.parse(pergaPage([
    item(1, 'user', { inputId: 'before-auth', content: 'Historical message' }),
  ]));
  expect(buildConversationTranscript(page.items, 'new-owner-id', true)).toMatchObject([
    { author: 'you', human: { userId: 'local-owner' } },
  ]);
  expect(buildConversationTranscript(page.items, 'member-id', false)).toMatchObject([{ author: 'human' }]);
});

it('projects SessionItemDto speech, thinking, tools and outcomes directly without provider envelopes', () => {
  const page = conversationItemsOperation('session', null).responseSchema.parse(pergaPage([
    item(1, 'user', { inputId: 'message', content: '  exact\nwords  ', steered: true }),
    item(2, 'thinking', { text: 'Reasoning\nverbatim' }),
    item(3, 'tool_call', { callId: 'call', name: 'custom_tool', args: { query: 'input' } }),
    item(4, 'tool_result', { callId: 'call', output: 'Tool result', isError: false }),
    item(5, 'assistant_text', { text: '  Exact\nreply  ' }),
    item(6, 'turn_outcome', { status: 'completed' }),
  ]));
  const entries = buildConversationTranscript(page.items, 'local-owner');
  expect(entries).toMatchObject([
    { author: 'you', text: '  exact\nwords  ', supplemental: true, human: { messageId: 'message' } },
    { author: 'activity', verb: 'Thought', content: 'Reasoning\nverbatim' },
    { author: 'activity', verb: 'Called', tool: 'custom_tool', state: 'done', atMs: 4,
      content: 'Input\n{\n  "query": "input"\n}\n\nOutput\nTool result' },
    { author: 'agent', text: '  Exact\nreply  ', nativeTurn: { threadId: 'native-thread', turnId: 'turn' } },
    { author: 'turn', status: 'completed', turnId: 'turn' },
  ]);
  for (const row of page.items) {
    expect(row.source).not.toHaveProperty('item.method');
    expect(row.source).not.toHaveProperty('item.params');
    expect(row.source.kind).not.toBe('native');
  }
});

it('keeps a running tool identity stable when its result arrives and preserves failed output', () => {
  const call = item(1, 'tool_call', { callId: 'call', name: 'custom', args: {} });
  const read = (items: unknown[]) => buildConversationTranscript(conversationItemsOperation('session', null).responseSchema.parse(pergaPage(items)).items, null);
  const running = read([call])[0];
  const failed = read([call, item(2, 'tool_result', { callId: 'call', output: 'Failure\nverbatim', isError: true })])[0];
  expect(running).toMatchObject({ author: 'activity', state: 'running' });
  expect(failed).toMatchObject({ id: running.id, author: 'activity', state: 'failed', content: 'Error\nFailure\nverbatim' });
});

it('projects active-turn failures only when their persisted outcome is missing', () => {
  const activeTurn = { id: 'turn', sessionId: 'session', turnIndex: 1, epoch: 2, triggerKind: 'user',
    status: 'interrupted', errorText: 'Stopped', startedAtMs: 1, completedAtMs: 9 };
  for (const items of [[], [item(1, 'turn_outcome', { status: 'interrupted', error: 'Stopped' })]]) {
    const page = conversationItemsOperation('session', null).responseSchema.parse(pergaPage(items, { activeTurn }));
    expect(buildConversationTranscript(page.items, null)).toMatchObject([{ author: 'turn', status: 'interrupted', text: 'Stopped' }]);
    expect(page.items).toHaveLength(1);
  }
});

it('keeps cursor ordering and page boundaries without fabricating persisted item identities', () => {
  const data = pergaPage(Array.from({ length: 65 }, (_, index) => item(index + 1, 'assistant_text', { text: String(index + 1) })));
  const latest = conversationItemsOperation('session', null).responseSchema.parse(data);
  expect(latest.items).toHaveLength(60);
  expect(latest.next_cursor?.position).toBe(6);
  const earlier = conversationItemsOperation('session', latest.next_cursor).responseSchema.parse(data);
  expect(buildConversationTranscript(earlier.items, null).map(entry => 'text' in entry ? entry.text : '')).toEqual(['1', '2', '3', '4', '5']);
  expect(earlier.next_cursor).toBeNull();
});

it.each(['user_ask', 'perga_user_ask', 'perga:user_ask', 'perga:perga_user_ask'])(
  'renders successful %s questions as speech and incomplete or refused calls as activities', (name) => {
    const call = item(1, 'tool_call', { callId: 'ask', name, args: { questions: [{ title: ' Choose ', options: [' A ', 'B'] }] } });
    const read = (items: unknown[]) => buildConversationTranscript(conversationItemsOperation('session', null).responseSchema.parse(pergaPage(items)).items, null);
    expect(read([call])).toMatchObject([{ author: 'activity', state: 'running' }]);
    expect(read([call, item(2, 'tool_result', { callId: 'ask', output: {}, isError: false })]))
      .toMatchObject([{ author: 'agent', origin: 'ask', text: 'Choose (A / B)' }]);
    expect(read([call, item(2, 'tool_result', { callId: 'ask', output: 'Refused', isError: true })]))
      .toMatchObject([{ author: 'activity', state: 'failed', detail: 'Refused' }]);
  },
);

it.each(['publish_pr', 'vcs_publish_pr', 'vcs:publish_pr', 'vcs:vcs_publish_pr'])(
  'renders %s as a PR publication', (name) => {
    const page = conversationItemsOperation('session', null).responseSchema.parse(
      pergaPage([item(1, 'tool_call', { callId: 'publish', name })]),
    );
    expect(buildConversationTranscript(page.items, null)).toMatchObject([
      { author: 'activity', verb: 'Publishing the PR', tool: name },
    ]);
  },
);

it.each(['task_list', 'task_ls', 'perga_task_ls', 'perga:perga_task_ls'])('renders %s as reading the plan', (name) => {
  const page = conversationItemsOperation('session', null).responseSchema.parse(pergaPage([
    item(1, 'tool_call', { callId: 'tasks', name }),
    item(2, 'tool_result', { callId: 'tasks', output: [], isError: false }),
  ]));
  expect(buildConversationTranscript(page.items, null)).toMatchObject([
    { author: 'activity', verb: 'Read plan', state: 'done', tool: name },
  ]);
});

it.each([
  ['area_list', {}, 'Listing areas', 'Listed areas', null],
  ['perga_track_list', { area: 'Research', query: 'paper' }, 'Listing tracks', 'Listed tracks', 'Research'],
  ['track_list', { query: 'report digest' }, 'Listing tracks', 'Listed tracks', 'report digest'],
  ['track_list', { area: '*' }, 'Listing tracks', 'Listed tracks', 'all areas'],
  ['track_list', {}, 'Listing tracks', 'Listed tracks', null],
  ['perga_report_read', { trackId: 'trk_peer' }, 'Reading report', 'Read report', 'trk_peer'],
  ['report_read', {}, 'Reading report', 'Read report', null],
  ['report_read', { trackId: '  ' }, 'Reading report', 'Read report', null],
] as const)('renders running and completed %s activities', (name, args, running, done, target) => {
  const call = item(1, 'tool_call', { callId: 'discovery', name, args });
  const result = item(2, 'tool_result', { callId: 'discovery', output: {}, isError: false });
  const project = (items: unknown[]) => buildConversationTranscript(
    conversationItemsOperation('session', null).responseSchema.parse(pergaPage(items)).items, null);

  expect(project([call])).toMatchObject([{ author: 'activity', verb: running, state: 'running', target }]);
  expect(project([call, result])).toMatchObject([{ author: 'activity', verb: done, state: 'done', target }]);
});

it('bounds thinking and tool disclosures without losing failure evidence', () => {
  const rows = [item(1, 'thinking', { text: 'x'.repeat(40_000) }),
    item(2, 'tool_call', { callId: 'call', name: 'custom' }), item(3, 'tool_result', { callId: 'call', isError: true, output: 'y'.repeat(40_000) })];
  const entries = buildConversationTranscript(conversationItemsOperation('session', null).responseSchema.parse(pergaPage(rows)).items, null);
  for (const entry of entries) {
    if (entry.author !== 'activity') throw new Error('Expected activity');
    expect(entry.content?.length).toBeLessThan(33_000);
    expect(entry.content).toContain('(truncated)');
  }
});

it('preserves persisted turn metadata and user turn ids without adding nativeTurn boundaries to user entries', () => {
  const turn1 = { id: 'trn_1', sessionId: 'session', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: 1000, completedAtMs: 5000 };
  const turn2 = { id: 'trn_2', sessionId: 'session', turnIndex: 2, epoch: 1, triggerKind: 'user', status: 'failed', startedAtMs: 6000, completedAtMs: 9000, errorText: 'Boom' };
  const page = conversationItemsOperation('session', null).responseSchema.parse({
    ...pergaPage([
      { id: 1, sessionId: 'session', turnId: 'trn_1', kind: 'user', payload: { content: 'First' }, createdAtMs: 1000 },
      { id: 2, sessionId: 'session', turnId: 'trn_1', kind: 'assistant_text', payload: { text: 'Reply 1' }, createdAtMs: 5000 },
      { id: 3, sessionId: 'session', turnId: 'trn_2', kind: 'user', payload: { content: 'Second' }, createdAtMs: 6000 },
    ], { lastTurn: turn2 }),
    turns: [turn1, turn2],
  });
  expect(page.turns).toEqual([turn1, turn2]);
  const entries = buildConversationTranscript(page.items, null);
  expect(entries[0]).toMatchObject({ author: 'human', text: 'First', sessionTurnId: 'trn_1' });
  expect(entries[0]?.nativeTurn).toBeUndefined();
  expect(entries[1]).toMatchObject({ author: 'agent', text: 'Reply 1', nativeTurn: { turnId: 'trn_1' } });
  expect(entries[2]).toMatchObject({ author: 'human', text: 'Second', sessionTurnId: 'trn_2' });
});
