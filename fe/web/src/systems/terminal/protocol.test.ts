import { describe, expect, it } from 'vitest';

import {
  decodeTerminalServerFrame,
  encodeTerminalInputFrame,
  encodeTerminalResizeFrame,
  TERMINAL_FRAME_CONTROL_JSON,
  TERMINAL_FRAME_PTY_DATA,
  terminalWsUrl,
} from './protocol.ts';

describe('Perga /api/v1 terminal WebSocket binary protocol', () => {
  it('builds /api/v1/terminals/{termId}/ws URLs across http and https', () => {
    expect(terminalWsUrl('term_01', { protocol: 'http:', host: '127.0.0.1:5199' }))
      .toBe('ws://127.0.0.1:5199/api/v1/terminals/term_01/ws');
    expect(terminalWsUrl('a/b', { protocol: 'https:', host: 'perga.local' }))
      .toBe('wss://perga.local/api/v1/terminals/a%2Fb/ws');
  });

  it('encodes PTY input frames with leading 0x01 tag and raw UTF-8 bytes', () => {
    const frame = encodeTerminalInputFrame('ls -la\r');
    expect(frame[0]).toBe(TERMINAL_FRAME_PTY_DATA);
    expect(new TextDecoder().decode(frame.subarray(1))).toBe('ls -la\r');
  });

  it('encodes resize control frames with leading 0x02 tag and JSON payload', () => {
    const frame = encodeTerminalResizeFrame(120, 40);
    expect(frame[0]).toBe(TERMINAL_FRAME_CONTROL_JSON);
    expect(JSON.parse(new TextDecoder().decode(frame.subarray(1)))).toEqual({
      type: 'resize',
      cols: 120,
      rows: 40,
    });
  });

  it('decodes 0x01 PTY output frames and 0x02 exit control frames', () => {
    const outPayload = new TextEncoder().encode('hello pty\r\n');
    const outFrame = new Uint8Array(1 + outPayload.length);
    outFrame[0] = TERMINAL_FRAME_PTY_DATA;
    outFrame.set(outPayload, 1);

    const decodedOut = decodeTerminalServerFrame(outFrame.buffer);
    expect(decodedOut.kind).toBe('output');
    if (decodedOut.kind === 'output') {
      expect(new TextDecoder().decode(decodedOut.bytes)).toBe('hello pty\r\n');
    }

    const exitJson = new TextEncoder().encode(JSON.stringify({ type: 'exit', exit_code: 0 }));
    const exitFrame = new Uint8Array(1 + exitJson.length);
    exitFrame[0] = TERMINAL_FRAME_CONTROL_JSON;
    exitFrame.set(exitJson, 1);
    expect(decodeTerminalServerFrame(exitFrame)).toEqual({ kind: 'exit', exitCode: 0 });
  });
});
