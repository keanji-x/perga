import { expect,it,vi } from 'vitest';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { mentionSearchOf } from './mentions.ts';

it('retains templates when the other mention sources fail', async () => {
  const transport: ApiTransportPort = { send(request) {
    return Promise.resolve(request.path === '/api/v1/templates'
      ? { status: 200, statusText: 'OK', body: [{ id: 'deploy', title: 'Deploy', description: null, availability: { status: 'ready' }, tasks: [] }] }
      : { status: 500, statusText: 'Error', body: { error: 'unavailable' } });
  } };
  const search = mentionSearchOf(transport, createUnauthorizedChannel({ enqueue: task => task() }), 'area', null);
  expect((await search('deploy', new AbortController().signal)).map(item => item.kind)).toEqual(['template']);
});

it('queries only Memory for a Memory prefix and keeps Area and Global suggestions in server order', async () => {
  const send = vi.fn<ApiTransportPort['send']>().mockResolvedValue({ status: 200, statusText: 'OK', body: [
    { ref: 'area/pinned', scope: 'area', key: 'pinned', title: 'Area pinned', summary: '', pinned: true, insert: '@`memory:area/pinned`' },
    { ref: 'area/notes', scope: 'area', key: 'notes', title: 'Area notes', summary: '', pinned: false, insert: '@`memory:area/notes`' },
    { ref: 'global/pinned', scope: 'global', key: 'pinned', title: 'Global pinned', summary: '', pinned: true, insert: '@`memory:global/pinned`' },
  ] });
  const unauthorized = createUnauthorizedChannel({ enqueue: task => task() });
  const signal = new AbortController().signal;
  const search = mentionSearchOf({ send }, unauthorized, 'area', null);
  expect((await search('memory：pin', signal)).map(item => item.label)).toEqual(['Area pinned', 'Area notes', 'Global pinned']);
  expect(send).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ path: '/api/v1/memory/mentions?areaId=area&q=pin', signal }));

  const sharedSearch = mentionSearchOf({ send }, unauthorized, 'area', null, undefined, false);
  expect(await sharedSearch('memory:', signal)).toEqual([]);
  expect(send).toHaveBeenCalledTimes(1);
});
