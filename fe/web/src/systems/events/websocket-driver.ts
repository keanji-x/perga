import { decodeEventFrame, eventSubscriptionFrame } from '../../../../core/events/protocol.ts';
import type { SyncCursorPort } from './cursor-port.ts';
import type { EventStreamConfiguration, EventStreamDriver, EventStreamSink } from './event-stream.ts';

export interface SocketPort {
  onopen: ((event: Event) => void) | null;
  onmessage: ((event: MessageEvent) => void) | null;
  onclose: ((event: CloseEvent) => void) | null;
  onerror: ((event: Event) => void) | null;
  send(data: string): void;
  close(): void;
}

export type UnauthorizedProbe = () => Promise<unknown>;
export type SocketFactory = (url: string) => SocketPort;

function isUnauthorized(error: unknown): boolean {
  if (typeof error !== 'object' || error === null || !('failure' in error)) return false;
  const failure = error.failure;
  return typeof failure === 'object' && failure !== null && 'kind' in failure && failure.kind === 'unauthorized';
}

export function wsUrl(
  location: Pick<Location, 'protocol' | 'host'> = window.location,
  options?: Readonly<{ since?: number | null; trackId?: string | null }>,
): string {
  const base = `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/events`;
  if (options === undefined) return base;
  const params = new URLSearchParams();
  if (options.since !== undefined && options.since !== null) params.set('since', String(options.since));
  if (options.trackId !== undefined && options.trackId !== null && options.trackId !== '') {
    params.set('trackId', options.trackId);
  }
  const query = params.toString();
  return query === '' ? base : `${base}?${query}`;
}

function buildPergaEventsConnectUrl(baseUrl: string, since: number | null, trackId?: string | null): string {
  if (!baseUrl.includes('/api/v1/events')) return baseUrl;
  const parsed = new URL(baseUrl);
  if (!parsed.searchParams.has('since')) parsed.searchParams.set('since', String(since ?? 0));
  if (trackId !== undefined && trackId !== null && trackId !== '' && !parsed.searchParams.has('trackId')) {
    parsed.searchParams.set('trackId', trackId);
  }
  return parsed.toString();
}

export class WebSocketDriver implements EventStreamDriver {
  private readonly cursor: Pick<SyncCursorPort, 'read'>;
  private readonly probeUnauthorized: UnauthorizedProbe;
  private readonly onUnauthorized: () => void;
  private readonly socketFactory: SocketFactory;
  private epoch = 0;
  private closed = true;
  private socket: SocketPort | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private retryDelay = 500;
  private handshakeTimer: ReturnType<typeof setTimeout> | null = null;
  private probeInFlight = false;
  private unauthorized = false;
  private configuration: EventStreamConfiguration | null = null;
  private url = '';
  private sink: EventStreamSink | null = null;

  constructor(options: Readonly<{
    cursor: Pick<SyncCursorPort, 'read'>;
    probeUnauthorized: UnauthorizedProbe;
    onUnauthorized: () => void;
    socketFactory?: SocketFactory;
  }>) {
    this.cursor = options.cursor;
    this.probeUnauthorized = options.probeUnauthorized;
    this.onUnauthorized = options.onUnauthorized;
    this.socketFactory = options.socketFactory ?? ((url) => new WebSocket(url));
  }

  start(configuration: EventStreamConfiguration, url: string, sink: EventStreamSink): void {
    const epoch = ++this.epoch;
    this.closed = false;
    this.probeInFlight = false;
    this.configuration = configuration;
    this.url = url;
    this.sink = sink;
    sink.connectionState('connecting');
    if (epoch !== this.epoch || this.closed) return;
    this.connect(epoch);
  }

  stop(): void {
    ++this.epoch;
    this.closed = true;
    this.probeInFlight = false;
    if (this.retryTimer !== null) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    if (this.handshakeTimer !== null) clearTimeout(this.handshakeTimer);
    this.handshakeTimer = null;
    const socket = this.socket;
    this.socket = null;
    if (socket !== null) {
      socket.onopen = null;
      socket.onmessage = null;
      socket.onclose = null;
      socket.onerror = null;
      socket.close();
    }
  }

  private connect(epoch: number): void {
    if (this.closed || epoch !== this.epoch) return;
    const configuration = this.configuration;
    const sink = this.sink;
    if (configuration === null || sink === null) return;
    const isPergaV1 = this.url.includes('/api/v1/events');
    const targetUrl = isPergaV1
      ? buildPergaEventsConnectUrl(this.url, this.cursor.read(), configuration.trackId)
      : this.url;
    let socket: SocketPort;
    try { socket = this.socketFactory(targetUrl); }
    catch { this.scheduleRetry(epoch); return; }
    if (this.closed || epoch !== this.epoch) { socket.close(); return; }
    this.socket = socket;
    if (__NC_BUNDLED__) this.handshakeTimer = setTimeout(() => {
      if (!this.closed && epoch === this.epoch && this.socket === socket) socket.close();
    }, 15_000);
    let opened = false;
    socket.onopen = () => {
      if (this.closed || epoch !== this.epoch) { socket.close(); return; }
      opened = true;
      // The v1 Host verifies the session before accepting the upgrade.
      if (isPergaV1) this.unauthorized = false;
      if (!__NC_BUNDLED__) this.retryDelay = 500;
      if (!isPergaV1) {
        socket.send(JSON.stringify(eventSubscriptionFrame(configuration.topics, this.cursor.read())));
      }
    };
    socket.onmessage = (event) => {
      if (this.closed || epoch !== this.epoch) return;
      let input: unknown;
      try { input = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; }
      catch { input = event.data; }
      const decoded = decodeEventFrame(input);
      if (decoded.status !== 'ready') return;
      if (decoded.frame.type === 'replay-complete') {
        this.retryDelay = 500;
        if (this.handshakeTimer !== null) clearTimeout(this.handshakeTimer);
        this.handshakeTimer = null;
        sink.connectionState('connected');
        if (this.closed || epoch !== this.epoch) return;
      }
      sink.frame(decoded.frame);
    };
    socket.onclose = (event) => {
      if (this.closed || epoch !== this.epoch) return;
      if (this.handshakeTimer !== null) clearTimeout(this.handshakeTimer);
      this.handshakeTimer = null;
      if (this.socket === socket) this.socket = null;
      if (!opened || event.code === 1008 || event.code === 1006) this.probe(epoch);
      this.scheduleRetry(epoch);
      sink.connectionState('connecting');
    };
  }

  private scheduleRetry(epoch: number): void {
    if (this.closed || epoch !== this.epoch || this.retryTimer !== null) return;
    const delay = __NC_BUNDLED__ ? this.retryDelay * (0.75 + Math.random() * 0.5) : this.retryDelay;
    this.retryDelay = Math.min(this.retryDelay * 2, 8000);
    this.retryTimer = setTimeout(() => {
      if (this.closed || epoch !== this.epoch) return;
      this.retryTimer = null;
      this.connect(epoch);
    }, delay);
  }

  private probe(epoch: number): void {
    if (this.probeInFlight) return;
    this.probeInFlight = true;
    void this.probeUnauthorized().then(() => {
      if (this.closed || epoch !== this.epoch) return;
      this.unauthorized = false;
    }).catch((error: unknown) => {
      if (this.closed || epoch !== this.epoch) return;
      if (isUnauthorized(error) && !this.unauthorized) {
        this.unauthorized = true;
        this.onUnauthorized();
      }
    }).finally(() => {
      if (this.closed || epoch !== this.epoch) return;
      this.probeInFlight = false;
    });
  }
}
