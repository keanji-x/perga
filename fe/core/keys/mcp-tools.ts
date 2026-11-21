/** Normalize session item names recorded by built-in MCP servers and OAR. */
export function normalizeMcpToolName(tool: string): string {
  const name = tool.replace(/^(?:perga|vcs):/, '').replace(/^(?:perga|vcs)_/, '');
  return name === 'task_ls' ? TASK_LIST_TOOL : name;
}

/** Everything that changes the report. `report_commit` is the canonical Perga batch/block writer. */
export const REPORT_WRITE_TOOLS: readonly string[] = Object.freeze([
  'report_write',
  'report_upsert',
  'report_commit',
]);

/** Single-op block delete in stored history. */
export const REPORT_DELETE_TOOL = 'report_delete';
/** Governance decisions on a task attempt. */
export const TASK_ACCEPT_TOOL = 'task_accept';
export const TASK_REJECT_TOOL = 'task_reject';
export const TASK_CANCEL_TOOL = 'task_cancel';
export const TASK_DONE_TOOL = 'task_done';
export const TASK_FAIL_TOOL = 'task_fail';
export const TASK_LIST_TOOL = 'task_list';

/** Reads of the report. */
export const REPORT_READ_TOOLS: readonly string[] = Object.freeze([
  'report_read',
]);

/** Every report tool, read or write, is under this prefix: an unknown report tool counts as a change. */
export const REPORT_TOOL_PREFIX = 'report_';

export const TRACK_TOOL_PREFIX = 'track_';

/** A `track_*` tool that is a WRITE — keep it out of the `TRACK_TOOL_PREFIX` read bucket. */
export const TRACK_RENAME_TOOL = 'track_rename';
export const TRACK_CLOSE_TOOL = 'track_close';

/** VCS publication: pushes the candidate branch and opens or reuses its PR (`/mcp/vcs`). */
export const DEV_PUBLISH_TOOL = 'publish_pr';

/** Human approval/question prompt (`user_ask`). */
export const USER_ASK_TOOL = 'user_ask';

/** Cross-track mail (`mail_send`). */
export const MAIL_SEND_TOOL = 'mail_send';
