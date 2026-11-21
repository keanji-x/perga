import {
    DEV_PUBLISH_TOOL,
    MAIL_SEND_TOOL,
    normalizeMcpToolName,
    REPORT_DELETE_TOOL, REPORT_READ_TOOLS, REPORT_WRITE_TOOLS,
    TASK_ACCEPT_TOOL,
    TASK_LIST_TOOL,
    TASK_REJECT_TOOL,
    TRACK_RENAME_TOOL, TRACK_TOOL_PREFIX,
    USER_ASK_TOOL,
} from '../keys/mcp-tools.js';
const ACTIVITY_TARGET_MAX = 64;

function clip(text: string): string | null {
  const line = text.trim().split('\n', 1)[0]?.trim() ?? '';
  if (line === '') return null;
  return line.length <= ACTIVITY_TARGET_MAX
    ? line
    : `${line.slice(0, ACTIVITY_TARGET_MAX - 1).trimEnd()}…`;
}

type ActivityShape = Readonly<{ running: string; done: string; target: string | null }>;

/**
 * The tools whose names are worth saying in English; an unknown tool keeps its wire name rather
 * than an invented phrase. `args` is the call's `arguments`, read only by a tool whose line names one.
 */
export function conversationToolShape(recordedTool: string, args: unknown): ActivityShape {
  const tool = normalizeMcpToolName(recordedTool);
  if (REPORT_WRITE_TOOLS.includes(tool)) {
    return { running: 'Writing report', done: 'Wrote report', target: null };
  }
  if (tool === REPORT_DELETE_TOOL) {
    return { running: 'Deleting blocks', done: 'Deleted blocks', target: null };
  }
  if (REPORT_READ_TOOLS.includes(tool)) {
    return { running: 'Reading report', done: 'Read report', target: null };
  }
  if (tool === TASK_ACCEPT_TOOL) {
    return { running: 'Accepting task', done: 'Accepted task', target: null };
  }
  if (tool === TASK_REJECT_TOOL) {
    return { running: 'Rejecting task', done: 'Rejected task', target: null };
  }
  if (tool === TASK_LIST_TOOL) {
    return { running: 'Reading plan', done: 'Read plan', target: null };
  }
  // Recognize track writes before the track prefix fallback.
  if (tool === TRACK_RENAME_TOOL) {
    return { running: 'Naming the track', done: 'Named the track', target: null };
  }
  if (tool === DEV_PUBLISH_TOOL) {
    return { running: 'Publishing the PR', done: 'Published the PR', target: null };
  }
  if (tool === MAIL_SEND_TOOL) {
    const summary = typeof args === 'object' && args !== null ? (args as { summary?: unknown }).summary : undefined;
    return { running: 'Sending mail', done: 'Sent mail', target: typeof summary === 'string' ? clip(summary) : null };
  }
  if (tool.startsWith(TRACK_TOOL_PREFIX)) {
    return { running: 'Reading the track', done: 'Read the track', target: null };
  }
  return { running: 'Calling', done: 'Called', target: clip(recordedTool) };
}


/** A successful application ask is speech. Refused or incomplete calls remain activities. */
export function conversationToolQuestion(tool: string, args: unknown): string | null {
  if (normalizeMcpToolName(tool) !== USER_ASK_TOOL || typeof args !== 'object' || args === null || !('questions' in args) || !Array.isArray(args.questions)) return null;
  const text = args.questions.flatMap((question: unknown) => {
    if (typeof question !== 'object' || question === null || !('title' in question) || typeof question.title !== 'string' || !question.title.trim()) return [];
    const options = 'options' in question && Array.isArray(question.options)
      ? question.options.filter((option): option is string => typeof option === 'string' && option.trim() !== '').map(option => option.trim()) : [];
    return [options.length > 0 ? `${question.title.trim()} (${options.join(' / ')})` : question.title.trim()];
  }).join('\n');
  return text === '' ? null : text;
}
