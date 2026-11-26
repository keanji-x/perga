import type { MarkdownPosition, SafeBlock } from '../markdown/public.js';

export type LineRange = Readonly<{ start: number; end: number }>;

export type MarkdownLineSelection = Readonly<{
  /** Top-level block indices to highlight (for non-list blocks, or whole list if empty). */
  blocks: ReadonlySet<number>;
  /** List block index -> item indices to highlight, avoiding lighting up an entire long list. */
  listItems: ReadonlyMap<number, ReadonlySet<number>>;
}>;

function emptySelection(): MarkdownLineSelection {
  return {
    blocks: new Set<number>(),
    listItems: new Map<number, ReadonlySet<number>>(),
  };
}

function nodeEndLine(source: string, position: MarkdownPosition): number {
  let line = position.start.line;
  const end = Math.min(position.end.offset, source.length);
  for (let i = position.start.offset; i < end; i += 1) {
    const code = source.charCodeAt(i);
    if (code === 10) {
      if (i + 1 < end) line += 1;
    } else if (code === 13) {
      const nextIsLf = i + 1 < end && source.charCodeAt(i + 1) === 10;
      const after = nextIsLf ? i + 2 : i + 1;
      if (after < end) line += 1;
      if (nextIsLf) i += 1;
    }
  }
  return line;
}

function addListItem(map: Map<number, Set<number>>, blockIndex: number, itemIndex: number) {
  let set = map.get(blockIndex);
  if (set === undefined) {
    set = new Set<number>();
    map.set(blockIndex, set);
  }
  set.add(itemIndex);
}

/**
 * Map a 1-based source line range (`:line` or `:start-end`) onto the rendered top-level
 * Markdown blocks (or specific list items inside a list) so rendered Markdown can scroll
 * to and highlight the cited passage without falling back to raw source code.
 */
export function selectMarkdownBlocksForLineRange(
  source: string,
  blocks: readonly SafeBlock[],
  range: LineRange,
  totalLines?: number,
): MarkdownLineSelection {
  if (
    blocks.length === 0
    || !Number.isSafeInteger(range.start)
    || !Number.isSafeInteger(range.end)
    || range.start < 1
    || range.end < range.start
  ) {
    return emptySelection();
  }
  const lineCount = totalLines ?? source.split(/\r\n?|\n/).length;
  if (range.start > lineCount) return emptySelection();

  const selectedBlocks = new Set<number>();
  const selectedListItems = new Map<number, Set<number>>();

  for (let blockIndex = 0; blockIndex < blocks.length; blockIndex += 1) {
    const block = blocks[blockIndex];
    const startLine = block.position.start.line;
    const endLine = nodeEndLine(source, block.position);
    if (startLine > range.end || endLine < range.start) continue;

    if (block.type === 'list' && block.children.length > 0) {
      let matchedItem = false;
      for (let itemIndex = 0; itemIndex < block.children.length; itemIndex += 1) {
        const item = block.children[itemIndex];
        const itemStart = item.position.start.line;
        const itemEnd = nodeEndLine(source, item.position);
        if (itemStart <= range.end && itemEnd >= range.start) {
          addListItem(selectedListItems, blockIndex, itemIndex);
          matchedItem = true;
        }
      }
      if (!matchedItem) {
        // Blank line inside a loose list: land on the next item (or the last item).
        const nextIndex = block.children.findIndex(item => item.position.start.line > range.start);
        addListItem(selectedListItems, blockIndex, nextIndex >= 0 ? nextIndex : block.children.length - 1);
      }
    } else {
      selectedBlocks.add(blockIndex);
    }
  }

  if (selectedBlocks.size > 0 || selectedListItems.size > 0) {
    return { blocks: selectedBlocks, listItems: selectedListItems };
  }

  // Cited line sits on blank space between blocks: land on the nearest following block (or the last block).
  const nextBlockIndex = blocks.findIndex(block => block.position.start.line > range.start);
  const fallbackIndex = nextBlockIndex >= 0 ? nextBlockIndex : blocks.length - 1;
  const fallbackBlock = blocks[fallbackIndex];
  if (fallbackBlock.type === 'list' && fallbackBlock.children.length > 0) {
    const itemIndex = nextBlockIndex >= 0 ? 0 : fallbackBlock.children.length - 1;
    addListItem(selectedListItems, fallbackIndex, itemIndex);
  } else {
    selectedBlocks.add(fallbackIndex);
  }
  return { blocks: selectedBlocks, listItems: selectedListItems };
}
