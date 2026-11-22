import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeAll, expect, it, vi } from 'vitest';
import { page } from 'vitest/browser';

import '../../../styles/entry.css';
import { encodeTerminalInputFrame, TERMINAL_FRAME_CONTROL_JSON } from '../../terminal/protocol.ts';
import { createCardHost } from '../host.ts';
import { createCardRegistry } from '../registry.ts';
import { BoardHost } from '../ui/board-host.tsx';
import { registerAvailableBuiltinCards } from './register.ts';

// Load the real lazy chunk before timing socket behavior; cold Vite compilation
// belongs to fixture setup and can exceed the socket assertion's one-second wait.
beforeAll(async () => { await import('../../terminal/xterm-view.tsx'); });

function terminalTransport() {
  const sockets: Socket[] = [];
  class Socket {
    static OPEN = 1;
    static CONNECTING = 0;
    readyState = 0;
    onopen: (() => void) | null = null;
    onmessage: ((event: { data: Uint8Array }) => void) | null = null;
    onclose: ((event: { code: number; reason: string; wasClean: boolean }) => void) | null = null;
    onerror: (() => void) | null = null;
    sent: Uint8Array[] = [];
    readonly url: string;
    constructor(url: string) { this.url = url; sockets.push(this); }
    send(data: Uint8Array) { this.sent.push(data.slice()); }
    close() { this.readyState = 3; }
    disconnect() {
      this.readyState = 3;
      this.onclose?.({ code: 1006, reason: '', wasClean: false });
    }
    open(text: string) {
      this.readyState = 1;
      this.onopen?.();
      this.output(text);
    }
    output(text: string) { this.onmessage?.({ data: encodeTerminalInputFrame(text) }); }
    exit(code: number) {
      const payload = new TextEncoder().encode(JSON.stringify({ type: 'exit', exit_code: code }));
      this.onmessage?.({ data: Uint8Array.of(TERMINAL_FRAME_CONTROL_JSON, ...payload) });
    }
  }
  vi.stubGlobal('WebSocket', Socket);
  return sockets;
}

function mountTerminal(status: 'running' | 'starting' = 'running', width?: number, notice: string | null = null) {
  const registry = createCardRegistry();
  registerAvailableBuiltinCards(registry);
  const card = registry.resolve({ id: 'card-1', kind: 'terminal', payload: {},
    terminal: { id: 'pty-1', trackId: 'w1', title: '', cwd: '/tmp', createdAtMs: 1, status },
  });
  if (card === null) throw new Error('Missing terminal');
  const host = createCardHost(registry);
  const board = (next: string | null) => <div style={{ width }}><BoardHost host={host} items={[
    { card, title: 'Terminal', originalIndex: 0, deletable: true, activity: null, notice: next },
  ]} visible activeCardId="card-1" onRemoveCard={() => {}} /></div>;
  const mounted = render(board(notice));
  return Object.assign(mounted, { setNotice: (next: string | null) => mounted.rerender(board(next)) });
}

/* The head's activity indicator. Its absence after a successful connect is the
   assertion that matters here (#1722 §5.3, INV-APP-118): the kernel's verdict
   is `BoardHostItem.activity`, `null` in every mount below, and a socket
   coming up must not paint one — the connection is this tab's, not the
   card's work. */
const headIndicator = () => document.querySelector('[data-nc-card-cell] [data-nc-activity]');
/* The head's words. `null` is the connected state and nothing else: attached and
   not ended, only `connected` prints no text (`Connecting…` / `Disconnected` /
   `Connection error` are the other three) — so "no text" is the positive
   evidence that the socket came up before the indicator is asserted absent. */
const headStatusText = () => document.querySelector('[data-nc-card-drag] [role="status"]')?.textContent ?? null;

function buffer() {
  return (window as unknown as { __xtermDumps__?: Record<string, () => string> }).__xtermDumps__?.['pty-1']?.() ?? '';
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('shows a refused connection and reconnects the same terminal without creating a process', async () => {
  await page.viewport(1200, 800);
  const sockets = terminalTransport();
  const mounted = mountTerminal();
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => sockets[0].disconnect());
  expect(await screen.findByText('Disconnected')).toBeTruthy();
  expect(headIndicator()).toBeNull();
  await page.screenshot({ path: 'test-results/terminal-disconnected.png' });
  await userEvent.click(screen.getByRole('button', { name: 'Reconnect' }));
  await waitFor(() => expect(sockets).toHaveLength(2));
  expect(sockets[1].url).toBe(sockets[0].url);
  expect(screen.queryByRole('button', { name: 'Reconnect' })).toBeNull();
  act(() => sockets[1].open('restored prompt'));
  await waitFor(() => expect(headStatusText()).toBeNull());
  expect(headIndicator()).toBeNull();
  act(() => sockets[0].output('stale output'));
  expect(headStatusText()).toBeNull();
  expect(headIndicator()).toBeNull();
  expect(buffer()).not.toContain('stale output');
  mounted.unmount();
  expect(sockets.every((socket) => socket.readyState === 3 && socket.onmessage === null && socket.onclose === null)).toBe(true);
});

it('holds terminal output through failed reconnects and replaces it only with authoritative replay', async () => {
  const previousUrl = location.href;
  history.replaceState(null, '', `${location.pathname}?testMounts=1`);
  try {
    await page.viewport(1200, 800);
    const sockets = terminalTransport();
    mountTerminal();
    await waitFor(() => expect(sockets).toHaveLength(1));
    act(() => sockets[0].open('kept output'));
    await waitFor(() => expect(buffer()).toContain('kept output'));
    act(() => sockets[0].disconnect());
    expect(buffer()).toContain('kept output');
    await userEvent.click(screen.getByRole('button', { name: 'Reconnect' }));
    await waitFor(() => expect(sockets).toHaveLength(2));
    expect(buffer()).toContain('kept output');
    act(() => sockets[1].disconnect());
    expect(buffer()).toContain('kept output');
    await userEvent.click(screen.getByRole('button', { name: 'Reconnect' }));
    await waitFor(() => expect(sockets).toHaveLength(3));
    act(() => sockets[2].open('kept output\r\nnew output'));
    await waitFor(() => expect(buffer()).toContain('new output'));
    expect(buffer().match(/kept output/g)).toHaveLength(1);
    expect(sockets.flatMap((socket) => socket.sent).filter((frame) => frame[0] === 0x01)).toEqual([]);
    await page.screenshot({ path: 'test-results/terminal-reconnected.png' });
  } finally {
    history.replaceState(null, '', previousUrl);
  }
});

it.each(['running', 'starting'] as const)('retains process exit truth when REST still says %s', async (status) => {
  await page.viewport(1200, 800);
  const sockets = terminalTransport();
  mountTerminal(status);
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => {
    sockets[0].open('finished');
    sockets[0].exit(23);
    sockets[0].disconnect();
  });
  expect(await screen.findByText('Session exited.')).toBeTruthy();
  expect(headIndicator()).toBeNull();
  expect(screen.queryByRole('button', { name: 'Reconnect' })).toBeNull();
});


it('keeps input and the same attachment while the notice shows, changes and clears', async () => {
  await page.viewport(1200, 800);
  const sockets = terminalTransport();
  const inputs = () => sockets[0].sent.filter((frame) => frame[0] === 0x01);
  const notice = () => document.querySelector('[data-nc-card-cell] [data-nc-terminal-notice]');
  const mounted = mountTerminal('running', undefined, 'Completed — this attempt has ended.');
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => sockets[0].open('parked prompt'));
  await waitFor(() => expect(headStatusText()).toBeNull());
  expect(notice()?.textContent).toContain('Completed — this attempt has ended.');
  await userEvent.click(screen.getByLabelText('Terminal input'));
  await userEvent.keyboard('x');
  await waitFor(() => expect(inputs()).toHaveLength(1));

  mounted.setNotice('Failed — this attempt has ended.');
  expect(notice()?.textContent).toContain('Failed — this attempt has ended.');
  mounted.setNotice(null);
  expect(notice()).toBeNull();
  await userEvent.click(screen.getByLabelText('Terminal input'));
  await userEvent.keyboard('y');
  await waitFor(() => expect(inputs()).toHaveLength(2));
  expect(sockets).toHaveLength(1);
  expect(sockets[0].readyState).toBe(1);
  expect(sockets[0].sent.filter(frame => frame[0] === 0x02)).not.toHaveLength(0);
});

it('keeps the terminal title and disconnected status separate in a 171px card', async () => {
  await page.viewport(390, 844);
  const sockets = terminalTransport();
  mountTerminal('running', 356);
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => sockets[0].disconnect());
  await screen.findByText('Disconnected');
  const title = screen.getByText('Terminal');
  const status = screen.getByText('Disconnected');
  const card = document.querySelector<HTMLElement>('[data-nc-card-cell]')!;
  expect(card.getBoundingClientRect().width).toBeCloseTo(171, 0);
  const titleRect = title.getBoundingClientRect();
  const statusRect = status.getBoundingClientRect();
  expect(statusRect.left >= titleRect.right || statusRect.top >= titleRect.bottom).toBe(true);
  expect(statusRect.left).toBeGreaterThanOrEqual(card.getBoundingClientRect().left);
  expect(statusRect.right).toBeLessThanOrEqual(card.getBoundingClientRect().right);
  const closeRect = screen.getByRole('button', { name: 'Delete card Terminal' }).getBoundingClientRect();
  expect(statusRect.right <= closeRect.left || statusRect.top >= closeRect.bottom).toBe(true);
  await page.screenshot({ path: 'test-results/terminal-disconnected-narrow.png' });
});


it('keeps the disconnected status on the title row when the terminal is wide', async () => {
  await page.viewport(1200, 800);
  const sockets = terminalTransport();
  mountTerminal();
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => sockets[0].disconnect());
  const status = await screen.findByText('Disconnected');
  const titleRect = screen.getByText('Terminal').getBoundingClientRect();
  const statusRect = status.getBoundingClientRect();
  expect(statusRect.top).toBeCloseTo(titleRect.top, 0);
  expect(statusRect.left).toBeGreaterThanOrEqual(titleRect.right);
});
