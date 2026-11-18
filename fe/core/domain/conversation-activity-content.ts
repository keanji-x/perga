/** Readable provider content stays in the domain; the browser renders it as plain code text. */
const MAX_CONTENT_LENGTH = 32_768;

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown> : null;
}

function textBlocks(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((part: unknown) => {
    if (typeof part === 'string') return part.trim() ? [part] : [];
    const block = record(part);
    return typeof block?.text === 'string' && block.text.trim() ? [block.text] : [];
  });
}

function printable(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  return typeof value === 'string' ? value : JSON.stringify(value, null, 2);
}

/** Missing content means the activity has no readable body; it never invents a tool result. */
export function activityContent(itemType: string, item: Record<string, unknown>): { content?: string } {
  let text: string;
  if (itemType === 'reasoning') {
    text = [...textBlocks(item.summary), ...textBlocks(item.content)].join('\n\n');
  } else if (itemType === 'dynamicToolCall' || itemType === 'mcpToolCall') {
    const sections: string[] = [];
    const argsObj = record(item.arguments);
    const hasArgs = argsObj !== null ? Object.keys(argsObj).length > 0 : item.arguments !== null && item.arguments !== undefined && item.arguments !== '';
    const input = hasArgs ? printable(item.arguments) : null;
    if (input) sections.push(`Input\n${input}`);
    const result = record(item.result);
    const output = result === null ? printable(item.result) : [
      ...textBlocks(result.content), printable(result.structuredContent ?? (Object.hasOwn(result, 'content') ? undefined : result)),
    ].filter(Boolean).join('\n\n');
    if (output) sections.push(`Output\n${output}`);
    if (itemType === 'mcpToolCall' && item.error !== undefined && item.error !== null) {
      const errorText = printable(item.error);
      if (errorText) sections.push(`Error\n${errorText}`);
    }
    text = sections.join('\n\n');
  } else {
    return {};
  }
  if (!text.trim()) return {};
  return { content: text.length > MAX_CONTENT_LENGTH
    ? `${text.slice(0, MAX_CONTENT_LENGTH)}\n… (truncated)` : text };
}
