import { expect, it } from 'vitest';
import { taskDtoSchema } from '../api/perga-v1.js';
import { trackTaskInventory } from './track-task-inventory.js';

it.each(['acp:claude', 'acp:grok', 'runtime/future-provider', null])('retains native task provider %s without a closed provider list', provider => {
  const task = taskDtoSchema.parse({ id: 'task', trackId: 'track', blockId: 'block', taskKey: 'review',
    kind: 'agent', provider, ready: true, status: 'running', createdAtMs: 1, updatedAtMs: 2 });
  const [row] = trackTaskInventory([{ id: 'block', kind: 'unsupported', declaredKind: 'task' }], [task]);
  expect(row.kind).toBe(provider ?? 'agent');
  expect(row.key).toBe('review');
  expect(row.status).toBe('running');
});
