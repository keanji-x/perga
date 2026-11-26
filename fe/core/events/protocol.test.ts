import { describe, expect, it } from 'vitest';

import { decodeEventFrame, eventSubscriptionFrame } from './protocol.js';

describe('event protocol behavior', () => {
  it('reports non-object inputs through the failed decode branch', () => {
    expect(decodeEventFrame('not-an-envelope')).toEqual({
      status: 'failed',
      error: {
        kind: 'decode',
        message: 'Event frame must be an object',
        cause: 'not-an-envelope',
      },
    });
  });

  it('always publishes a since cursor and uses zero for cold starts', () => {
    expect(eventSubscriptionFrame(['*'], null)).toEqual({ sub: ['*'], since: 0 });
    expect(eventSubscriptionFrame(['track:w1'], 42)).toEqual({ sub: ['track:w1'], since: 42 });
  });
});

it.each(['/tmp/log.txt', 'draft:notes.txt', 'draft\\notes.txt'])('decodes a transient file-open request for %s', path => {
  const payload = { trackId: 'track', sessionId: 'planner', path, line: 42, endLine: 44 };
  const event = { kind: 'workspace.file_open_requested', trackId: 'track', eventVersion: 2, eventId: null, payload };
  expect(decodeEventFrame(event)).toEqual({
    status: 'ready', frame: {
      type: 'event', event: { ev: event.kind, data: payload }, meta: { id: 0, eventVersion: 2 },
    },
  });
  for (const invalid of [
    { ...event, eventId: 7 },
    { ...event, trackId: 'elsewhere' },
    { ...event, payload: { ...payload, line: null } },
    { ...event, payload: { ...payload, path: 'bad\0path' } },
  ]) {
    expect(decodeEventFrame(invalid)).toMatchObject({ status: 'ready', frame: { type: 'malformed-event' } });
  }
});
