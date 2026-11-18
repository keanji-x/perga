import { afterEach, describe, expect, it, vi } from 'vitest';

import { performApiRequest } from '../../../../core/api/client.ts';
import { updateWorkerSettingsOperation } from '../../../../core/domain/worker-session.ts';
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
  it('sends Worker approval proof in a header and rejects redirects', async () => {
    const settings = { attemptId: 'attempt', version: 1, model: null, reasoningEffort: null, approvalMode: 'ask' } as const;
    const proof = 'a'.repeat(64);
    const fetch = vi.fn<(path: string, init: RequestInit) => Promise<Response>>()
      .mockResolvedValue(new Response('{}', { status: 400 }));
    vi.stubGlobal('fetch', fetch);

    await performApiRequest(createFetchTransport(), updateWorkerSettingsOperation('worker', settings, proof));

    expect(fetch).toHaveBeenCalledTimes(1);
    const [path, init] = fetch.mock.calls[0];
    expect(path).toBe('/api/v1/sessions/worker/settings');
    expect(init.redirect).toBe('error');
    expect(new Headers(init.headers).get('X-Perga-Worker-Approval-Key')).toBe(proof);
    expect(init.body).toBe(JSON.stringify(settings));
  });

  function capture() {
    const seen: { body?: BodyInit | null } = {};
    vi.stubGlobal('fetch', vi.fn((_path: string, init?: RequestInit) => {
      seen.body = init?.body ?? null;
      return Promise.resolve(new Response('{}', { status: 200, statusText: 'OK' }));
    }));
    return seen;
  }

  /* `JSON.stringify` of a typed array is `{"0":137,"1":80,…}` — not the file, not an error, and nothing in the type system objects. */
  it('sends a Uint8Array body as the bytes themselves', async () => {
    const seen = capture();
    const bytes = new Uint8Array([0x89, 0x50, 0x4e, 0x47]);
    await createFetchTransport().send({
      method: 'POST', path: '/api/cards/c/planner/attachments', credentials: 'include',
      headers: { 'content-type': 'image/png' }, body: bytes,
    });
    expect(seen.body).toBe(bytes);
  });

  it('still serializes an ordinary object body as JSON', async () => {
    const seen = capture();
    await createFetchTransport().send({
      method: 'POST', path: '/api/cards/c/planner/input', credentials: 'include',
      body: { text: 'hello' },
    });
    expect(seen.body).toBe('{"text":"hello"}');
  });
});
