import { expect, it } from 'vitest';
import { workerToolActivity } from './worker-session.js';

const event = (sequence: number, payload: unknown) => ({
  sequence, kind: 'session.tool_progress', payload, createdAtMs: sequence,
});

it('ignores tool progress without a call identity', () => {
  expect(workerToolActivity([
    event(1, { preview: 'Old truncated payload', truncated: true }),
    event(2, { callId: '', name: 'read_file', phase: 'started' }),
  ]).size).toBe(0);
});

it('keeps the identity and failure of a truncated completion without its start', () => {
  const tools = workerToolActivity([
    event(2, { callId: 'read', phase: 'completed', name: 'read_file', output: 'Access denied…', isError: true, truncated: true }),
  ]);
  expect([...tools.values()]).toEqual([
    { callId: 'read', phase: 'completed', name: 'read_file', output: 'Access denied…', isError: true, args: null, atMs: 2 },
  ]);
});

it('uses the explicit phase and output even if the event has a legacy preview field', () => {
  const tools = workerToolActivity([
    event(1, { callId: 'read', phase: 'started', args: '{"path":"README…', truncated: true }),
    event(2, { callId: 'read', phase: 'completed', output: 'Access denied', preview: 'Unused preview', isError: true }),
  ]);
  expect(tools.get('read')).toMatchObject({ phase: 'completed', args: '{"path":"README…', output: 'Access denied', isError: true });
});
