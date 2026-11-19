import { encodeTerminalInputFrame, TERMINAL_FRAME_CONTROL_JSON } from '../../systems/terminal/protocol.ts';

/** Binary v1 wire simulator; real xterm, input, fit and lifecycle execute in browser tests. */
export function workbenchSockets() {
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
    open() {
      this.readyState = 1; this.onopen?.();
      this.onmessage?.({ data: encodeTerminalInputFrame('workbench prompt') });
    }
    exit(code: number) {
      const payload = new TextEncoder().encode(JSON.stringify({ type: 'exit', exit_code: code }));
      this.onmessage?.({ data: Uint8Array.of(TERMINAL_FRAME_CONTROL_JSON, ...payload) });
    }
  }
  return { sockets, Socket };
}
