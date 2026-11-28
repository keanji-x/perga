import { describe, expect, it } from 'vitest';

import { conversationRunOperation } from './conversation-display.js';
import {
  permissionModeAfterProviderChange,
  resolvePermissionControl,
  setPlannerPermissionModeOperation,
} from './planner-permission-mode.js';

const RUN = {
  card_id: 'card-1', worker_session_id: null, phase: null, model: null, reasoning_effort: null,
  blocked_reason: null, manual_compact_supported: true, running_turn: null,
};

describe('session permission mode', () => {
  it('writes the mode to the unified /api/v1/sessions/{sessionId}/permission-mode endpoint', () => {
    const op = setPlannerPermissionModeOperation('ses/1', 'ask');
    expect(op).toMatchObject({
      method: 'PATCH', path: '/api/v1/sessions/ses%2F1', body: { permissionMode: 'ask' },
    });
    expect(op.responseSchema.parse({
      id: 'ses/1', trackId: 'trk_1', isLead: false, provider: 'claude',
      epoch: 1, status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
      permissionMode: 'acceptEdits',
    })).toMatchObject({ id: 'ses/1', permissionMode: 'acceptEdits' });
  });

  it('rejects the retired card response', () => {
    expect(setPlannerPermissionModeOperation('ses/1', 'ask').responseSchema.safeParse({
      card_id: 'ses/1', permission_mode: 'ask',
    }).success).toBe(false);
  });

  it('production conversationRunOperation preserves Lead, Assistant, and task-bound session permissionMode', () => {
    const { responseSchema } = conversationRunOperation('ses_1');
    const basePage = {
      items: [],
      activeTurn: null,
      state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null, mcpHealth: null, attachmentsSupported: false,
    };
    const lead = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_1', trackId: 'trk_1', isLead: true, provider: 'claude',
        epoch: 1, status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'bypassPermissions',
      },
    });
    expect(lead.session.permissionMode).toBe('bypassPermissions');

    const assistant = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_2', trackId: 'trk_1', isLead: false, provider: 'codex',
        epoch: 1, status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'ask',
      },
    });
    expect(assistant.session.permissionMode).toBe('ask');

    const taskBound = responseSchema.parse({
      ...basePage,
      session: {
        id: 'ses_3', trackId: 'trk_1', isLead: false, provider: 'codex',
        epoch: 1, status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'full',
      },
    });
    expect(taskBound.session.permissionMode).toBe('full');
  });

  it('rejects the retired planner run response', () => {
    expect(conversationRunOperation('card-1').responseSchema.safeParse({ ...RUN, permission_mode: 'ask' }).success).toBe(false);
  });

  it('resolves permission controls uniformly from backend metadata without inventing fallback modes', () => {
    const agyModes = [
      { id: 'plan', label: 'Plan', description: 'Read first', tone: 'neutral' as const, isDefault: true },
      { id: 'bypassPermissions', label: 'Full access', description: 'Skip prompts', tone: 'danger' as const, isDefault: false },
    ];
    expect(resolvePermissionControl({ storedMode: null, providerModes: agyModes, metadataLoaded: true, isDisabled: false }))
      .toEqual({ visible: true, mode: 'plan', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ storedMode: 'bypassPermissions', providerModes: agyModes, metadataLoaded: true, isDisabled: false }))
      .toEqual({ visible: true, mode: 'bypassPermissions', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ storedMode: 'stale', providerModes: agyModes, metadataLoaded: true, isDisabled: false }))
      .toEqual({ visible: true, mode: 'plan', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ storedMode: 'bypassPermissions', providerModes: [], metadataLoaded: false, isDisabled: false }))
      .toEqual({
        visible: true,
        mode: 'bypassPermissions',
        options: [{ id: 'bypassPermissions', label: 'bypassPermissions', description: 'Provider approval options unavailable.', tone: 'neutral', isDefault: true }],
        isDisabled: true,
      });
    expect(resolvePermissionControl({ storedMode: 'bypassPermissions', providerModes: [], metadataLoaded: true, isDisabled: false }))
      .toEqual({ visible: false });
    expect(resolvePermissionControl({ storedMode: null, providerModes: [], metadataLoaded: false, isDisabled: false }))
      .toEqual({ visible: false });
  });

  it('clears permission mode when switching providers and preserves it otherwise', () => {
    expect(permissionModeAfterProviderChange('codex', 'codex', 'ask')).toBe('ask');
    expect(permissionModeAfterProviderChange('codex', 'antigravity', 'ask')).toBeNull();
    expect(permissionModeAfterProviderChange('antigravity', 'claude', 'bypassPermissions')).toBeNull();
  });
});
