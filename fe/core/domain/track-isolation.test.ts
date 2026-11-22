import { describe, expect, it } from 'vitest';
import { trackDtoSchema } from '../api/perga-v1.js';
import { trackUpdatedSchema } from '../api/schemas.js';
import { toTrack } from './track.js';

const row = Object.freeze({ id: 't', areaId: 'a', title: '', sortOrder: 0, cwd: '/repo', createdAtMs: 1, updatedAtMs: 1 });

describe('Host workspace projection', () => {
  it.each([['managed', 'managed'], ['vcs-workspace', 'worktree'], ['direct', 'none']])('projects %s consistently for HTTP and events', (workspaceDriver, isolation) => {
    const wire = { ...row, workspaceDriver, workspaceSpec: { worktreePath: '/worktree' } };
    expect(toTrack(trackDtoSchema.parse(wire)).isolation).toBe(isolation);
    expect(toTrack(trackUpdatedSchema.parse({ ev: 'track.updated', data: wire }).data).isolation).toBe(isolation);
  });
  it('rejects the old snake_case carrier', () => {
    expect(trackDtoSchema.safeParse({ id: 't', area_id: 'a', created_at: 1, updated_at: 1 }).success).toBe(false);
  });
});
