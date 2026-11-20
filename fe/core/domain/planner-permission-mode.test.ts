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
      method: 'PUT', path: '/api/v1/sessions/ses%2F1/permission-mode', body: { permissionMode: 'ask' },
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
      unknownInput: null,
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
        id: 'ses_3', trackId: 'trk_1', isLead: false, boundTaskId: 'tsk_1', provider: 'codex',
        epoch: 1, status: 'idle', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
        permissionMode: 'full',
      },
    });
    expect(taskBound.session.permissionMode).toBe('full');
  });

  it('rejects the retired planner run response', () => {
    expect(conversationRunOperation('card-1').responseSchema.safeParse({ ...RUN, permission_mode: 'ask' }).success).toBe(false);
  });

  it('preselects the provider default in new drafts', () => {
    const agyModes = [
      { id: 'plan', label: 'Plan', description: 'Read first', tone: 'neutral' as const, isDefault: true },
      { id: 'bypassPermissions', label: 'Full access', description: 'Skip prompts', tone: 'danger' as const, isDefault: false },
    ];
    const draft = { source: 'draft', providerModes: agyModes, metadataLoaded: true, isDisabled: false } as const;
    expect(resolvePermissionControl({ ...draft, storedMode: null }))
      .toEqual({ visible: true, mode: 'plan', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ ...draft, storedMode: 'bypassPermissions' }))
      .toEqual({ visible: true, mode: 'bypassPermissions', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ ...draft, storedMode: 'stale' }))
      .toEqual({ visible: true, mode: 'plan', options: agyModes, isDisabled: false });
    expect(resolvePermissionControl({ ...draft, storedMode: 'bypassPermissions', providerModes: [], metadataLoaded: false }))
      .toEqual({ visible: true, mode: 'bypassPermissions', options: [], isDisabled: true });
    expect(resolvePermissionControl({ ...draft, storedMode: 'bypassPermissions', providerModes: [] }))
      .toEqual({ visible: false });
    expect(resolvePermissionControl({ ...draft, storedMode: null, providerModes: [], metadataLoaded: false }))
      .toEqual({ visible: false });
  });

  it('keeps an existing session selection when the catalogue only contains the installation default', () => {
    const launchModes = [
      { id: 'bypassPermissions', label: 'Full access', description: '', tone: 'danger' as const, isDefault: true },
    ];
    const session = { source: 'session', providerModes: launchModes, metadataLoaded: true, isDisabled: false } as const;
    expect(resolvePermissionControl({ ...session, storedMode: 'workspace-review' }))
      .toEqual({ visible: true, mode: 'workspace-review', options: launchModes, isDisabled: false });
    expect(resolvePermissionControl({ ...session, storedMode: null }))
      .toEqual({ visible: true, mode: null, options: launchModes, isDisabled: false });
    expect(resolvePermissionControl({ ...session, storedMode: 'workspace-review', providerModes: [] }))
      .toEqual({ visible: true, mode: 'workspace-review', options: [], isDisabled: true });
  });

  it('clears permission mode when switching providers and preserves it otherwise', () => {
    expect(permissionModeAfterProviderChange('codex', 'codex', 'ask')).toBe('ask');
    expect(permissionModeAfterProviderChange('codex', 'antigravity', 'ask')).toBeNull();
    expect(permissionModeAfterProviderChange('antigravity', 'claude', 'bypassPermissions')).toBeNull();
  });
});
