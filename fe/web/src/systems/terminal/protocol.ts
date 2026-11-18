// Perga /api/v1/terminals/{termId}/ws binary framing codec:
// - 0x01 + raw PTY bytes (bidirectional: client stdin -> PTY, server replay & live output -> xterm)
// - 0x02 + UTF-8 JSON control frame:
//   - Client -> Server: {"type":"resize","cols":120,"rows":40}
//   - Server -> Client: {"type":"exit","exit_code":0}

export const TERMINAL_FRAME_PTY_DATA = 0x01;
export const TERMINAL_FRAME_CONTROL_JSON = 0x02;

export type TerminalServerFrame =
  | Readonly<{ kind: 'output'; bytes: Uint8Array }>
  | Readonly<{ kind: 'exit'; exitCode: number | null }>
  | Readonly<{ kind: 'ignore' }>;

export function terminalWsUrl(
  terminalId: string,
  location: Pick<Location, 'protocol' | 'host'> = window.location,
  shareLinkId?: string | null,
): string {
  const base = `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/v1/terminals/${encodeURIComponent(terminalId)}/ws`;
  return shareLinkId === undefined || shareLinkId === null || shareLinkId === ''
    ? base
    : `${base}?share_link=${encodeURIComponent(shareLinkId)}`;
}

export function encodeTerminalInputFrame(data: string | Uint8Array): Uint8Array {
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : data;
  const frame = new Uint8Array(1 + bytes.length);
  frame[0] = TERMINAL_FRAME_PTY_DATA;
  frame.set(bytes, 1);
  return frame;
}

export function encodeTerminalResizeFrame(cols: number, rows: number): Uint8Array {
  const json = JSON.stringify({ type: 'resize', cols, rows });
  const bytes = new TextEncoder().encode(json);
  const frame = new Uint8Array(1 + bytes.length);
  frame[0] = TERMINAL_FRAME_CONTROL_JSON;
  frame.set(bytes, 1);
  return frame;
}

export function decodeTerminalServerFrame(raw: ArrayBuffer | Uint8Array): TerminalServerFrame {
  const bytes = raw instanceof Uint8Array ? raw : new Uint8Array(raw);
  if (bytes.length === 0) return { kind: 'ignore' };
  const tag = bytes[0];
  if (tag === TERMINAL_FRAME_PTY_DATA) {
    return { kind: 'output', bytes: bytes.subarray(1) };
  }
  if (tag === TERMINAL_FRAME_CONTROL_JSON) {
    try {
      const parsed = JSON.parse(new TextDecoder().decode(bytes.subarray(1))) as {
        type?: unknown;
        exit_code?: unknown;
        exitCode?: unknown;
      };
      if (parsed !== null && typeof parsed === 'object' && parsed.type === 'exit') {
        const rawCode = parsed.exit_code !== undefined ? parsed.exit_code : parsed.exitCode;
        return {
          kind: 'exit',
          exitCode: typeof rawCode === 'number' && Number.isFinite(rawCode) ? rawCode : null,
        };
      }
    } catch {
      return { kind: 'ignore' };
    }
  }
  return { kind: 'ignore' };
}
