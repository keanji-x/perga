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

/** Bound readable content while preserving its original text. */
export function thinkingActivityContent(text: string): { content?: string } {
  if (!text.trim()) return {};
  return { content: text.length > MAX_CONTENT_LENGTH ? `${text.slice(0, MAX_CONTENT_LENGTH)}\n… (truncated)` : text };
}

/** Perga tool inputs/results become readable text, without a provider item envelope. */
export function toolActivityContent(args: unknown, output: unknown, isError: boolean): { content?: string } {
  const sections: string[] = [];
  const argsObj = record(args);
  const hasArgs = argsObj !== null ? Object.keys(argsObj).length > 0 : args !== null && args !== undefined && args !== '';
  const input = hasArgs ? printable(args) : null;
  if (input) sections.push(`Input\n${input}`);
  const result = record(output);
  const body = isError || result === null ? printable(output) : [
    ...textBlocks(result.content), printable(result.structuredContent ?? (Object.hasOwn(result, 'content') ? undefined : result)),
  ].filter(Boolean).join('\n\n');
  if (body) sections.push(`${isError ? 'Error' : 'Output'}\n${body}`);
  return thinkingActivityContent(sections.join('\n\n'));
}
