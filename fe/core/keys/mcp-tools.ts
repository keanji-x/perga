/** Perga MCP tool names (`/mcp/kernel` `perga_*` and `/mcp/vcs` `vcs_*`), as they appear on the wire. */

/** Everything that changes the report. `perga_report_commit` is the canonical Perga batch/block writer. */
export const REPORT_WRITE_TOOLS: readonly string[] = Object.freeze([
  'perga_report_write',
  'perga_report_upsert',
  'perga_report_commit',
]);

/** Single-op block delete in stored history. */
export const REPORT_DELETE_TOOL = 'perga_report_delete';
/** Governance decisions on a task attempt. */
export const TASK_ACCEPT_TOOL = 'perga_task_accept';
export const TASK_REJECT_TOOL = 'perga_task_reject';
export const TASK_CANCEL_TOOL = 'perga_task_cancel';
export const TASK_DONE_TOOL = 'perga_task_done';
export const TASK_FAIL_TOOL = 'perga_task_fail';
export const TASK_LS_TOOL = 'perga_task_ls';

/** Reads of the report. */
export const REPORT_READ_TOOLS: readonly string[] = Object.freeze([
  'perga_report_read',
]);

/** Every report tool, read or write, is under this prefix: an unknown report tool counts as a change. */
export const REPORT_TOOL_PREFIX = 'perga_report_';

export const TRACK_TOOL_PREFIX = 'perga_track_';

/** A `perga_track_*` tool that is a WRITE — keep it out of the `TRACK_TOOL_PREFIX` read bucket. */
export const TRACK_RENAME_TOOL = 'perga_track_rename';
export const TRACK_CLOSE_TOOL = 'perga_track_close';

/** VCS publication: pushes the candidate branch and opens or reuses its PR (`/mcp/vcs`). */
export const DEV_PUBLISH_TOOL = 'vcs_publish_pr';

/** Human approval/question prompt (`perga_user_ask`). */
export const USER_ASK_TOOL = 'perga_user_ask';

/** Cross-track mail (`perga_mail_send`). */
export const MAIL_SEND_TOOL = 'perga_mail_send';
