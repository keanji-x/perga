import { describe, expect, it } from 'vitest';

import { plannerRunOperation } from './conversation.js';
import { conversationRunOperation } from './conversation-display.js';
import { setPlannerPermissionModeOperation } from './planner-permission-mode.js';

const RUN = {
  card_id: 'card-1', worker_session_id: null, phase: null, model: null, reasoning_effort: null,
  blocked_reason: null, manual_compact_supported: true, recovery: null, compact_outcome: null, context_usage_invalidated: false, running_turn: null,
};

describe('session permission mode', () => {
  it('writes the mode to the unified /api/v1/sessions/{sessionId}/permission-mode endpoint', () => {
    const op = setPlannerPermissionModeOperation('ses/1', 'ask');
    expect(op).toMatchObject({
      method: 'PUT', path: '/api/v1/sessions/ses%2F1/permission-mode', body: { permission_mode: 'ask' },
    });
    expect(op.responseSchema.parse({
      id: 'ses/1', trackId: 'trk_1', isLead: false, provider: 'claude',
      epoch: 1, status: 'idle', contextUsageRatio: 0, createdAtMs: 1, updatedAtMs: 2,
      permissionMode: 'acceptEdits',
    })).toEqual({ card_id: 'ses/1', permission_mode: 'acceptEdits' });
  });

  it('reads the run\'s mode as required: a mode, `null` when unset or task-bound, never absent', () => {
    const { responseSchema } = plannerRunOperation('card-1');
    expect(responseSchema.parse({ ...RUN, permission_mode: 'ask' }).permission_mode).toBe('ask');
    expect(responseSchema.parse({ ...RUN, permission_mode: 'full' }).permission_mode).toBe('full');
    expect(responseSchema.parse({ ...RUN, permission_mode: null }).permission_mode).toBeNull();
    expect(responseSchema.safeParse(RUN).success).toBe(false);
    expect(responseSchema.safeParse({ ...RUN, permission_mode: '' }).success).toBe(false);
  });

  it('production conversationRunOperation preserves Lead/Assistant permissionMode and nulls task-bound sessions', () => {
    const { responseSchema } = conversationRunOperation('ses_1');
    const basePage = {
      items: [],
      activeTurn: null,
      unknownInput: null,
    };
    const lead = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_1', trackId: 'trk_1', isLead: true, provider: 'claude',
        epoch: 1, status: 'idle', contextUsageRatio: 0, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'bypassPermissions',
      },
    });
    expect(lead.permission_mode).toBe('bypassPermissions');

    const assistant = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_2', trackId: 'trk_1', isLead: false, provider: 'codex',
        epoch: 1, status: 'idle', contextUsageRatio: 0, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'ask',
      },
    });
    expect(assistant.permission_mode).toBe('ask');

    const taskBound = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_3', trackId: 'trk_1', isLead: false, boundTaskId: 'tsk_1', provider: 'codex',
        epoch: 1, status: 'idle', contextUsageRatio: 0, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'full',
      },
    });
    expect(taskBound.permission_mode).toBeNull();
  });
});
