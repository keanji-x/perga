// @vitest-environment jsdom
import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { ConversationProvider, useConversationCommands } from './public.tsx';
import { useConversationOutbox } from './outbox.ts';
import type { ApiTransportPort } from '../../../../core/api/types.ts';
import { sendPlannerInputOperation } from '../../../../core/domain/conversation.ts';

const transport: ApiTransportPort = { send: () => Promise.reject(new Error('unused')) };
afterEach(() => { cleanup(); vi.useRealTimers(); });
function setup(send = vi.fn().mockResolvedValue({ card_id: 'a', worker_session_id: 'a', entry_id: 'input', turnId: 'accepted-turn' })) {
  return { send, ...renderHook(() => ({ registry: useConversationCommands(), outbox: useConversationOutbox({
    cardId: 'a', transport, send, serverEntries: [], liveReplies: [], stalled: false, queuesInput: false,
    queueIfBusy: false, pressed: () => {},
  }) }), { wrapper: ({ children }: { children: ReactNode }) => <ConversationProvider>{children}</ConversationProvider> }) };
}
it('preserves the accepted turn identity from the wire through outbox settlement', async () => {
  const parsed = sendPlannerInputOperation('a', 'text', [], 'key').responseSchema.parse({ sessionId: 'a', inputId: 'input',
    turnId: 'accepted-turn', deliveryState: 'accepted', turnStatus: 'queued', statusUrl: '/status' });
  expect(parsed.turnId).toBe('accepted-turn');
  const { result } = setup(vi.fn().mockResolvedValue(parsed));
  await act(async () => { await result.current.outbox.send('a', 'text', [], true); });
  expect(result.current.registry.acceptedTurnOf('a')).toBe('accepted-turn');
});
it.each(['reset', 'epoch'] as const)('fences permission-deferred sends on %s and restores intent', async boundary => {
  const { result, send } = setup();
  let release!: () => void;
  const permission = new Promise<void>(resolve => { release = resolve; });
  act(() => { result.current.registry.observeEpoch('a', 1); void result.current.registry.holdPermissionWrite('a', permission); });
  let delivery: Promise<void> | null = null;
  act(() => { delivery = result.current.outbox.send('a', 'retained', [], true); });
  expect(send).not.toHaveBeenCalled();
  act(() => { if (boundary === 'reset') result.current.registry.invalidateSends('a'); else result.current.registry.observeEpoch('a', 2); });
  await act(async () => { release(); await delivery; });
  expect(send).not.toHaveBeenCalled();
  expect(result.current.registry.composerOf('a').text).toBe('retained');
  expect(result.current.registry.outboxOf('a')).toEqual([]);
});
it('fences automatic retry timers and ignores late replies after reset', async () => {
  vi.useFakeTimers();
  const { result, send } = setup(vi.fn().mockRejectedValue(new Error('lost response')));
  let delivery: Promise<void> | null = null;
  await act(async () => { delivery = result.current.outbox.send('a', 'retry intent', [], true); await Promise.resolve(); });
  expect(send).toHaveBeenCalledOnce();
  act(() => { result.current.registry.invalidateSends('a'); });
  await act(async () => { await vi.runAllTimersAsync(); await delivery; });
  expect(send).toHaveBeenCalledOnce();
  expect(result.current.registry.composerOf('a').text).toBe('retry intent');
  expect(result.current.registry.outboxOf('a')).toEqual([]);
});

it('ignores a pre-reset successful reply without clearing restored draft or adopting old identity', async () => {
  let answer!: (value: { card_id: string; worker_session_id: string; entry_id: string; turnId: string }) => void;
  const pending = new Promise<{ card_id: string; worker_session_id: string; entry_id: string; turnId: string }>(resolve => { answer = resolve; });
  const { result } = setup(vi.fn(() => pending));
  let delivery: Promise<void> | null = null;
  act(() => { delivery = result.current.outbox.send('a', 'original', [], true); });
  act(() => { result.current.registry.invalidateSends('a'); });
  await act(async () => { answer({ card_id: 'a', worker_session_id: 'a', entry_id: 'old', turnId: 'old-turn' }); await delivery; });
  expect(result.current.registry.composerOf('a').text).toBe('original');
  expect(result.current.registry.acceptedTurnOf('a')).toBeNull();
});
