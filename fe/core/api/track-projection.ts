import type { TrackDto } from './perga-v1.js';

/** Shared HTTP/event projection into the existing Track presentation shape. */
export function trackDtoToWire(dto: TrackDto) {
  const spec = typeof dto.workspaceSpec === 'object' && dto.workspaceSpec !== null
    ? (dto.workspaceSpec as Record<string, unknown>)
    : {};
  const worktreePath = typeof spec.mergedCwd === 'string' && spec.mergedCwd !== ''
    ? spec.mergedCwd
    : typeof spec.worktreePath === 'string' && spec.worktreePath !== ''
      ? spec.worktreePath
      : typeof spec.workspacePath === 'string' && spec.workspacePath !== ''
        ? spec.workspacePath
        : undefined;
  const logicalRoot = typeof spec.logicalRoot === 'string' && spec.logicalRoot !== ''
    ? spec.logicalRoot
    : dto.cwd;
  const isWorktree = dto.workspaceDriver === 'vcs-workspace'
    || dto.workspaceDriver === 'vcs'
    || dto.workspaceDriver === 'git-worktree'
    || dto.workspaceDriver === 'jj'
    || dto.workspaceDriver === 'worktree';
  const isAttachedNone = (dto.workspaceDriver === 'direct' || dto.workspaceDriver === 'none') && dto.cwd !== '';
  const workspace = isWorktree
    ? {
      isolation: 'worktree' as const,
      kind: 'attached' as const,
      path: logicalRoot,
      frozen_at: null,
      ...(worktreePath !== undefined ? { worktree: worktreePath } : {}),
    }
    : isAttachedNone
      ? { isolation: 'none' as const, kind: 'attached' as const, path: dto.cwd, frozen_at: null }
      : { isolation: 'managed' as const, kind: 'managed' as const, path: dto.cwd, frozen_at: null };
  return {
    id: dto.id,
    area_id: dto.areaId,
    title: dto.title,
    sort: dto.sortOrder,
    cwd: dto.cwd,
    workspace,
    pinned_at: dto.pinnedAtMs ?? null,
    closed_at: dto.closedAtMs ?? null,
    admin_granted_at: null,
    created_at: dto.createdAtMs,
    updated_at: dto.updatedAtMs,
    templateId: dto.templateId ?? null,
    templateTitle: dto.templateTitle ?? null,
    templateInputs: dto.templateInputs ?? {},
    templateHeaderView: dto.templateHeaderView ?? null,
    template_id: dto.templateId ?? null,
    plugin_scope: null,
    purpose: null,
    template_input: dto.templateInputs ?? null,
    recipe_id: null,
    recipe_revision: null,
    creator_track_id: dto.parentTrackId ?? null,
    creator_key: dto.parentAttemptId ?? null,
  };
}
