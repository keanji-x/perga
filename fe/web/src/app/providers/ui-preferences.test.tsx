import { cleanup } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';

import { DATABASE_ID_KEY, DB_INSTANCE_ID_KEY } from '../../../../core/keys/storage.ts';
import { createUiPreferences } from './ui-preferences.tsx';

afterEach(cleanup);

function memoryStorage() {
  const values = new Map<string, string>();
  return { values, getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); } };
}

describe('browser display preferences', () => {
  it('restores independent Track and Area choices with a fresh store', () => {
    const storage = memoryStorage();
    const first = createUiPreferences(storage);
    first.setConversation('a:/', 'chat-a');
    first.setConversation('b', 'chat-b');
    first.setAreaExpanded('work', false);
    first.setAreaExpanded('home', true);
    first.setRailCollapsed(true);
    first.setAreaShowsClosed('work', true);
    const restored = createUiPreferences(storage);
    expect(restored.conversation('a:/')).toBe('chat-a');
    expect(restored.conversation('b')).toBe('chat-b');
    expect(restored.conversation('new')).toBeNull();
    expect(restored.areaExpanded('work')).toBe(false);
    expect(restored.areaExpanded('home')).toBe(true);
    expect(restored.railCollapsed()).toBe(true);
    expect(restored.areaShowsClosed('work')).toBe(true);
    expect(restored.areaShowsClosed('home')).toBe(false);
    restored.setConversation('a:/', null);
    const closed = createUiPreferences(storage);
    expect(closed.conversation('a:/')).toBeNull();
    expect(closed.conversation('b')).toBe('chat-b');
  });

  it('keeps a terminal layout choice across app instances and scopes it to the database', () => {
    const storage = memoryStorage();
    const first = createUiPreferences(storage);
    first.setDatabaseScope('database-1');
    first.setTerminalWorkbench('track', 'terminal', false);

    const restored = createUiPreferences(storage);
    restored.setDatabaseScope('database-1');
    expect(restored.terminalWorkbench('track', 'terminal')).toBe(false);
    expect(restored.terminalWorkbench('track', 'other-terminal')).toBeNull();
    restored.setDatabaseScope('database-2');
    expect(restored.terminalWorkbench('track', 'terminal')).toBeNull();
  });

  it.each(['{broken', '[]', '{}', '123', '""'])('ignores malformed or unrecognized storage %s', (value) => {
    const preferences = createUiPreferences({ getItem: () => value, setItem: () => {} });
    expect(preferences.conversation('track')).toBeNull();
    expect(preferences.areaExpanded('area')).toBe(true);
    expect(preferences.areaShowsClosed('area')).toBe(false);
    expect(preferences.railCollapsed()).toBeNull();
  });

  it('remembers a preview block\'s device choice per Track and key across instances (#1780)', () => {
    const storage = memoryStorage();
    const first = createUiPreferences(storage);
    first.setPreviewViewport('t1', 'fe', 'mobile');
    first.setPreviewViewport('t2', 'fe', 'desktop');
    const restored = createUiPreferences(storage);
    expect(restored.previewViewport('t1', 'fe')).toBe('mobile');
    expect(restored.previewViewport('t2', 'fe')).toBe('desktop');
    expect(restored.previewViewport('t1', 'api')).toBeNull();
    const throwing = createUiPreferences({
      getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); },
    });
    expect(throwing.previewViewport('t1', 'fe')).toBeNull();
    throwing.setPreviewViewport('t1', 'fe', 'mobile');
    expect(throwing.previewViewport('t1', 'fe')).toBe('mobile');
  });

  /* The shell applies it (`app/shell/drawer-width.browser.test.tsx`); this is what is remembered. */
  describe('the conversation drawer width', () => {
    it('is unset by default and remembered per browser across app instances', () => {
      const storage = memoryStorage();
      const first = createUiPreferences(storage);
      expect(first.drawerWidth()).toBeNull();
      first.setDrawerWidth(47.5);
      expect(createUiPreferences(storage).drawerWidth()).toBe(47.5);
      first.setDrawerWidth(null);
      expect(createUiPreferences(storage).drawerWidth()).toBeNull();
    });

    it('keeps the width in memory when browser storage throws', () => {
      const preferences = createUiPreferences({ getItem() { throw new Error('denied'); }, setItem() { throw new Error('quota'); } });
      expect(preferences.drawerWidth()).toBeNull();
      preferences.setDrawerWidth(30);
      expect(preferences.drawerWidth()).toBe(30);
    });

    it.each(['true', '"wide"', '"-4"', '"0"', '"Infinity"', '42'])('reads %s as no width', (stored) => {
      expect(createUiPreferences({ getItem: () => stored, setItem: () => {} }).drawerWidth()).toBeNull();
    });
  });

  it('rejects valid JSON of the wrong preference type', () => {
    const string = createUiPreferences({ getItem: () => '"chat"', setItem: () => {} });
    expect(string.areaExpanded('area')).toBe(true);
    expect(string.railCollapsed()).toBeNull();
    const boolean = createUiPreferences({ getItem: () => 'false', setItem: () => {} });
    expect(boolean.conversation('track')).toBeNull();
  });

  it('keeps choices in memory when browser storage throws', () => {
    const preferences = createUiPreferences({
      getItem() { throw new Error('denied'); }, setItem() { throw new Error('quota'); },
    });
    preferences.setConversation('track', 'chat');
    preferences.setAreaExpanded('area', false);
    preferences.setRailCollapsed(false);
    expect(preferences.conversation('track')).toBe('chat');
    expect(preferences.areaExpanded('area')).toBe(false);
    expect(preferences.railCollapsed()).toBe(false);
    preferences.setConversation('track', null);
    expect(preferences.conversation('track')).toBeNull();
  });

  it('does not overwrite choices for other entities from a second app instance', () => {
    const storage = memoryStorage();
    const first = createUiPreferences(storage);
    const second = createUiPreferences(storage);
    first.setConversation('a', 'chat-a');
    second.setConversation('b', 'chat-b');
    first.setAreaExpanded('area-a', false);
    second.setAreaExpanded('area-b', false);
    const restored = createUiPreferences(storage);
    expect(restored.conversation('a')).toBe('chat-a');
    expect(restored.conversation('b')).toBe('chat-b');
    expect(restored.areaExpanded('area-a')).toBe(false);
    expect(restored.areaExpanded('area-b')).toBe(false);
  });
});

it('seeds the database scope from the stable database id, never the per-boot instance id', () => {
  const storage = memoryStorage();
  storage.values.set(DB_INSTANCE_ID_KEY, 'boot-42');
  expect(createUiPreferences(storage).databaseScope()).toBeNull();
  storage.values.set(DATABASE_ID_KEY, 'db1');
  expect(createUiPreferences(storage).databaseScope()).toBe('db1');
});

it('keeps sidebar display choices across app instances', () => {
  const storage = memoryStorage();
  const first = createUiPreferences(storage);
  first.setSidebarGroupVisible('focus', false);
  first.setSidebarGroupVisible('pinned', false);
  first.setSidebarGroupExpanded('pinned', false);
  const restored = createUiPreferences(storage);
  expect(restored.sidebarGroupVisible('focus')).toBe(false);
  expect(restored.sidebarGroupVisible('pinned')).toBe(false);
  expect(restored.sidebarGroupExpanded('pinned')).toBe(false);
});

it('keeps order and visibility across app instances, with an in-memory fallback', () => {
  const storage = memoryStorage();
  const first = createUiPreferences(storage);
  first.setSidebarOrder('sections', ['areas', 'pinned', 'focus']);
  first.setSidebarOrder('areas', ['b', 'a']);
  first.setSidebarGroupVisible('focus', false);
  first.setSidebarGroupVisible('area:a', false);
  const restored = createUiPreferences(storage);
  expect(restored.sidebarOrder('sections', ['focus', 'pinned', 'areas'])).toEqual(['areas', 'pinned', 'focus']);
  expect(restored.sidebarOrder('areas', ['a', 'b', 'new'])).toEqual(['b', 'a', 'new']);
  expect(restored.sidebarGroupVisible('focus')).toBe(false);
  expect(restored.sidebarGroupVisible('area:a')).toBe(false);
  const memory = createUiPreferences({ getItem() { throw new Error('denied'); }, setItem() { throw new Error('quota'); } });
  memory.setSidebarOrder('areas', ['b', 'a']);
  memory.setSidebarGroupVisible('area:a', false);
  expect(memory.sidebarOrder('areas', ['a', 'b'])).toEqual(['b', 'a']);
  expect(memory.sidebarGroupVisible('area:a')).toBe(false);
});

it('isolates Area pins by database and preserves them across app instances', () => {
  const storage = memoryStorage();
  const preferences = createUiPreferences(storage);
  preferences.setDatabaseScope('db1');
  preferences.setAreaTrackPinned('area', 't', true);
  expect(preferences.areaTrackPinned('other-area', 't')).toBe(false);
  preferences.setDatabaseScope('db2');
  expect(preferences.areaTrackPinned('area', 't')).toBe(false);
  const restored = createUiPreferences(storage);
  restored.setDatabaseScope('db1');
  expect(restored.areaTrackPinned('area', 't')).toBe(true);
  restored.setDatabaseScope(null);
  restored.setAreaTrackPinned('area', 't', true);
  expect(restored.areaTrackPinned('area', 't')).toBe(false);
});
