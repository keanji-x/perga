import { expect, it } from 'vitest';
import { decodeRecoveryContext, recoveryPage } from './context.js';
it('persists only allowlisted resource IDs and page layout without arbitrary URL data', () => {
  expect(recoveryPage('/track/t1', '?file=secrets.txt&token=secret&card=c1&panel=cards')).toEqual({
    route: '/track/t1?card=c1&panel=cards', kind: 'track', pane: 'cards',
  });
  expect(recoveryPage('/area/a1/new')).toEqual({ route: '/', kind: 'today', pane: null });
  expect(recoveryPage('/track/%2e%2e').kind).toBe('today');
});
it('corrupt, future, cross-origin and unbounded presentation records fail closed', () => {
  const scope = { origin: 'https://example.test', userId: 'owner', dbInstanceId: 'db' };
  const record = { schemaVersion: 1, scope, page: recoveryPage('/track/t1'), scroll: [{ region: 'page', top: 50, left: 0 }] };
  expect(decodeRecoveryContext(JSON.stringify(record), scope.origin)).toEqual(record);
  for (const invalid of [ { ...record, schemaVersion: 2 }, { ...record, scroll: [{ region: 'page', top: -1, left: 0 }] },
    { ...record, page: { ...record.page, route: 'https://other.test' } }, { ...record, scope: { ...scope, origin: 'https://other.test' } } ]) {
    expect(decodeRecoveryContext(JSON.stringify(invalid), scope.origin)).toBeNull();
  }
});

it('restores only current root-mounted settings routes', () => {
  for (const path of ['/settings', '/settings/planners', '/settings/appearance', '/settings/about']) {
    expect(recoveryPage(path)).toEqual({ route: path, kind: 'settings', pane: null });
  }
  for (const path of ['/recipes', '/settings/network', '/settings/plugins', '/settings/general']) {
    expect(recoveryPage(path)).toEqual({ route: '/', kind: 'today', pane: null });
  }
});
