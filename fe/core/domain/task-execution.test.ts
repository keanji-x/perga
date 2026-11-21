import { expect, it } from 'vitest';
import { taskDtoSchema } from '../api/perga-v1.js';
import { currentTaskExecution } from './task-execution.js';

it('projects session identity and run count from the current task', () => {
  expect(currentTaskExecution(undefined)).toBeUndefined();
  const task = taskDtoSchema.parse({ id: 'task', trackId: 'track', blockId: 'block', taskKey: 'inspect',
    kind: 'agent', status: 'failed', sessionId: 'session', runCount: 2, statusDetail: 'validation failed',
    createdAtMs: 1, updatedAtMs: 4, startedAtMs: 2, finishedAtMs: 3 });
  expect(currentTaskExecution(task)).toEqual({ sessionId: 'session', runCount: 2, status: 'failed',
    createdAtMs: 2, finishedAtMs: 3, label: 'Failed', statusDetail: 'validation failed', workerCardId: 'session', blockingReason: null });
});

it('represents a pending task before its first run', () => {
  const task = taskDtoSchema.parse({ id: 'task', trackId: 'track', blockId: 'block', taskKey: 'inspect',
    kind: 'agent', status: 'pending', runCount: 0, createdAtMs: 1, updatedAtMs: 1 });
  expect(currentTaskExecution(task)).toMatchObject({ runCount: 0, sessionId: null, label: 'Queued' });
});
