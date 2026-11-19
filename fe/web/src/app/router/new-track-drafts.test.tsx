import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { expect, it } from 'vitest';
import type { Area } from '../../../../core/domain/area.ts';
import { CurrentUserScope } from '../auth/current-user.tsx';
import { NewTrackDraftProvider, useNewTrackSession } from './new-track-drafts.tsx';
import { createMemoryStorage } from './share-reuse-fixture.ts';

it.each([undefined, 'Already submitted'])('restores the creation request after reload with first message %s', (firstMessage) => {
  const identity = { userId: 'create-replay-owner', displayName: 'Owner', role: 'owner', sessionId: 'one' } as const;
  const area: Area = { id: 'create-replay-area', name: 'Area', color: '#112233', defaultCwd: null,
    defaultTemplateId: null, kind: 'user', sort: 0, createdAt: 0, updatedAt: 0 };
  const storage = createMemoryStorage();
  const wrapper = ({ children }: { children: ReactNode }) => <CurrentUserScope value={identity}>
    <NewTrackDraftProvider submittedStorage={() => storage}>{children}</NewTrackDraftProvider>
  </CurrentUserScope>;
  const original = renderHook(() => useNewTrackSession(area.id, area), { wrapper });
  const key = original.result.current.session?.key;
  expect(key).toBeTruthy();
  if (key === undefined) throw new Error('Draft did not initialize');
  const request = {
    key,
    ...(firstMessage === undefined ? {} : { message: firstMessage }),
    body: {
      area_id: area.id, planner_provider: 'claude', cwd: '/repo', permissionMode: 'plan',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
    } as const,
  };
  act(() => { original.result.current.store.update(area.id, { request, creating: true }); });
  original.unmount();

  const reopened = renderHook(() => useNewTrackSession(area.id, area), { wrapper });
  expect(reopened.result.current.session).toMatchObject({
    key, request, creating: false, permissionMode: 'plan',
    form: { message: firstMessage ?? '', cwd: request.body.cwd },
  });
  act(() => { reopened.result.current.store.forget(area.id); });
  reopened.unmount();
  const next = renderHook(() => useNewTrackSession(area.id, area), { wrapper });
  expect(next.result.current.session?.request).toBeNull();
  expect(next.result.current.session?.key).not.toBe(key);
});

it('keeps a mode for same-provider model changes and clears it on provider change', () => {
  const area: Area = { id: 'modes', name: 'Modes', color: '#112233', defaultCwd: null,
    defaultTemplateId: null, kind: 'user', sort: 0, createdAt: 0, updatedAt: 0 };
  const wrapper = ({ children }: { children: ReactNode }) => <NewTrackDraftProvider>{children}</NewTrackDraftProvider>;
  const draft = renderHook(() => useNewTrackSession(area.id, area), { wrapper });
  act(() => { draft.result.current.store.update(area.id, { permissionMode: 'provider-choice' }); });
  act(() => { draft.result.current.store.update(area.id, { provider: 'codex', model: { model: 'other', reasoning_effort: null } }); });
  expect(draft.result.current.session?.permissionMode).toBe('provider-choice');
  act(() => { draft.result.current.store.update(area.id, { provider: 'opencode' }); });
  expect(draft.result.current.session?.permissionMode).toBeNull();
});
