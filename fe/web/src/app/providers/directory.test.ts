// The `ListDirectory` port, end to end over a recording transport — the real
// operation, the real decoder, and the real `joinDirectoryPath` the picker
// itself uses. `core/domain/fs.test.ts` pins the mapping with a stand-in join
// because `core` may not import `ui`; this is the half that proves the wiring
// hands the owner's function in, and that the two agree about where a row is.

import { describe,expect,it } from 'vitest';

import type { ApiRequest,ApiTransportPort } from '../../../../core/api/types.ts';
import { createUnauthorizedChannel } from '../../../../core/api/unauthorized.ts';
import { createDirectoryLister,createTrackCardFilesPort,createTrackWorkspaceFilesPort } from './directory.ts';

const unauthorized = createUnauthorizedChannel({ enqueue: (task: () => void) => task() });

function harness(body: unknown, status = 200) {
  const sent: ApiRequest[] = [];
  const transport: ApiTransportPort = {
    send(request) {
      sent.push(request);
      return Promise.resolve({ status, statusText: status === 200 ? 'OK' : 'Bad Request', body });
    },
  };
  return { sent, transport, listDirectory: createDirectoryLister(transport, unauthorized) };
}

describe('createDirectoryLister', () => {
  /* The shared read rule: a refusal adds the server's reason after the fixed sentence; a fault or lost answer does not. */
  it.each([
    [404, { error: 'path /gone not found', code: 'path_not_found' }, 'Could not list this folder. path /gone not found'],
    [403, { error: 'permission denied reading /root', code: 'forbidden' }, 'Could not list this folder. permission denied reading /root'],
    [500, { error: 'fs /gone: Input/output error', code: 'internal' }, 'Could not list this folder.'],
  ] as const)('rejects a %i listing with the sentence the browser shows', async (status, body, text) => {
    const { listDirectory } = harness(body, status);
    await expect(listDirectory('/gone')).rejects.toThrow(new Error(text));
  });

  it('asks for the server default when no path is given', async () => {
    const { sent, listDirectory } = harness({ path: '/home/kenji', parent: '/home', entries: [] });
    await listDirectory();
    expect(sent).toHaveLength(1);
    expect(sent[0]).toMatchObject({ method: 'GET', path: '/api/v1/fs/listdir', credentials: 'include' });
  });

  it('places every entry at an absolute path built by the browser\'s own join', async () => {
    const { sent, listDirectory } = harness({
      path: '/home/kenji',
      parent: '/home',
      entries: [
        { name: '.config', is_dir: true },
        { name: '.env', is_dir: false },
        { name: 'code', is_dir: true },
        { name: 'todo.txt', is_dir: false },
      ],
    });
    const listing = await listDirectory('/home/kenji');
    expect(sent[0]?.path).toBe('/api/v1/fs/listdir?path=%2Fhome%2Fkenji');
    expect(listing).toEqual({
      path: '/home/kenji',
      parent: '/home',
      entries: [
        { name: '.config', path: '/home/kenji/.config', isDirectory: true },
        { name: '.env', path: '/home/kenji/.env', isDirectory: false },
        { name: 'code', path: '/home/kenji/code', isDirectory: true },
        { name: 'todo.txt', path: '/home/kenji/todo.txt', isDirectory: false },
      ],
    });
  });

  /* The root is the case a naive `${parent}/${name}` gets wrong — it yields
     `//usr`, a path the next listdir would answer for a different directory on
     some platforms and which no `parent` chain leads back to. */
  it('joins at the filesystem root without doubling the separator', async () => {
    const { listDirectory } = harness({
      path: '/', parent: null, entries: [{ name: 'usr', is_dir: true }],
    });
    const listing = await listDirectory('/');
    expect(listing.parent).toBeNull();
    expect(listing.entries[0]?.path).toBe('/usr');
  });

  it('rejects with the API error the browser renders as its inline message', async () => {
    const { listDirectory } = harness({ error: 'path /nope is not a directory' }, 400);
    await expect(listDirectory('/nope')).rejects.toThrow('path /nope is not a directory');
  });
});

describe('createTrackWorkspaceFilesPort', () => {
  it('binds text and raw reads to their Track', async () => {
    const sent: ApiRequest[] = [];
    const transport: ApiTransportPort = {
      send(request) {
        sent.push(request);
        return Promise.resolve({
          status: 200,
          statusText: 'OK',
          body: { path: 'docs/a.md', size: 1, text: 'x', truncated: false },
        });
      },
    };
    const files = createTrackWorkspaceFilesPort(transport, unauthorized, 'track/1');
    await files.readFile('docs/a.md');
    expect(sent[0]).toMatchObject({
      method: 'GET',
      path: '/api/v1/tracks/track%2F1/files?path=docs%2Fa.md',
      credentials: 'include',
    });
    expect(files.rawUrl('img/a.png'))
      .toBe('/api/v1/tracks/track%2F1/files/raw?path=img%2Fa.png');
  });
});

describe('createTrackCardFilesPort', () => {
  it('uses relative paths for workspace directory, text and raw reads', async () => {
    const listing = harness({ relativePath: 'docs', isDirectory: true, entries: [] });
    const file = harness({ relativePath: 'docs/a.txt', isDirectory: false, content: 'a' });
    const folders = createTrackCardFilesPort(listing.transport, unauthorized, 'track/1', '/repo');
    const files = createTrackCardFilesPort(file.transport, unauthorized, 'track/1', '/repo');

    await expect(folders.listDirectory('/repo/docs')).resolves.toEqual({ path: '/repo/docs', parent: '/repo', entries: [] });
    await expect(files.readFile('/repo/docs/a.txt')).resolves.toMatchObject({ text: 'a' });
    expect(listing.sent[0]?.path).toBe('/api/v1/tracks/track%2F1/files?path=docs');
    expect(file.sent[0]?.path).toBe('/api/v1/tracks/track%2F1/files?path=docs%2Fa.txt');
    expect(files.rawUrl('/repo/docs/a.png')).toBe('/api/v1/tracks/track%2F1/files/raw?path=docs%2Fa.png');
  });

  it.each([
    ['/outside/logs', '/outside'],
    ['/outside', '/'],
    ['/', null],
  ])('keeps the canonical external directory %s and its parent', async (canonical, parent) => {
    const { sent, transport } = harness({ relativePath: canonical, isDirectory: true, entries: [] });
    const files = createTrackCardFilesPort(transport, unauthorized, 'track/1', '/repo');

    await expect(files.listDirectory('/repo/alias')).resolves.toEqual({ path: canonical, parent, entries: [] });
    expect(sent[0]?.path).toBe('/api/v1/tracks/track%2F1/files?path=alias');
    expect(files.rawUrl('/outside/a.png')).toBe('/api/v1/tracks/track%2F1/files/raw?path=%2Foutside%2Fa.png');
  });

});

it('keeps remote directory reads on their selected target including the initial home read', async () => {
  const paths: string[] = [];
  const transport: ApiTransportPort = { send: (request) => {
    paths.push(request.path);
    return Promise.resolve({ status: 200, statusText: 'OK', body: { path: '/srv', parent: '/', entries: [] } });
  } };
  const list = createDirectoryLister(transport, unauthorized, 'ssh/user &host');
  await list();
  await list('/srv/a &b');
  expect(paths).toEqual([
    '/api/v1/fs/listdir?targetId=ssh%2Fuser%20%26host',
    '/api/v1/fs/listdir?path=%2Fsrv%2Fa%20%26b&targetId=ssh%2Fuser%20%26host',
  ]);
});
