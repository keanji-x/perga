import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { expect, it } from 'vitest';
import type { Area } from '../../../../core/domain/area.ts';
import { TRACK_CREATE_TEXT } from '../../../../core/domain/track.ts';
import { createStorageKey } from '../../../../core/keys/storage.ts';
import { CurrentUserScope } from '../auth/current-user.tsx';
import { NewTrackDraftProvider, useNewTrackSession } from './new-track-drafts.tsx';
import { createMemoryStorage } from './share-reuse-fixture.ts';

function draftFixture() {
  const identity = { userId: 'create-replay-owner', displayName: 'Owner', username: 'owner', expiresAtMs: 2000000000000, role: 'owner', sessionId: 'one' } as const;
  const area: Area = { id: 'create-replay-area', name: 'Area', color: '#112233', defaultCwd: null,
    defaultTemplateId: null, kind: 'user', sort: 0, createdAt: 0, updatedAt: 0 };
  const storage = createMemoryStorage();
  const storageKey = createStorageKey('track-create-v1', identity.userId, area.id);
  const wrapper = ({ children }: { children: ReactNode }) => <CurrentUserScope value={identity}>
    <NewTrackDraftProvider submittedStorage={() => storage}>{children}</NewTrackDraftProvider>
  </CurrentUserScope>;
  const request = {
    key: 'original-key',
    body: {
      area_id: area.id, planner_provider: 'claude', cwd: '/repo', permissionMode: 'plan',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
    } as const,
  };
  return { area, storage, storageKey, request, mount: () => renderHook(() => useNewTrackSession(area.id, area), { wrapper }) };
}

it.each([undefined, 'Already submitted'])('restores an unconfirmed creation with first message %s and forgets it', (firstMessage) => {
  const { area, storage, storageKey, request: originalRequest, mount } = draftFixture();
  const original = mount();
  const key = original.result.current.session?.key;
  if (key === undefined) throw new Error('Draft did not initialize');
  const request = { ...originalRequest, key, ...(firstMessage === undefined ? {} : { message: firstMessage }) };
  act(() => { original.result.current.store.update(area.id, { request, creating: true }); });
  expect(JSON.parse(storage.getItem(storageKey) ?? 'null')).toStrictEqual(request);
  original.unmount();

  const reopened = mount();
  expect(reopened.result.current.session).toMatchObject({
    key, request, creating: false, createdTrackId: null, error: TRACK_CREATE_TEXT.unknown, permissionMode: 'plan',
    form: { message: firstMessage ?? '', cwd: request.body.cwd },
  });
  act(() => { reopened.result.current.store.forget(area.id); });
  expect(storage.getItem(storageKey)).toBeNull();
  reopened.unmount();
  const next = mount();
  expect(next.result.current.session?.request).toBeNull();
  expect(next.result.current.session?.key).not.toBe(key);
});

it.each([undefined, 'Already submitted'])('restores a confirmed creation with first message %s and forgets it', (firstMessage) => {
  const { area, storage, storageKey, request: originalRequest, mount } = draftFixture();
  const original = mount();
  const key = original.result.current.session?.key;
  if (key === undefined) throw new Error('Draft did not initialize');
  const request = { ...originalRequest, key, ...(firstMessage === undefined ? {} : { message: firstMessage }) };
  act(() => { original.result.current.store.update(area.id, { request, creating: true }); });
  act(() => { original.result.current.store.update(area.id, { createdTrackId: 'confirmed-track' }); });
  original.unmount();

  const reopened = mount();
  expect(reopened.result.current.session).toMatchObject({
    key: request.key, request, creating: false, createdTrackId: 'confirmed-track', error: null, permissionMode: 'plan',
    form: { message: firstMessage ?? '', cwd: request.body.cwd },
  });
  act(() => { reopened.result.current.store.forget(area.id); });
  expect(storage.getItem(storageKey)).toBeNull();
  reopened.unmount();
  const next = mount();
  expect(next.result.current.session?.request).toBeNull();
  expect(next.result.current.session?.key).not.toBe(request.key);
});

it.each([{}, { createdTrackId: '' }, { createdTrackId: '  ' }, { createdTrackId: 123 }])(
  'restores an unconfirmed request with legacy or malformed confirmation %j', (confirmation) => {
    const { storage, storageKey, request, mount } = draftFixture();
    storage.setItem(storageKey, JSON.stringify({ ...request, ...confirmation }));
    expect(mount().result.current.session).toMatchObject({
      key: request.key, request, createdTrackId: null, error: TRACK_CREATE_TEXT.unknown,
    });
  },
);

it('clears the confirmation and storage when the request is cleared', () => {
  const { area, storage, storageKey, request, mount } = draftFixture();
  const draft = mount();
  act(() => { draft.result.current.store.update(area.id, { request, createdTrackId: 'confirmed-track' }); });
  expect(draft.result.current.session?.createdTrackId).toBe('confirmed-track');
  act(() => { draft.result.current.store.update(area.id, { request: null }); });
  expect(draft.result.current.session).toMatchObject({ request: null, createdTrackId: null });
  expect(storage.getItem(storageKey)).toBeNull();
});

it('clears the old confirmation when the request is replaced', () => {
  const { area, storage, storageKey, request, mount } = draftFixture();
  const draft = mount();
  act(() => { draft.result.current.store.update(area.id, { request, createdTrackId: 'confirmed-track' }); });
  expect(draft.result.current.session?.createdTrackId).toBe('confirmed-track');
  const replacement = { ...request, key: 'replacement-key' };
  act(() => { draft.result.current.store.update(area.id, { key: replacement.key, request: replacement }); });
  expect(draft.result.current.session).toMatchObject({ request: replacement, createdTrackId: null });
  expect(JSON.parse(storage.getItem(storageKey) ?? 'null')).toStrictEqual(replacement);
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

it.each(['local', 'jasper'])('restores the persisted execution target %s', (executionTargetId) => {
  const { storage, storageKey, request, mount } = draftFixture();
  storage.setItem(storageKey, JSON.stringify({ ...request, body: { ...request.body, execution_target_id: executionTargetId } }));
  const reopened = mount();
  expect(reopened.result.current.session?.form?.executionTargetId).toBe(executionTargetId);
});
