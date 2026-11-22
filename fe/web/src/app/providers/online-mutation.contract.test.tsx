import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import { NotSentError } from '../../../../core/domain/failure-class.ts';
import { useOnlineMutation } from './online-mutation.ts';

afterEach(() => { cleanup(); onlineManager.setOnline(true); });

function renderMutation(mutationFn: (value: string) => Promise<string>) {
  const release = vi.fn();
  const acquireLocal = vi.fn(() => release);
  const client = new QueryClient({ defaultOptions: { mutations: { retry: 2, retryDelay: 0 } } });
  const view = renderHook(() => useOnlineMutation({ mutationFn, acquireLocal }), {
    wrapper: ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  });
  return { ...view, acquireLocal, release };
}

it('refuses an offline write immediately, releases local resources and dispatches nothing on reconnect', async () => {
  onlineManager.setOnline(false);
  const send = vi.fn((value: string) => Promise.resolve(value));
  const { result, acquireLocal, release } = renderMutation(send);
  await act(async () => {
    await expect(result.current.mutateAsync('offline')).rejects.toBeInstanceOf(NotSentError);
    onlineManager.setOnline(true);
  });
  expect(acquireLocal).toHaveBeenCalledExactlyOnceWith('offline');
  expect(release).toHaveBeenCalledOnce();
  expect(send).not.toHaveBeenCalled();
});

it.each([true, false])('holds the local resources until an online write settles (success: %s)', async success => {
  onlineManager.setOnline(true);
  const error = new Error('Write failed');
  const onSuccess = vi.fn();
  const onError = vi.fn();
  let answer!: () => void;
  const send = vi.fn((value: string) => new Promise<string>((resolve, reject) => {
    answer = () => { if (success) resolve(value); else reject(error); };
  }));
  const { result, acquireLocal, release } = renderMutation(send);
  let pending!: Promise<string>;
  await act(() => {
    pending = result.current.mutateAsync('online', { onSuccess, onError });
    return Promise.resolve();
  });
  expect(acquireLocal).toHaveBeenCalledExactlyOnceWith('online');
  expect(release).not.toHaveBeenCalled();
  await act(async () => {
    answer();
    if (success) await expect(pending).resolves.toBe('online');
    else await expect(pending).rejects.toBe(error);
  });
  expect(send).toHaveBeenCalledOnce();
  expect(success ? onSuccess : onError).toHaveBeenCalledWith(success ? 'online' : error, 'online', undefined, expect.anything());
  expect(release).toHaveBeenCalledOnce();
});
