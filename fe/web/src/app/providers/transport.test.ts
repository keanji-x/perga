import { afterEach, describe, expect, it, vi } from 'vitest';

import { performApiRequest } from '../../../../core/api/client.ts';
import { sendPlannerInputOperation } from '../../../../core/domain/conversation-commands.ts';
import { setPlannerPermissionModeOperation } from '../../../../core/domain/planner-permission-mode.ts';
import { createFetchTransport } from './transport.ts';

const request = { method: 'DELETE', path: '/api/tracks/w1', credentials: 'include' } as const;

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('fetch transport cancellation', () => {
  it('relays caller abort to fetch', async () => {
    let fetchSignal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_path: string, init?: RequestInit) => {
      fetchSignal = init?.signal as AbortSignal;
      return new Promise<Response>((_resolve, reject) => {
        fetchSignal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      });
    }));
    const controller = new AbortController();
    const pending = createFetchTransport().send({ ...request, signal: controller.signal });
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(fetchSignal?.aborted).toBe(true);
  });

  it('aborts a request after the transport timeout', async () => {
    vi.useFakeTimers();
    let fetchSignal: AbortSignal | undefined;
    vi.stubGlobal('fetch', vi.fn((_path: string, init?: RequestInit) => {
      fetchSignal = init?.signal as AbortSignal;
      return new Promise<Response>((_resolve, reject) => {
        fetchSignal?.addEventListener('abort', () => reject(new DOMException('Request timed out.', 'TimeoutError')));
      });
    }));
    const pending = createFetchTransport().send(request);
    const rejected = expect(pending).rejects.toMatchObject({ name: 'TimeoutError' });
    await vi.advanceTimersByTimeAsync(29_999);
    expect(fetchSignal?.aborted).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    await rejected;
    expect(fetchSignal?.aborted).toBe(true);
  });
});

describe('fetch transport bodies', () => {
  it('updates session permissions through the unified PATCH', async () => {
    const fetch = vi.fn<(path: string, init: RequestInit) => Promise<Response>>()
      .mockResolvedValue(new Response('{}', { status: 400 }));
    vi.stubGlobal('fetch', fetch);
    await performApiRequest(createFetchTransport(), setPlannerPermissionModeOperation('worker', 'auto'));
    const [path, init] = fetch.mock.calls[0];
    expect(path).toBe('/api/v1/sessions/worker');
    expect(init.method).toBe('PATCH');
    expect(init.body).toBe(JSON.stringify({ permissionMode: 'auto' }));
  });

  function capture() {
    const seen: { body?: BodyInit | null } = {};
    vi.stubGlobal('fetch', vi.fn((_path: string, init?: RequestInit) => {
      seen.body = init?.body ?? null;
      return Promise.resolve(new Response('{}', { status: 200, statusText: 'OK' }));
    }));
    return seen;
  }

  it('passes one multipart body to fetch without overriding its boundary header', async () => {
    const fetch = vi.fn<(path: string, init: RequestInit) => Promise<Response>>()
      .mockResolvedValue(new Response('{}', { status: 400 }));
    vi.stubGlobal('fetch', fetch);
    const file = new File(['image bytes'], 'image.png', { type: 'image/png' });
    await performApiRequest(createFetchTransport(), sendPlannerInputOperation('session', '', [file], 'the-key'));
    const [path, init] = fetch.mock.calls[0];
    expect(path).toBe('/api/v1/sessions/session/messages');
    expect(init.headers).toEqual({ 'Idempotency-Key': 'the-key' });
    expect(init.body).toBeInstanceOf(FormData);
    const form = init.body as FormData;
    expect(form.get('payload')).toBe('{"content":"","mode":"default"}');
    expect(form.getAll('attachments')).toEqual([file]);
  });

  it('still serializes an ordinary object body as JSON', async () => {
    const seen = capture();
    await createFetchTransport().send({
      method: 'POST', path: '/api/v1/sessions/session/messages', credentials: 'include',
      body: { text: 'hello' },
    });
    expect(seen.body).toBe('{"text":"hello"}');
  });
});
