import { expect, it } from 'vitest';
import { taskDtoSchema } from '../api/perga-v1.js';
import { taskEvidence, taskStatusLabel, cancelTaskOperation, rejectTaskOperation } from './task-recovery.js';

it('reads result and gate evidence directly from the latest task run', () => {
  const task = taskDtoSchema.parse({ id: 'task', trackId: 'track', blockId: 'block', taskKey: 'inspect',
    kind: 'agent', status: 'failed', runCount: 2, createdAtMs: 1, updatedAtMs: 2,
    workerResult: { summary: 'partial progress' }, gateVerdict: { passed: false, steps: [
      { name: 'check', exitCode: 1, durationMs: 4, stdout: 'out', stderr: 'err', outputTail: '' },
    ] } });
  expect(taskEvidence(task)).toMatchObject({ gatePassed: false, gateSteps: [{ name: 'check', output: 'out\nerr' }] });
  expect(taskEvidence(task).workerResult).toContain('partial progress');
  expect(taskEvidence({ ...task, workerResult: null, gateVerdict: null })).toEqual({ workerResult: null, gatePassed: null, gateSteps: [] });
});

it('labels task lifecycle and encodes task action routes', () => {
  expect(taskStatusLabel('pending')).toBe('Queued');
  expect(taskStatusLabel('running')).toBe('Running');
  expect(taskStatusLabel('verifying')).toBe('Checking result');
  expect(cancelTaskOperation('track/1', 'b + c').path).toBe('/api/v1/tracks/track%2F1/tasks/b%20%2B%20c/cancel');
  expect(rejectTaskOperation('track', 'b', 'retry').body).toEqual({ note: 'retry' });
});
