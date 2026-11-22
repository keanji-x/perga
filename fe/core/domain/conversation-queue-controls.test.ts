import { describe,expect,it } from 'vitest';
import { pendingQueueOwnsHumanEntry,pendingQueueWithCurrentHumanVersions,sessionSteeringOutcome } from './conversation-queue-controls.js';

describe('canonical queued message controls', () => {
  it('reconciles only listed own Human identities and preserves queue position', () => {
    const rows=[{entry_id:'message',rev:1,text:'old',queued_at_ms:123}];
    const source={id:'human:message',author:'you' as const,text:'new',atMs:9,
      human:{messageId:'message',revision:2,userId:'me',displayName:'Me'}};
    expect(pendingQueueWithCurrentHumanVersions(rows,[{...source,author:'human'}])).toBe(rows);
    const current=pendingQueueWithCurrentHumanVersions(rows,[source,{...source,id:'human:other',human:{...source.human,messageId:'other'}}]);
    expect(current).toEqual([{...rows[0],rev:2,text:'new'}]);
    const versions=new Map([['message',2]]);
    expect(pendingQueueOwnsHumanEntry({...source,human:{...source.human,revision:1}},versions)).toBe(true);
    expect(pendingQueueOwnsHumanEntry(source,versions)).toBe(true);
    expect(pendingQueueOwnsHumanEntry({...source,human:{...source.human,revision:3}},versions)).toBe(false);
  });
});

it.each([
  ['delivered', 'done'], ['queued', 'not_running'], ['rejected', 'not_running'], ['unknown', 'unanswered'],
])('presents a unified %s steering receipt as %s', (delivery, kind) => {
  expect(sessionSteeringOutcome(delivery)).toEqual({ kind });
});
