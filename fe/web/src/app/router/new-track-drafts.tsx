import { createContext, useContext, useEffect, useSyncExternalStore, type ReactNode } from 'react';
import { FOLLOW_INSTALLATION_DEFAULT, type ModelSelection } from '../../../../core/domain/conversation.ts';
import { permissionModeAfterProviderChange } from '../../../../core/domain/planner-permission-mode.ts';
import type { AgentProvider } from '../../../../core/api/generated/wire.ts';
import type { Area } from '../../../../core/domain/area.ts';
import type { NewTrackBody } from '../../../../core/domain/track.ts';
import type { NewTrackFormState } from '../../features/area/new-track/public.tsx';
import type { PlannerRole } from '../../features/chat/thread/model-pill.tsx';
import { useState } from '../../ui/state/public.ts';
import { mintIdempotencyKey } from '../providers/idempotency-key.ts';
import { createStorageKey } from '../../../../core/keys/storage.ts';
import { useCurrentUser } from '../auth/current-user.tsx';
import { TRACK_CREATE_TEXT } from '../../../../core/domain/track.ts';

/** One create's wire body, its key, and any initial message held for post-create send. */
export type TrackCreationRequest = Readonly<{ body: NewTrackBody; key: string; message?: string }>;

type SubmittedTrackDraft = Readonly<{ request: TrackCreationRequest; createdTrackId: string | null }>;

export type NewTrackDraftArea = Pick<Area, 'id' | 'name' | 'defaultCwd' | 'executionTargetId'> & Readonly<{
  defaultTemplateId?: string | null;
}>;

export type NewTrackSession = Readonly<{
  area: NewTrackDraftArea;
  form: NewTrackFormState | null;
  /** The Planner's backend; `codex` until the reader picks a model from another provider's group. */
  provider: AgentProvider;
  permissionMode: string | null;
  /** A selection from `provider`'s catalog; set together with `provider` by one pick. */
  model: ModelSelection;
  /** `admin` creates an Area admin Track (`admin: true`); `member` sends no `admin` key. */
  role: PlannerRole;
  key: string;
  creating: boolean;
  request: TrackCreationRequest | null;
  createdTrackId: string | null;
  error: string | null;
  canRetryAsNewTrack: boolean;
  folderConflict: Readonly<{
    ownerAreaId: string; areaName: string; folderId: number; cwd: string;
  }> | null;
}>;

function createDraftStore(submittedStorage?: () => Storage, userId?: string) {
  const keyOf = (areaId: string) => createStorageKey('track-create-v1', encodeURIComponent(userId ?? ''), encodeURIComponent(areaId));
  const readSubmittedDraft = (areaId: string): SubmittedTrackDraft | null => {
    if (submittedStorage === undefined || !userId) return null;
    try {
      const value: unknown = JSON.parse(submittedStorage().getItem(keyOf(areaId)) ?? 'null');
      if (typeof value !== 'object' || value === null) return null;
      const { key, body, message, createdTrackId } = value as Partial<TrackCreationRequest> & { createdTrackId?: unknown };
      if (typeof key !== 'string' || body?.area_id !== areaId
        || (message !== undefined && typeof message !== 'string')) return null;
      return {
        request: { key, body, ...(message === undefined ? {} : { message }) },
        createdTrackId: typeof createdTrackId === 'string' && createdTrackId.trim() !== '' ? createdTrackId : null,
      };
    } catch {
      return null;
    }
  };
  const remember = (areaId: string, request: TrackCreationRequest | null, createdTrackId: string | null) => {
    if (submittedStorage === undefined || !userId) return;
    try {
      const storage = submittedStorage();
      if (request === null) storage.removeItem(keyOf(areaId));
      else storage.setItem(keyOf(areaId), JSON.stringify({ ...request, ...(createdTrackId === null ? {} : { createdTrackId }) }));
    } catch { /* Keep the in-memory draft when browser storage is unavailable. */ }
  };
  const drafts = new Map<string, NewTrackSession>();
  const listeners = new Set<() => void>();
  const notify = () => { for (const listener of listeners) listener(); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    get: (areaId: string) => drafts.get(areaId) ?? null,
    ensure: (area: Area) => {
      if (drafts.has(area.id)) return;
      const { request, createdTrackId } = readSubmittedDraft(area.id) ?? { request: null, createdTrackId: null };
      drafts.set(area.id, {
        area: {
          id: area.id,
          name: area.name,
          defaultCwd: area.defaultCwd,
          executionTargetId: area.executionTargetId,
          defaultTemplateId: area.defaultTemplateId,
        },
        form: request === null ? null : {
          message: request.message ?? '', cwd: request.body.cwd ?? '', isolation: request.body.isolation,
          workspaceOptionId: request.body.workspace_option_id,
          templateId: request.body.template_id ?? null,
          templateInputs: Object.fromEntries(Object.entries(request.body.template_input ?? {}).flatMap(([key, value]) =>
            typeof value === 'number' ? [[key, String(value)]]
              : typeof value === 'string' || typeof value === 'boolean' ? [[key, value]] : [])),
        },
        provider: request?.body.planner_provider ?? 'codex',
        permissionMode: request?.body.permissionMode ?? null,
        model: request === null ? FOLLOW_INSTALLATION_DEFAULT : {
          model: request.body.model ?? null, reasoning_effort: request.body.reasoning_effort ?? null,
        },
        role: request?.body.admin ? 'admin' : 'member',
        key: request?.key ?? mintIdempotencyKey(), creating: false, request, createdTrackId,
        error: request === null || createdTrackId !== null ? null : TRACK_CREATE_TEXT.unknown, canRetryAsNewTrack: false, folderConflict: null,
      });
      notify();
    },
    update: (areaId: string, patch: Partial<NewTrackSession>, { persistConfirmation = true } = {}) => {
      const current = drafts.get(areaId);
      if (current === undefined) return;
      const nextProvider = patch.provider ?? current.provider;
      const nextPermissionMode = patch.provider !== undefined
        ? permissionModeAfterProviderChange(current.provider, nextProvider, patch.permissionMode ?? current.permissionMode)
        : (patch.permissionMode !== undefined ? patch.permissionMode : current.permissionMode);
      const request = patch.request === undefined ? current.request : patch.request;
      const createdTrackId = request === null ? null
        : patch.createdTrackId === undefined ? (request === current.request ? current.createdTrackId : null) : patch.createdTrackId;
      drafts.set(areaId, { ...current, ...patch, permissionMode: nextPermissionMode, request, createdTrackId });
      if (patch.request !== undefined || (patch.createdTrackId !== undefined && persistConfirmation)) {
        remember(areaId, request, persistConfirmation ? createdTrackId : null);
      }
      notify();
    },
    forget: (areaId: string) => { drafts.delete(areaId); remember(areaId, null, null); notify(); },
  };
}

const DraftContext = createContext<ReturnType<typeof createDraftStore> | null>(null);

/** One tab's unfinished intentions; route navigation never clears them. */
export function NewTrackDraftProvider({ children, submittedStorage }: { children: ReactNode; submittedStorage?: () => Storage }) {
  const userId = useCurrentUser()?.userId;
  const [store] = useState(() => createDraftStore(submittedStorage, userId));
  return <DraftContext.Provider value={store}>{children}</DraftContext.Provider>;
}

function useDraftStore() {
  const store = useContext(DraftContext);
  if (store === null) throw new Error('NewTrackDraftProvider is required');
  return store;
}

/** Read a retained intention for presentation; it never proves live Area membership. */
export function useNewTrackDraftArea(areaId: string | undefined): NewTrackDraftArea | undefined {
  const store = useDraftStore();
  return useSyncExternalStore(store.subscribe, () => areaId === undefined ? undefined : store.get(areaId)?.area);
}

/** Seed the existing draft from a confirmed create response before entering its route. */
export function usePrepareNewTrack(): (area: Area) => void {
  return useDraftStore().ensure;
}

export function useNewTrackSession(areaId: string, area: Area | undefined) {
  const store = useDraftStore();
  const session = useSyncExternalStore(store.subscribe, () => store.get(areaId));
  useEffect(() => { if (area !== undefined) store.ensure(area); }, [area, store]);
  return { store, session };
}
