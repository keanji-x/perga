import { describe, expect, it } from 'vitest';

import { conversationToolShape } from './conversation-tool.js';

describe('MCP activity classification', () => {
  it.each(['', 'perga_', 'perga:', 'perga:perga_'])('recognizes track_set with prefix %s as a write', prefix => {
    expect(conversationToolShape(`${prefix}track_set`, {})).toEqual({
      running: 'Updating the track', done: 'Updated the track', target: null,
    });
    expect(conversationToolShape(`${prefix}track_status`, {}).done).toBe('Read the track');
  });
});
