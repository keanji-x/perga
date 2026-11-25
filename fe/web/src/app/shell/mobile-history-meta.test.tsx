// @vitest-environment jsdom
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Conversation } from '../../../../core/domain/conversation.ts';
import { MobileHistory } from './mobile-history.tsx';

let originalShowModal: PropertyDescriptor | undefined;
let originalClose: PropertyDescriptor | undefined;

beforeEach(() => {
  if (typeof HTMLDialogElement !== 'undefined') {
    originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');
    originalClose = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close');
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      writable: true,
      value(this: HTMLDialogElement) { this.setAttribute('open', ''); },
    });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      writable: true,
      value(this: HTMLDialogElement) { this.removeAttribute('open'); },
    });
  }
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  if (typeof HTMLDialogElement !== 'undefined') {
    if (originalShowModal === undefined) Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
    else Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal);
    if (originalClose === undefined) Reflect.deleteProperty(HTMLDialogElement.prototype, 'close');
    else Object.defineProperty(HTMLDialogElement.prototype, 'close', originalClose);
  }
});

const EMPTY_GROUPS = { pinned: [], unread: [], waiting: [] } as const;

function stubResolvedTimeZone(timeZone: string) {
  const NativeFormatter = Intl.DateTimeFormat;
  vi.spyOn(Intl, 'DateTimeFormat').mockImplementation(function (locales, options) {
    const formatter = new NativeFormatter(locales, options);
    if (locales === undefined && options === undefined) {
      const resolved = formatter.resolvedOptions();
      Object.defineProperty(formatter, 'resolvedOptions', { value: () => ({ ...resolved, timeZone }) });
    }
    return formatter;
  });
}

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
  it('formats local dates and times deterministically across UTC midnight boundaries and exposes metadata accessibly', () => {
    // Pin Asia/Shanghai (UTC+8) so 2026-11-24T16:30:00Z is 2026-11-25 00:30 local (crosses UTC date boundary).
    stubResolvedTimeZone('Asia/Shanghai');
    const earlyLocalAcrossUtcMidnight = Date.UTC(2026, 10, 24, 16, 30);
    const lateLocal = Date.UTC(2026, 10, 25, 15, 30);
    const prevLocal = Date.UTC(2026, 10, 24, 2, 15);

    const todayLeadEarly: Conversation = {
      id: 'c-today-early',
      trackId: 't-today',
      trackTitle: '2026-11-25',
      title: null,
      kind: 'agent',
      isLead: true,
      provider: 'claude',
      state: 'idle',
      updatedAt: earlyLocalAcrossUtcMidnight,
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
    const firstDescId = rows[0]?.getAttribute('aria-describedby');
    expect(firstDescId).toBeTruthy();
    expect(document.getElementById(firstDescId!)?.textContent).toBe('Lead · 00:30');

    expect(rows[1]?.textContent).toContain('23:30');
    expect(rows[1]?.textContent?.match(/2026-11-25/g)).toHaveLength(1);
    const secondDescId = rows[1]?.getAttribute('aria-describedby');
    expect(secondDescId).toBeTruthy();
    expect(document.getElementById(secondDescId!)?.textContent).toContain('23:30');

    const namedRow = screen.getByRole('button', { name: /Mobile Polish/ });
    expect(namedRow.textContent).toContain('Lead · 2026-11-24');
  });
});
