import { describe, expect, it } from 'vitest';

import { parse, sanitizeAstPolicy } from '../markdown/public.js';
import { selectMarkdownBlocksForLineRange } from './markdown-line-target.js';

function parseSafe(source: string) {
  const parsed = parse(source);
  if (parsed.status !== 'ready') throw new Error('Expected valid markdown fixture');
  return sanitizeAstPolicy(parsed.value, { rawHtml: 'drop' });
}

describe('selectMarkdownBlocksForLineRange', () => {
  const essay = [
    '# Essay Title',           // 1
    '',                        // 2
    'Intro paragraph.',        // 3
    '',                        // 4
    '1. First item.',          // 5
    '',                        // 6
    '2. Second item.',         // 7
    '',                        // 8
    '3. **Parallel worktrees** blew up target/.', // 9
    '',                        // 10
    '   ```toml',              // 11
    '   [profile.dev]',        // 12
    '   debug = 1',            // 13
    '   ```',                  // 14
    '',                        // 15
    '4. **Bad code** grew.',   // 16
    '',                        // 17
    '## Picking a backend',    // 18
    '',                        // 19
    'Closing paragraph.',      // 20
  ].join('\n');

  it('highlights the specific list item (including fenced code inside it) instead of the entire list', () => {
    const ast = parseSafe(essay);
    // Line 9 starts item 3; line 12 is inside item 3's fenced code block.
    for (const line of [9, 12]) {
      const selection = selectMarkdownBlocksForLineRange(essay, ast.children, { start: line, end: line }, ast.sourceLines.length);
      expect([...selection.blocks]).toEqual([]);
      expect([...selection.listItems.entries()].map(([k, v]) => [k, [...v]])).toEqual([[2, [2]]]);
    }
  });

  it('highlights multiple items or blocks spanned by a line range', () => {
    const ast = parseSafe(essay);
    const selection = selectMarkdownBlocksForLineRange(essay, ast.children, { start: 9, end: 18 }, ast.sourceLines.length);
    expect([...selection.blocks]).toEqual([3]);
    expect([...selection.listItems.entries()].map(([k, v]) => [k, [...v]])).toEqual([[2, [2, 3]]]);
  });

  it('lands on the nearest following block or item when the line falls on blank space', () => {
    const ast = parseSafe(essay);
    // Line 17 is blank right before heading on line 18 (block index 3).
    const beforeHeading = selectMarkdownBlocksForLineRange(essay, ast.children, { start: 17, end: 17 }, ast.sourceLines.length);
    expect([...beforeHeading.blocks]).toEqual([3]);
    expect(beforeHeading.listItems.size).toBe(0);

    // Line 6 is blank inside the loose list right before item 2 (index 1).
    const insideList = selectMarkdownBlocksForLineRange(essay, ast.children, { start: 6, end: 6 }, ast.sourceLines.length);
    expect([...insideList.blocks]).toEqual([]);
    expect([...insideList.listItems.entries()].map(([k, v]) => [k, [...v]])).toEqual([[2, [1]]]);
  });

  it('returns an empty selection for invalid or out-of-bounds line numbers', () => {
    const ast = parseSafe(essay);
    for (const range of [{ start: 0, end: 1 }, { start: 5, end: 3 }, { start: 99, end: 99 }]) {
      const selection = selectMarkdownBlocksForLineRange(essay, ast.children, range, ast.sourceLines.length);
      expect(selection.blocks.size).toBe(0);
      expect(selection.listItems.size).toBe(0);
    }
  });

  it('handles CR-only and CRLF line endings identically to the Markdown parser', () => {
    for (const eol of ['\r', '\r\n']) {
      const source = `first${eol}second${eol}${eol}# Next`;
      const ast = parseSafe(source);
      const line2 = selectMarkdownBlocksForLineRange(source, ast.children, { start: 2, end: 2 });
      expect([...line2.blocks]).toEqual([0]);
      const line4 = selectMarkdownBlocksForLineRange(source, ast.children, { start: 4, end: 4 });
      expect([...line4.blocks]).toEqual([1]);
    }
  });
});
