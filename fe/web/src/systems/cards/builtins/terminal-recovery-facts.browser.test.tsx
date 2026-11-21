import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { page } from 'vitest/browser';
import { afterEach, beforeAll, expect, it, vi } from 'vitest';
import '../../../styles/entry.css';
import { RecoveryAccess } from '../../../../../core/domain/recovery/access.ts';
import { createCardHost } from '../host.ts';
import { createCardRegistry } from '../registry.ts';
import { BoardHost } from '../ui/board-host.tsx';
import { registerAvailableBuiltinCards } from './register.ts';
import { XtermView, type XtermViewHandle } from '../../terminal/xterm-view.tsx';
import { createRef } from 'react';

beforeAll(async () => { await import('../../terminal/xterm-view.tsx'); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('retains binary exit truth after a recovery pause', async () => {
  vi.stubGlobal('__NC_BUNDLED__', true);
  await page.viewport(390, 844);
  const sockets: Socket[] = [];
  class Socket {
    static OPEN = 1;
    static CONNECTING = 0;
    readyState = 0;
    onopen: (() => void) | null = null;
    onmessage: ((event: { data: Uint8Array }) => void) | null = null;
    onclose: (() => void) | null = null;
    onerror: (() => void) | null = null;
    constructor() { sockets.push(this); }
    send() {}
    close() { this.readyState = 3; }
    message(data: Uint8Array) { this.onmessage?.({ data }); }
  }
  vi.stubGlobal('WebSocket', Socket);
  const access = new RecoveryAccess(); access.change('connected');
  const registry = createCardRegistry(); registerAvailableBuiltinCards(registry);
  const card = registry.resolve({ id: 'card-1', kind: 'terminal', payload: {},
    runtime: { worker_session_id: 'run-1', kind: 'terminal', status: 'running', terminal_id: 'pty-1' } });
  if (!card) throw new Error('Fixture terminal missing');
  render(<div style={{ width: 390 }}><BoardHost host={createCardHost(registry, { recovery: access })}
    items={[{ card, title: 'Terminal', originalIndex: 0, deletable: true, activity: null, notice: null }]}
    visible activeCardId="card-1" onRemoveCard={() => {}} /></div>);
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => {
    sockets[0].readyState = 1; sockets[0].onopen?.();
    sockets[0].message(exitFrame(0));
  });
  await screen.findByText('Session exited.');
  act(() => access.invalidate('recovering'));
  act(() => access.change('connected'));
  expect(sockets).toHaveLength(1);
  expect(screen.queryByText('Session exited.')).not.toBeNull();
});

it('retains terminal exit details through repeated revocation until a deliberate attach', async () => {
  vi.stubGlobal('__NC_BUNDLED__', true);
  await page.viewport(800, 600);
  const sockets: Socket[] = [];
  class Socket {
    static OPEN = 1; static CONNECTING = 0; readyState = 0;
    onopen: (() => void) | null = null;
    onmessage: ((event: { data: Uint8Array }) => void) | null = null;
    onclose: (() => void) | null = null; onerror: (() => void) | null = null;
    constructor() { sockets.push(this); }
    send() {} close() { this.readyState = 3; }
  }
  vi.stubGlobal('WebSocket', Socket);
  const access = new RecoveryAccess(); access.change('connected');
  const exit = vi.fn(); const ref = createRef<XtermViewHandle>();
  render(<div style={{ width: 800, height: 400 }}><XtermView ref={ref} terminalId="pty" recovery={access} onExitChange={exit} /></div>);
  await waitFor(() => expect(sockets).toHaveLength(1));
  act(() => {
    sockets[0].readyState = 1; sockets[0].onopen?.();
    sockets[0].onmessage?.({ data: exitFrame(137) });
  });
  expect(exit).toHaveBeenLastCalledWith({ exit_code: 137, signal_killed: false });
  const calls = exit.mock.calls.length;
  act(() => { access.invalidate('paused'); access.invalidate('recovering'); access.change('connected'); });
  expect(exit).toHaveBeenCalledTimes(calls); expect(sockets).toHaveLength(1);
  act(() => ref.current?.refresh());
  await waitFor(() => expect(sockets).toHaveLength(2));
  expect(exit).toHaveBeenLastCalledWith(null);
});

function exitFrame(code: number): Uint8Array {
  return new Uint8Array([2, ...new TextEncoder().encode(JSON.stringify({ type: 'exit', exit_code: code }))]);
}
