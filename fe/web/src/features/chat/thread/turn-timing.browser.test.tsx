import '../../../styles/entry.css';
import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';
import type { Conversation } from '../../../../../core/domain/conversation.ts';
import { buildConversationTranscript, conversationItemsOperation } from '../../../../../core/domain/conversation-display.ts';
import { formatTurnTimestamp } from '../../../../../core/domain/conversation-meta.ts';
import { CurrentStatusNotice } from './outcome-notice.tsx';
import { ChatThread } from './public.tsx';

afterEach(async () => {
  cleanup();
  vi.useRealTimers();
  await page.viewport(1280, 720);
});

it('ticks only a running lastTurn and shows duration alongside completion timestamp when finished', () => {
  vi.useFakeTimers(); vi.setSystemTime(100_000);
  const turn = { id: 'turn', sessionId: 'session', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: 82_000, completedAtMs: null };
  const view = render(<CurrentStatusNotice lastTurn={turn} copyAction={null} regenerateAction={null} />);
  const runningStatus = screen.getByRole('status', { name: 'Current response status' });
  expect(runningStatus.textContent).toContain('18s');
  expect(runningStatus.querySelector('time')).toBeNull();
  act(() => { vi.advanceTimersByTime(1000); });
  expect(runningStatus.textContent).toContain('19s');
  const completedAtMs = Date.UTC(2026, 10, 24, 6, 32, 8);
  view.rerender(<CurrentStatusNotice lastTurn={{ ...turn, status: 'completed', startedAtMs: completedAtMs - 83_000, completedAtMs }} copyAction={null} regenerateAction={null} />);
  const doneStatus = screen.getByRole('status', { name: 'Current response status' });
  expect(doneStatus.textContent).toContain('Done');
  expect(doneStatus.textContent).toContain('1m 23s');
  const time = doneStatus.querySelector('time')!;
  expect(time.checkVisibility()).toBe(true);
  expect(time.textContent).toBe(`Ended ${formatTurnTimestamp(completedAtMs)}`);
  expect(time.getAttribute('datetime')).toBe(new Date(completedAtMs).toISOString());
  expect(vi.getTimerCount()).toBe(0);
});

it.each([390, 1280])('shows persisted duration and completion timestamp for every finished turn and keeps earlier bars when another turn starts (%ipx)', async (width) => {
  await page.viewport(width, 844);
  const dayOneEnd = Date.UTC(2026, 10, 23, 15, 10, 5);
  const dayTwoEnd = Date.UTC(2026, 10, 24, 6, 32, 8);
  const turn1 = { id: 'trn_1', sessionId: 'ses_1', turnIndex: 1, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: dayOneEnd - 45_000, completedAtMs: dayOneEnd };
  const turn2 = { id: 'trn_2', sessionId: 'ses_1', turnIndex: 2, epoch: 1, triggerKind: 'user', status: 'failed', startedAtMs: dayTwoEnd - 83_000, completedAtMs: dayTwoEnd, errorText: 'Provider timeout' };
  const turn3Running = { id: 'trn_3', sessionId: 'ses_1', turnIndex: 3, epoch: 1, triggerKind: 'user', status: 'running', startedAtMs: dayTwoEnd + 10_000, completedAtMs: null };
  const conversation: Conversation = { id: 'ses_1', trackId: 'trk_1', title: 'Multi-turn', kind: 'agent', provider: 'codex', state: 'idle', updatedAt: dayTwoEnd };

  const parsedPage = conversationItemsOperation('ses_1', null).responseSchema.parse({
    items: [
      { id: 1, sessionId: 'ses_1', turnId: 'trn_1', kind: 'user', payload: { content: 'First prompt' }, createdAtMs: dayOneEnd - 45_000 },
      { id: 2, sessionId: 'ses_1', turnId: 'trn_1', kind: 'assistant_text', payload: { text: 'First answer' }, createdAtMs: dayOneEnd },
      { id: 3, sessionId: 'ses_1', turnId: 'trn_2', kind: 'user', payload: { content: 'Second prompt' }, createdAtMs: dayTwoEnd - 83_000 },
      { id: 4, sessionId: 'ses_1', turnId: 'trn_2', kind: 'turn_outcome', payload: { status: 'failed', error: 'Provider timeout' }, createdAtMs: dayTwoEnd },
    ],
    activeTurn: null,
    lastTurn: turn2,
    turns: [turn1, turn2],
    state: 'idle',
    readonlyReason: null,
    canReopen: false,
    workerPaused: false,
    unknownDetail: null,
    attachmentsSupported: false,
    session: {
      id: 'ses_1', trackId: 'trk_1', isLead: true, title: 'Multi-turn', provider: 'codex', model: null,
      nativeThreadId: 'thr_1', epoch: 1, status: 'idle', contextUsage: null, archivedAtMs: null, createdAtMs: dayOneEnd - 50_000, updatedAtMs: dayTwoEnd,
    },
  });
  const entries = buildConversationTranscript(parsedPage.items, null);
  const view = render(
    <div style={{ width }}>
      <ChatThread
        conversation={conversation}
        turns={entries}
        sessionTurns={parsedPage.turns}
        lastSessionTurn={turn2}
        hasPendingMessages={false}
        canContinue
        cards={{}}
        stalled={false}
      />
    </div>,
  );

  const historical = screen.getByRole('status', { name: 'Historical response status' });
  expect(historical.textContent).toContain('Done');
  expect(historical.textContent).toContain('45s');
  expect(historical.querySelector('time')?.textContent).toBe(`Ended ${formatTurnTimestamp(dayOneEnd)}`);
  expect(historical.querySelector('time')?.checkVisibility()).toBe(true);

  const current = screen.getByRole('status', { name: 'Current response status' });
  expect(current.textContent).toContain('Turn failed');
  expect(current.textContent).toContain('1m 23s');
  expect(current.querySelector('time')?.textContent).toBe(`Ended ${formatTurnTimestamp(dayTwoEnd)}`);
  expect(current.querySelector('time')?.checkVisibility()).toBe(true);
  expect(document.documentElement.scrollWidth).toBe(width);

  // Starting turn 3 keeps turn 1 and turn 2 completion bars intact without fabricating a completion timestamp for turn 3,
  // and never renders an unloaded turn whose items are not present in the loaded transcript.
  const unloadedTurn = { id: 'trn_unloaded', sessionId: 'ses_1', turnIndex: 3, epoch: 1, triggerKind: 'user', status: 'completed', startedAtMs: dayTwoEnd + 1_000, completedAtMs: dayTwoEnd + 4_000 };
  const turn4Running = { ...turn3Running, id: 'trn_4', turnIndex: 4 };
  view.rerender(
    <div style={{ width }}>
      <ChatThread
        conversation={conversation}
        turns={[...entries, { id: 'human:3', author: 'you', text: 'Third prompt', atMs: dayTwoEnd + 10_000, sessionTurnId: 'trn_4' }]}
        sessionTurns={[turn1, turn2, unloadedTurn, turn4Running]}
        lastSessionTurn={turn4Running}
        hasPendingMessages={false}
        canContinue
        cards={{}}
        stalled={false}
      />
    </div>,
  );

  const historicalBars = screen.getAllByRole('status', { name: 'Historical response status' });
  expect(historicalBars).toHaveLength(2);
  expect(historicalBars[0]?.querySelector('time')?.textContent).toBe(`Ended ${formatTurnTimestamp(dayOneEnd)}`);
  expect(historicalBars[1]?.querySelector('time')?.textContent).toBe(`Ended ${formatTurnTimestamp(dayTwoEnd)}`);
  expect(screen.getByRole('status', { name: 'Current response status' }).querySelector('time')).toBeNull();
  expect(document.documentElement.scrollWidth).toBe(width);
});
