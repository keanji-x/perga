/** Wire simulator only; real xterm, input, fit and lifecycle execute in browser tests. */
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
    send(data: Uint8Array) { this.sent.push(data); }
    close() { this.readyState = 3; }
    open() {
      this.readyState = 1; this.onopen?.();
      this.onmessage?.({ data: new Uint8Array([1, ...new TextEncoder().encode('workbench prompt')]) });
    }
    exit() {
      this.onmessage?.({ data: new Uint8Array([2, ...new TextEncoder().encode('{"type":"exit","exit_code":0}')]) });
    }
  }
  return { sockets, Socket };
}
