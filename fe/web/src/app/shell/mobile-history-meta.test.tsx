// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Conversation } from '../../../../core/domain/conversation.ts';
import { MobileHistory } from './mobile-history.tsx';

beforeEach(() => {
  if (typeof HTMLDialogElement !== 'undefined') {
    HTMLDialogElement.prototype.showModal = function showModal() { this.setAttribute('open', ''); };
    HTMLDialogElement.prototype.close = function close() { this.removeAttribute('open'); };
  }
});

afterEach(() => { cleanup(); });

const EMPTY_GROUPS = { pinned: [], unread: [], waiting: [] } as const;

function renderHistory(conversations: readonly Conversation[], now: number, loading = false) {
  return render(
    <MobileHistory
      owner={{}}
      open
      onOpenChange={vi.fn()}
      selectedConversationId={null}
      trackGroups={EMPTY_GROUPS}
      currentTrackId="t-today"
      isUnread={() => false}
      tracksLoading={false}
      tracksError={null}
      onRetryTracks={vi.fn()}
      onOpenTrack={vi.fn()}
      conversations={conversations}
      loading={loading}
      failed={false}
      onRetry={vi.fn()}
      onNew={vi.fn()}
      now={now}
      onSelect={vi.fn()}
      onOpenSettings={vi.fn()}
      scopeLabel="Current track · 2026-11-25"
      accountLabel="User"
      accountInitial="U"
    />,
  );
}

describe('MobileHistory row subtitles', () => {
  it('shows local time instead of duplicating the local date on Today rows across midnight UTC boundaries', () => {
    const earlyLocal = new Date(2026, 10, 25, 0, 30).getTime();
    const lateLocal = new Date(2026, 10, 25, 23, 30).getTime();
    const prevLocal = new Date(2026, 10, 24, 10, 15).getTime();

    const todayLeadEarly: Conversation = {
      id: 'c-today-early',
      trackId: 't-today',
      trackTitle: '2026-11-25',
      title: null,
      kind: 'agent',
      isLead: true,
      provider: 'claude',
      state: 'idle',
      updatedAt: earlyLocal,
      archivedAt: null,
    };
    const todaySubLate: Conversation = {
      ...todayLeadEarly,
      id: 'c-today-sub',
      isLead: false,
      updatedAt: lateLocal,
    };
    const namedLead: Conversation = {
      ...todayLeadEarly,
      id: 'c-named',
      trackId: 't-named',
      trackTitle: 'Mobile Polish',
      updatedAt: prevLocal,
    };
    renderHistory([todayLeadEarly, todaySubLate, namedLead], lateLocal);

    const rows = screen.getAllByRole('button', { name: /2026-11-25/ });
    expect(rows).toHaveLength(2);
    expect(rows[0]?.textContent).toContain('Lead · 00:30');
    expect(rows[0]?.textContent?.match(/2026-11-25/g)).toHaveLength(1);
    expect(rows[1]?.textContent).toContain('23:30');
    expect(rows[1]?.textContent?.match(/2026-11-25/g)).toHaveLength(1);

    const namedRow = screen.getByRole('button', { name: /Mobile Polish/ });
    expect(namedRow.textContent).toContain('Lead · 2026-11-24');
  });
});
