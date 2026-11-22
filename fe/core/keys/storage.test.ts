import { describe, expect, it } from 'vitest';

import { IDB_DB_NAME, DATABASE_ID_KEY, DB_INSTANCE_ID_KEY, SYNC_CURSOR_KEY, THEME_KEY, createStorageKey } from './storage.js';

describe('stable storage keys', () => {
  it('pins the new Perga namespace independently of production constants', () => {
    expect(IDB_DB_NAME).toBe('perga');
    expect(SYNC_CURSOR_KEY).toBe('perga:sync:cursor');
    expect(DB_INSTANCE_ID_KEY).toBe('perga:db_instance_id');
    expect(DATABASE_ID_KEY).toBe('perga:database_id');
    expect(THEME_KEY).toBe('perga:theme');
  });

  it('creates namespaced colon-separated keys', () => {
    expect(createStorageKey('draft', 'report', '42')).toBe('perga:draft:report:42');
  });

  it('rejects ambiguous dynamic key segments', () => {
    expect(() => createStorageKey('draft', '')).toThrow(TypeError);
    expect(() => createStorageKey('draft:report')).toThrow(TypeError);
  });
});
