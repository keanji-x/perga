import { describe, expect, it } from 'vitest';

import {
  TASK_LS_TOOL, REPORT_DELETE_TOOL, REPORT_READ_TOOLS, REPORT_TOOL_PREFIX,
  REPORT_WRITE_TOOLS, TRACK_TOOL_PREFIX, USER_ASK_TOOL,
} from '../keys/mcp-tools.js';
import {
  REPORT_EDIT_AUTHORS, foldQuietSyncs, isAskTurn, isReportWriteTool, reportEditAuthor,
  type QuietSyncGroup, type ReportEditAuthor, type TranscriptBlock,
} from './conversation-quiet-sync.js';
import {
  SYSTEM_PRESENTATION_LABELS,
  type ConversationActivity, type ConversationSystemEntry, type ConversationTurn,
  type ConversationTurnOutcome, type TranscriptEntry,
} from './conversation.js';


const NOW = 1_760_000_000_000;

const DIFF_TEXT = 'The track report was edited (author = "user").\n'
  + 'Block-level diff follows; this is information, not an instruction to re-read.\n'
  + 'Blocks: 0 added, 0 removed, 1 modified (2 unchanged).\n';

/** A report-edit wake that was the whole of its batch, marked `quiet`. */
function reportEdited(id: string, text = DIFF_TEXT): ConversationSystemEntry {
  return {
    id, author: 'system', label: SYSTEM_PRESENTATION_LABELS.system_report_edited, text, atMs: NOW,
    quiet: true,
  };
}

function otherSystem(id: string): ConversationSystemEntry {
  return {
    id, author: 'system', label: SYSTEM_PRESENTATION_LABELS.system_task_completed,
    text: 'Task completed.', atMs: NOW,
  };
}

function you(id: string, text = 'hello'): ConversationTurn {
  return { id, author: 'you', text, atMs: NOW };
}

function agent(id: string, text = 'I re-read the report.'): ConversationTurn {
  return { id, author: 'agent', text, atMs: NOW };
}

function ask(id: string, text = 'You changed the block I was writing. Keep yours or mine?'): ConversationTurn {
  return { id, author: 'agent', text, atMs: NOW, origin: 'ask' };
}

function activity(id: string, overrides: Partial<ConversationActivity> = {}): ConversationActivity {
  return {
    id, author: 'activity', verb: 'Read report', target: null, state: 'done',
    durationMs: null, detail: null, tool: REPORT_READ_TOOLS[0] ?? null, atMs: NOW, ...overrides,
  };
}

/** A completed `neige_report_commit` row. */
function wrote(id: string, overrides: Partial<ConversationActivity> = {}): ConversationActivity {
  return activity(id, { verb: 'Wrote report', tool: REPORT_WRITE_TOOLS[2] ?? null, ...overrides });
}

function outcome(id: string, status: ConversationTurnOutcome['status'] = 'completed'): ConversationTurnOutcome {
  return { id, author: 'turn', elapsedMs: null, turnId: 'turn-1', status, atMs: NOW };
}

function ids(block: TranscriptBlock): string | readonly string[] {
  return block.kind === 'entry' ? block.entry.id : block.entries.map((entry) => entry.id);
}

function group(block: TranscriptBlock | undefined): QuietSyncGroup {
  if (block?.kind !== 'quiet-sync') throw new Error(`expected a quiet-sync group, got ${JSON.stringify(block)}`);
  return block;
}

describe('foldQuietSyncs', () => {
  it('folds a report-edit wake with the activity and replies that followed it', () => {
    const blocks = foldQuietSyncs([
      you('u1'), agent('a1'),
      reportEdited('s1'), activity('act1'), agent('a2'), outcome('o1'),
      you('u2'),
    ]);
    expect(blocks.map(ids)).toEqual([
      'u1', 'a1', ['s1', 'act1', 'a2', 'o1'], 'u2',
    ]);
    const sync = group(blocks[2]);
    expect(sync.id).toBe('s1');
    expect(sync.author).toBe('user');
    expect(sync.atMs).toBe(NOW);
    expect(sync.entries[0]?.author).toBe('system');
  });

  it('does not fold a turn the user opened, and closes a fold at the user turn', () => {
    const blocks = foldQuietSyncs([
      reportEdited('s1'), activity('act1'),
      you('u1'), activity('act2'), agent('a1'),
    ]);
    expect(blocks.map(ids)).toEqual([['s1', 'act1'], 'u1', 'act2', 'a1']);
    expect(blocks.slice(1).every((block) => block.kind === 'entry')).toBe(true);
  });

  it('lifts an ask turn out of the fold and keeps the rest folded', () => {
    const blocks = foldQuietSyncs([
      reportEdited('s1'), activity('act1'), ask('n1'), activity('act2'), agent('a1'),
      you('u1'),
    ]);
    expect(blocks.map(ids)).toEqual([['s1', 'act1', 'act2', 'a1'], 'n1', 'u1']);
    const lifted = blocks[1];
    expect(lifted?.kind).toBe('entry');
    expect(lifted?.kind === 'entry' && isAskTurn(lifted.entry)).toBe(true);
    expect(lifted?.kind === 'entry' && lifted.entry.author).toBe('agent');
  });

  it('keeps an ordinary agent reply inside the fold — only an ask is lifted', () => {
    const blocks = foldQuietSyncs([reportEdited('s1'), agent('a1')]);
    expect(blocks.map(ids)).toEqual([['s1', 'a1']]);
  });

  it('folds consecutive report-edit wakes separately and stops at any system entry', () => {
    const blocks = foldQuietSyncs([
      reportEdited('s1'), activity('act1'),
      reportEdited('s2'), agent('a1'),
      otherSystem('s3'), agent('a2'),
    ]);
    expect(blocks.map(ids)).toEqual([['s1', 'act1'], ['s2', 'a1'], 's3', 'a2']);
  });

  it('leaves a transcript without report-edit wakes untouched', () => {
    const entries: readonly TranscriptEntry[] = [you('u1'), activity('act1'), agent('a1'), otherSystem('s1')];
    const blocks = foldQuietSyncs(entries);
    expect(blocks.map(ids)).toEqual(['u1', 'act1', 'a1', 's1']);
    expect(blocks.map((block) => (block.kind === 'entry' ? block.entry : null))).toEqual(entries);
  });

  it('keeps an ask outside any sync as an ordinary entry', () => {
    const blocks = foldQuietSyncs([you('u1'), ask('n1')]);
    expect(blocks.map(ids)).toEqual(['u1', 'n1']);
  });

  it('lifts a failed or interrupted outcome out of the fold, after it', () => {
    const failed = foldQuietSyncs([reportEdited('s1'), activity('act1'), outcome('o1', 'failed'), you('u1')]);
    expect(failed.map(ids)).toEqual([['s1', 'act1'], 'o1', 'u1']);
    const stopped = foldQuietSyncs([reportEdited('s1'), agent('a1'), outcome('o1', 'interrupted')]);
    expect(stopped.map(ids)).toEqual([['s1', 'a1'], 'o1']);
    const fine = foldQuietSyncs([reportEdited('s1'), agent('a1'), outcome('o1', 'completed')]);
    expect(fine.map(ids)).toEqual([['s1', 'a1', 'o1']]);
  });

  it('does not fold a report-edit entry that is not marked quiet', () => {
    const unmarked: ConversationSystemEntry = {
      id: 's1', author: 'system', label: SYSTEM_PRESENTATION_LABELS.system_report_edited, text: DIFF_TEXT, atMs: NOW,
    };
    const blocks = foldQuietSyncs([unmarked, activity('act1'), agent('a1')]);
    expect(blocks.map(ids)).toEqual(['s1', 'act1', 'a1']);
    expect(blocks.every((block) => block.kind === 'entry')).toBe(true);
  });
});

describe('foldQuietSyncs outcome', () => {
  it('is accepted when the turn completed with reads only and nothing said', () => {
    const blocks = foldQuietSyncs([reportEdited('s1'), activity('act1'), agent('a1'), outcome('o1')]);
    expect(group(blocks[0]).outcome).toBe('accepted');
    expect(group(foldQuietSyncs([reportEdited('s1'), outcome('o1')])[0]).outcome).toBe('accepted');
  });

  it('is updated when a report write landed in the turn', () => {
    const blocks = foldQuietSyncs([reportEdited('s1'), activity('act1'), wrote('w1'), outcome('o1')]);
    expect(group(blocks[0]).outcome).toBe('updated');
    for (const tool of [...REPORT_WRITE_TOOLS, REPORT_DELETE_TOOL]) {
      const one = foldQuietSyncs([reportEdited('s1'), wrote('w1', { tool }), outcome('o1')]);
      expect(group(one[0]).outcome, tool).toBe('updated');
    }
    const spoken = foldQuietSyncs([reportEdited('s1'), wrote('w1'), ask('n1'), outcome('o1')]);
    expect(group(spoken[0]).outcome).toBe('updated');
  });

  it('is null while the turn has not completed, after a bad ending, or when the planner spoke', () => {
    expect(group(foldQuietSyncs([reportEdited('s1'), activity('act1')])[0]).outcome).toBeNull();
    expect(group(foldQuietSyncs([reportEdited('s1'), wrote('w1')])[0]).outcome).toBeNull();
    expect(group(foldQuietSyncs([reportEdited('s1'), wrote('w1'), outcome('o1', 'failed')])[0]).outcome).toBeNull();
    expect(group(foldQuietSyncs([reportEdited('s1'), outcome('o1', 'interrupted')])[0]).outcome).toBeNull();
    expect(group(foldQuietSyncs([reportEdited('s1'), ask('n1'), outcome('o1')])[0]).outcome).toBeNull();
  });

  it('does not count a refused or still-running write, nor a read, nor a non-report tool', () => {
    const refused = foldQuietSyncs([reportEdited('s1'), wrote('w1', { state: 'failed' }), outcome('o1')]);
    expect(group(refused[0]).outcome).toBe('accepted');
    const running = foldQuietSyncs([reportEdited('s1'), wrote('w1', { state: 'running' }), outcome('o1')]);
    expect(group(running[0]).outcome).toBe('accepted');
    const reads = foldQuietSyncs([
      reportEdited('s1'), ...REPORT_READ_TOOLS.map((tool, index) => activity(`r${index}`, { tool })), outcome('o1'),
    ]);
    expect(group(reads[0]).outcome).toBe('accepted');
    const other = foldQuietSyncs([reportEdited('s1'), activity('t1', { tool: TASK_LS_TOOL }), outcome('o1')]);
    expect(group(other[0]).outcome).toBe('accepted');
    const shell = foldQuietSyncs([reportEdited('s1'), activity('sh', { verb: 'Ran', tool: null }), outcome('o1')]);
    expect(group(shell[0]).outcome).toBe('accepted');
  });

  it('treats any unlisted report tool as a write', () => {
    expect(isReportWriteTool(`${REPORT_TOOL_PREFIX}blocks.something_new`)).toBe(true);
    for (const tool of REPORT_READ_TOOLS) expect(isReportWriteTool(tool), tool).toBe(false);
    for (const tool of REPORT_WRITE_TOOLS) expect(isReportWriteTool(tool), tool).toBe(true);
    expect(isReportWriteTool(USER_ASK_TOOL)).toBe(false);
    expect(isReportWriteTool(`${TRACK_TOOL_PREFIX}cat`)).toBe(false);
    expect(isReportWriteTool(`${REPORT_TOOL_PREFIX.slice(0, -1)}ing.x`)).toBe(false);
  });

});

describe('reportEditAuthor', () => {
  it('reads the author the kernel named on the first line', () => {
    expect(reportEditAuthor(DIFF_TEXT)).toBe('user');
    expect(reportEditAuthor('The track report was edited (author = "plugin"). Re-read the track status.')).toBe('plugin');
    expect(reportEditAuthor('The track report was edited (author = "assistant").\nmore')).toBe('assistant');
  });

  it('answers null for the pre-#1252 sentence and for an unknown spelling', () => {
    expect(reportEditAuthor('The user edited the track report. Re-read the track status.')).toBeNull();
    expect(reportEditAuthor('The track report was edited (author = "kernel").')).toBeNull();
    expect(reportEditAuthor('')).toBeNull();
    expect(reportEditAuthor('The user edited the track report.\n+(author = "plugin")')).toBeNull();
  });

  it('accepts exactly the listed authors', () => {
    const parsed = new Set(REPORT_EDIT_AUTHORS.map((author: ReportEditAuthor) =>
      reportEditAuthor(`The track report was edited (author = "${author}").`)));
    expect([...parsed].sort()).toEqual([...REPORT_EDIT_AUTHORS].sort());
    expect(Object.isFrozen(REPORT_EDIT_AUTHORS)).toBe(true);
  });
});
