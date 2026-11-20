import type { ReportBlockDto } from '../api/schemas.js';

function jsonObject(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

/** The Host reads a task declaration from an object payload or JSON in the block's Markdown. */
export function reportTaskPayload(block: { payload?: unknown; markdown?: string | null }): Record<string, unknown> | null {
  const payload = jsonObject(block.payload);
  if (payload !== null) return payload;

  let text = block.markdown?.trim() ?? '';
  const firstNewLine = text.indexOf('\n');
  if (text.startsWith('```') && firstNewLine >= 0) {
    const body = text.slice(firstNewLine + 1);
    const closingFence = body.lastIndexOf('```');
    text = (closingFence < 0 ? body : body.slice(0, closingFence)).trim();
  }
  if (!text.startsWith('{')) return null;
  try { return jsonObject(JSON.parse(text)); } catch { return null; }
}

const TASK_KEY_FIELDS = Object.freeze(['task_key', 'taskKey', 'key', 'id']);

/** Follow TaskBlockSynchronizer's key precedence, including exact matches before case-insensitive aliases. */
export function reportTaskKey(payload: Record<string, unknown>, blockId: string): string {
  const field = TASK_KEY_FIELDS.find((key) => Object.hasOwn(payload, key))
    ?? Object.keys(payload).find((key) => TASK_KEY_FIELDS.some((alias) => alias.toLowerCase() === key.toLowerCase()));
  const value = field === undefined ? undefined : payload[field];
  return typeof value === 'string' ? value : blockId;
}

/** Retained task records are current only when their block still declares the same task. */
export function liveReportTaskKey(block: ReportBlockDto): string | null {
  if (block.kind !== 'task') return null;
  const payload = reportTaskPayload(block);
  if (payload === null) return block.id;
  return 'tombstoned_by' in payload ? null : reportTaskKey(payload, block.id);
}
