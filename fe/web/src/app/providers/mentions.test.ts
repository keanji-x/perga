import { expect,it } from 'vitest';
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
