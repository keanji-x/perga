import { describe, expect, it } from 'vitest';
import { decodeWireEvent } from './schemas.js';
import { agentRuntimeStatusSchema } from './perga-v1.js';

describe('runtime activity contracts', () => {
  it('preserves idle activity and structured turn outcomes', () => {
    const status = { kind: 'idle', idleActivity: { since: 10, lastEventAt: 20 },
      lastTurnOutcome: { kind: 'completed', structuredOutput: { result: true } } };
    expect(agentRuntimeStatusSchema.parse(status)).toEqual(status);
    const decoded = decodeWireEvent({ kind: 'session.runtime_status', payload: { sessionId: 'session', status } });
    expect(decoded).toEqual({ status: 'ready', value: { ev: 'session.runtime_status', data: { sessionId: 'session', status } } });
  });
  it('decodes out-of-turn task progress without requiring a turn id', () => {
    const task = { taskId: 'background', taskType: 'shell', background: true, status: 'running' };
    expect(decodeWireEvent({ kind: 'session.task_progress', payload: { sessionId: 'session', task } }))
      .toEqual({ status: 'ready', value: { ev: 'session.task_progress', data: { sessionId: 'session', task } } });
    expect(decodeWireEvent({ kind: 'session.task_progress', payload: { sessionId: 'session', task: {} } }).status).toBe('failed');
  });
});
