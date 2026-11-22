import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { DATABASE_ID_KEY, DB_INSTANCE_ID_KEY } from '../../../../core/keys/storage.ts';
import { ServerCompatGate, WEB_COMPAT_VERSION } from './public.tsx';
import { createUiPreferences, UiPreferencesProvider } from './ui-preferences.tsx';

afterEach(cleanup);

it('database_id_change_overwrites_the_key', async () => {
  // The per-boot instance id is unchanged here, so none of the cache busting runs.
  const values = new Map<string, string>([[DB_INSTANCE_ID_KEY, 'boot-1'], [DATABASE_ID_KEY, 'db-old']]);
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
  const reload = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const runtime = {
    storage, reload, deleteDatabase: () => {}, idbDatabaseName: 'test',
    fetchVersion: () => Promise.resolve({ webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION,
      syncEventVersion: 20, dbInstanceId: 'boot-1', databaseId: 'db-new' }),
  };
  const preferences = createUiPreferences(storage);
  expect(preferences.databaseScope()).toBe('db-old');
  try {
    render(<QueryClientProvider client={client}>
      <ServerCompatGate client={client} runtime={runtime} cursorStore={{ clear() {} }}>
        <UiPreferencesProvider preferences={preferences}><p>Route</p></UiPreferencesProvider>
      </ServerCompatGate>
    </QueryClientProvider>);
    await waitFor(() => expect(values.get(DATABASE_ID_KEY)).toBe('db-new'));
    await waitFor(() => expect(preferences.databaseScope()).toBe('db-new'));
    expect(reload).not.toHaveBeenCalled();
    expect(values.get(DB_INSTANCE_ID_KEY)).toBe('boot-1');
  } finally { cleanup(); client.clear(); }
});

it('leaves the database scope null, and no Area pin applied, when the kernel reports no database identity', async () => {
  const values = new Map<string, string>();
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const runtime = {
    storage, reload: () => {}, deleteDatabase: () => {}, idbDatabaseName: 'test',
    fetchVersion: () => Promise.resolve({ webCompatVersion: WEB_COMPAT_VERSION, minWebCompatVersion: WEB_COMPAT_VERSION,
      syncEventVersion: 20, dbInstanceId: 'boot-1' }),
  };
  const preferences = createUiPreferences(storage);
  try {
    render(<QueryClientProvider client={client}>
      <ServerCompatGate client={client} runtime={runtime} cursorStore={{ clear() {} }}>
        <UiPreferencesProvider preferences={preferences}><p>Route</p></UiPreferencesProvider>
      </ServerCompatGate>
    </QueryClientProvider>);
    await waitFor(() => expect(values.get(DB_INSTANCE_ID_KEY)).toBe('boot-1'));
    expect(values.has(DATABASE_ID_KEY)).toBe(false);
    expect(preferences.databaseScope()).toBeNull();
    preferences.setAreaTrackPinned('area', 't', true);
    expect(preferences.areaTrackPinned('area', 't')).toBe(false);
  } finally { cleanup(); client.clear(); }
});
