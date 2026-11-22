

export type AgentProvider = "opencode" | "codex" | "claude" | "grok" | "antigravity";

export type Area = { id: AreaId, name: string, color: string, sort: number, kind: AreaKind,
/**
 * Built-in Track template preselected by the official New Track surface; `None` keeps "No template".
 */
default_template_id: string | null,
/**
 * Exact attached Git working directory preselected for a new Track; `None` keeps the server-managed default.
 */
default_cwd: string | null, execution_target_id?: string, created_at: number, updated_at: number, };

/**
 * One row per claimed directory; `path` is absolute and globally unique across the table. A folder
 * covers every descendant path, and overlapping claims are rejected.
 */
export type AreaFolder = { id: number, area_id: AreaId, path: string, created_at: number, };

/**
 * Area identifier.
 */
export type AreaId = string;

/**
 * Whether an area is user-visible or kernel-owned storage scaffolding.
 */
export type AreaKind = "user" | "system";

/**
 * An explicit user choice with a lifecycle effect; absent on ordinary/native asks.
 */
export type AskAction = { "kind": "objective_approval_v2", objective: string, standard_hash: string, } | { "kind": "objective_approval", objective: string, standard_hash: string, } | { "kind": "reopen_track", closed_at: number, } | { "kind": "grant_admin" } | { "kind": "track_plugin_access", plugin_id: string, enabled: boolean, manifest_sha256: string, expected_revision: number, } | { "kind": "forge_action", idempotency_key: string, payload_hash: string, };

/**
 * One answer of an `ask.answered` (#2348): the index of a clicked option, or typed text. Clicking
 * an option and typing the same words are different answers.
 */
export type AskAnswer = { "option": number } | { "text": string };

/**
 * One question of an `ask.requested`: what the user is asked, and the answers offered. The first
 * option is the recommended one; no options means a free-text answer.
 */
export type AskQuestion = { title: string,
/**
 * Complete plain-text context frozen with this question. Never clipped by title bounds.
 */
detail?: string, options: Array<string>, };

/**
 * `<uuid-v4>.<ext>` — an attachment's id *and* its file name. The only constructor is
 * [`AttachmentId::parse`] and `Deserialize` goes through the same gate.
 */
export type AttachmentId = string;

/**
 * One block of a report of the area; `insert` is ``@`/AreaName/TrackName/report.md#<block_id>` ``.
 */
export type BlockMention = {
/**
 * The block's heading in the Report outline; a block with no heading is not offered.
 */
label: string, block_id: string,
/**
 * The title of the track whose report holds the block.
 */
track_title: string, track_id: TrackId, insert: string, };

export type Card = { id: CardId, track_id: TrackId,
/**
 * `"terminal"` for built-in PTY cards, `"ui://<plugin>/<view>"` for plugin-provided cards.
 * `[legacy]` `"plugin:<plugin-id>:<view-id>"` may still appear on persisted rows.
 */
kind: string, sort: number,
/**
 * Opaque JSON blob.
 */
payload: unknown, title?: string, runtime?: CardRuntimeView,
/**
 * System-card guard: `true` for user-facing cards, `false` for kernel-owned cards the user cannot
 * remove. Defaults to `true` because `bool::default()` is the unsafe fallback for a deny-by-omission auth bit.
 */
deletable: boolean, created_at: number, updated_at: number, };

/**
 * Card identifier.
 */
export type CardId = string;

/**
 * Live runtime projection read from `worker_sessions` when a card is fetched or serialized. Not
 * part of the idempotency contract: it may differ between a first POST response and a retry.
 */
export type CardRuntimeView = { worker_session_id: string, kind: WorkerSessionKind, status: WorkerSessionState, updated_at_ms?: number, provider?: AgentProvider, terminal_id?: string, thread_id?: string, session_id?: string, source?: string, thread_status?: string,
/**
 * When the card's last non-interrupted turn ended, or absent when it has none.
 */
last_turn_completed_ms?: number,
/**
 * A Claude card's child: the first token `claude --version` printed when it was started
 * (#2516). Absent on other runtimes, and when that run gave no answer.
 */
claude_version?: string, };

export type ConversationCursor = { contract: ConversationReadContract, card_id: string, source: ConversationCursorSource, position: number, };

export type ConversationCursorSource = "presentation" | "historical_raw";

export type ConversationDisplayIdentity = { "kind": "human", message_id: string, } | { "kind": "system", observation_id: string, } | { "kind": "native", thread_id: string, item_id: string, } | { "kind": "imported", provenance: ImportedProvenance, };

export type ConversationDisplayItem = { cursor: ConversationCursor, source: ConversationDisplaySource, };

export type ConversationDisplaySource = { "kind": "human", message_id: string, revision: number, author: ConversationHumanAuthor, text: string, attachments: Array<PlannerAttachment>, created_at_ms: number, delivery_error?: string | undefined, supplemental?: boolean | undefined, message_status?: "queued" | "sending" | "processing" | "completed" | "failed" | "unconfirmed" | undefined, } | { "kind": "system", observation_id: string, presentation: SystemInputPresentation, text: string, created_at_ms: number, } | { "kind": "native", item: HarnessItem, } | { "kind": "imported_item", item: HarnessItem, provenance: ImportedProvenance, } | { "kind": "historical_raw", item: HarnessItem, } | { "kind": "removed", identity: ConversationDisplayIdentity, };

export type ConversationHumanAuthor = { user_id: string, display_name: string, };

export type ConversationLiveReplies = { contract: ConversationReadContract, turn: ConversationLiveTurn | null, };

export type ConversationLiveTurn = { thread_id: string, turn_id: string, items: Array<HarnessLiveReply>, };

export type ConversationReadContract = "conversation_v1";

/**
 * 409 body for the folder-create conflict case.
 */
export type FolderConflict = { folder_id: number, area_id: AreaId, conflict_path: string, conflict_kind: FolderConflictKind, };

/**
 * Kind of overlap detected by the `POST /api/areas/:area_id/folders` conflict check.
 */
export type FolderConflictKind = "equal" | "ancestor" | "descendant";

export type GitHubPreview = { kind: GitHubPreviewKind, number: number, title: string, state: GitHubPreviewState, author: string, labels: Array<string>, excerpt: string, changes: GitHubPullChanges | null, };

export type GitHubPreviewKind = "issue" | "pull";

export type GitHubPreviewState = "open" | "closed" | "merged" | "draft";

export type GitHubPullChanges = { additions: number, deletions: number, changed_files: number, };

/**
 * Why a segment was in the turn: the observation that produced it and the event behind it.
 * `queue_entry` binds identity and actual revision as one fact. Legacy or non-user input
 * has no versioned reference, including released origins that omitted this metadata.
 */
export type HarnessInputOrigin = {
/**
 * The observation's serde `type` tag, e.g. `task_completed`, `track_wake`, `user_message`.
 */
observation: string,
/**
 * The `events.id` that produced the observation (for a folded entry, the newest one);
 * `null` when no event did, e.g. a user message or the Track goal seed.
 */
event_id: number | null, queue_entry: HarnessQueueEntryRef | null, };

/**
 * How one segment of a harness `userMessage` should be presented to a human. The rendered English
 * is not a protocol: wording changes must not turn a system update into something the UI attributes to the user.
 */
export type HarnessInputPresentation = "user" | "system" | "system_worker_turn_finished" | "system_report_edited" | "system_task_completed" | "system_task_failed" | "system_mail" | "system_planner_turn_budget" | "system_planner_context_usage";

/**
 * One observation in the exact order and wording sent to `turn/start`.
 */
export type HarnessInputSegment = { presentation: HarnessInputPresentation, text: string,
/**
 * The images this segment carried into `turn/start`.
 */
attachments: Array<PlannerAttachment>,
/**
 * The queue entry this segment was built from. Every segment a turn issues or a steer
 * delivers carries one. Absent only on rows written before #2206 and on the echo of a
 * turn an older binary had in flight (its segments were restored from that binary's
 * snapshot, which has no queue entry to name). Trusted per-turn budget segments also
 * have no queue entry: their owner is the persisted start attempt.
 */
origin?: HarnessInputOrigin, };

/**
 * Kernel-owned cause recorded only for a confirmed interrupted turn.
 * This metadata is separate from the provider's original error payload.
 */
export type HarnessInterruptionReason = "max_turn_duration";

export type HarnessItem = { id: number, worker_session_id: string, card_id: CardId, track_id: TrackId, thread_id: string, turn_id: string | null, item_uuid: string | null, item_type: string | null, method: string, params: string, input_segments?: Array<HarnessInputSegment>, created_at_ms: number,
/**
 * Readable reason for a `turn/completed` row: the provider's reduced `error.message`, or a
 * confirmed kernel interruption cause when the provider gives no readable message. `params`
 * retains the raw provider error and kernel cause metadata. `null` without a readable reason.
 */
turn_error_text: string | null, };

/**
 * `GET /api/cards/{id}/harness/live`: reply text the running turn is streaming and has not stored
 * yet (#1923). Each item leaves this answer once its `item/completed` row is in the transcript.
 */
export type HarnessLiveReplies = {
/**
 * The turn the items belong to: the turn that started last, until it ends with nothing left to
 * store; `null` before any turn of this harness and after one that ended so. A turn that ends
 * without settling (`turn/aborted`, an interrupt that times out and wedges) or whose partial
 * reply failed to store keeps its id here, with the replies it still holds, until the next turn
 * starts.
 */
turn_id: string | null, items: Array<HarnessLiveReply>, };

/**
 * One reply as far as it has streamed.
 */
export type HarnessLiveReply = {
/**
 * The `item.id` its `item/completed` row will carry as `item_uuid`.
 */
item_id: string, text: string, };

export type HarnessPhaseTag = "pending_thread_start" | "idle" | "issuing_turn" | "compacting" | "issuing_interrupt" | "turn_running" | "turn_completed" | "resumed" | "wedged";

/**
 * The actual addressable queue entry and the revision its input was built from.
 */
export type HarnessQueueEntryRef = { id: string, rev: number, };

/**
 * Written exclusively at the kernel terminal-outcome boundary after confirmation.
 */
export type HarnessTimeoutEvidence = { "version": "v1", request: HarnessTimeoutRequest, ended_at_ms: number, };

/**
 * All facts are sampled by the watchdog before issuing the interrupt.
 */
export type HarnessTimeoutRequest = { session_id: string, card_id: string, track_id: string, thread_id: string, turn_id: string, reason: HarnessInterruptionReason, requested_at_ms: number, effective_elapsed_ms: number, budget_ms: number, };

/**
 * Immutable body-free facts, not an authenticated input origin. Digest v1 is
 * SHA-256 of JSON serialization of (original params string, nullable original
 * input_segments string); null and an empty array remain distinct.
 */
export type ImportedProvenance = { "kind": "unknown_legacy", original_row_id: number, worker_session_id: string, card_id: string, track_id: string, thread_id: string, turn_id: string | null, item_uuid: string | null, item_type: string | null, method: string, created_at_ms: number, original_digest: string, import_boundary_sequence: number, };

export type KeepConversationInputRequest = { carrier_id: string, submission_id: string, attempt_id: string,
/**
 * Exact decimal revision from the run read, never a JavaScript number.
 */
revision: string, };

/**
 * The candidates for one query, best first within each group.
 */
export type MentionCandidates = { tags: Array<TagMention>, tracks: Array<TrackMention>, blocks: Array<BlockMention>, };

export type Overlay = { id: string, plugin_id: string,
/**
 * `"track"` or `"card"`.
 */
entity_kind: string, entity_id: string,
/**
 * Plugin-defined string. Kernel does not interpret.
 */
kind: string,
/**
 * Opaque JSON blob.
 */
payload: unknown, updated_at: number, };

/**
 * One attachment as the frontend sees it in a queue entry or a transcript segment.
 */
export type PlannerAttachment = { id: AttachmentId,
/**
 * Derived from `id`, never stored separately.
 */
contentType: string, size: number,
/**
 * Where to read the bytes; derived by [`attachment_url`]. The server's host path is NOT here and must not be.
 */
url: string, };

/**
 * What a Planner card's provider may do outside its sandbox (#2348, #2441): nothing, what the
 * person allows when asked, or anything, with no sandbox at all. Stored on the card as the server-owned payload key `permission_mode`; new Planner
 * cards use their ACP registration’s declared default, or `never` without one; delegated children inherit their parent’s mode. Only `PUT /api/cards/{id}/planner/permission-mode` changes it.
 */
export type PlannerPermissionMode = string;

/**
 * One target's receipt. `readThroughMs` only rises; `manualUnread` is the reader's explicit
 * "mark unread", cleared by the next mark read.
 */
export type ReadReceipt = { kind: ReadReceiptKind, targetId: string, readThroughMs: number, manualUnread: boolean, };

/**
 * What a receipt is about: a track, or one conversation. A closed set, checked by the table too.
 */
export type ReadReceiptKind = "track" | "conversation";

/**
 * `GET /api/read-receipts`: the cutover baseline (activity at or before it is read) and every
 * stored receipt.
 */
export type ReadReceipts = { baselineMs: number, receipts: Array<ReadReceipt>, };

/**
 * Where a body came from. Stored verbatim as the row's `origin` column.
 */
export type SourceOrigin = { "kind": "plugin", plugin_id: string, tool: string, args_sha256: string,
/**
 * Canonicalization version behind `args_sha256` (`v1`: compact serde_json text with sorted keys).
 */
args_canon: string, content_id?: string, } | { "kind": "manual", url?: string, content_id?: string, };

/**
 * What the body *is*: the Planner's declaration, page-visible. `manual` bodies are the Planner's
 * own and are marked as not kernel-verified.
 */
export type SourceProvenance = "full_text" | "summary" | "web_page" | "manual";

/**
 * One anchor of a source: `text` is a byte-exact substring of the body (`body[start..end]`, UTF-8 byte offsets).
 */
export type SourceQuote = { id: string, text: string, start: number, end: number, };

export type SystemInputPresentation = Exclude<HarnessInputPresentation, 'user'>;

/**
 * A report tag of the area; `insert` is ``@`tag:<tag>` ``.
 */
export type TagMention = {
/**
 * The tag.
 */
label: string,
/**
 * How many of the area's reports carry the tag.
 */
track_count: number, insert: string, };

export type Track = { id: TrackId, area_id: AreaId, title: string, sort: number, pinned_at: number | null,
/**
 * Unix-ms time the track was closed, or `None` while it is open. Closed tracks do not schedule.
 */
closed_at: number | null,
/**
 * Unix-ms time a person granted Area admin authority; absent on historical events.
 */
admin_granted_at: number | null,
/**
 * Wire-compatibility alias of `workspace.path`, serialized as `cwd`; Rust readers must use `workspace.path`.
 */
cwd: string,
/**
 * Currently selected Track working method. Switching preserves authored Report content.
 */
template_id: string | null,
/**
 * The plugin owning the selected working method. Earlier owners retain manifest-bound grants.
 */
plugin_scope: string | null,
/**
 * Server-owned structural marker. Public track creation cannot set this.
 */
purpose: string | null,
/**
 * Template input is validated at creation and otherwise remains opaque.
 */
template_input: unknown,
/**
 * The user recipe ([`TrackRecipe`]) this track was instantiated from; may name a recipe that no longer exists.
 */
recipe_id: string | null,
/**
 * The recipe's `revision` at the moment this track was created.
 */
recipe_revision: number | null,
/**
 * The Track whose Planner created this one as a top-level Track; `None` for every other
 * creation. May name a Track that no longer exists.
 */
creator_track_id: string | null,
/**
 * The raw idempotency key that creator passed; set exactly when `creator_track_id` is.
 */
creator_key: string | null, workspace: TrackWorkspace, created_at: number, updated_at: number, };

/**
 * Track identifier.
 */
export type TrackId = string;

/**
 * A report of the area; `insert` is ``@`/AreaName/TrackName/report.md` ``, the path `neige cat` reads.
 */
export type TrackMention = {
/**
 * The track title.
 */
label: string, track_id: TrackId, insert: string, };

/**
 * A captured source with its body: the raw text the kernel stored, verbatim.
 */
export type TrackSourceDetail = { source_id: string, provenance: SourceProvenance, origin: SourceOrigin, title: string, published_at?: string, content_id?: string, url?: string, body_bytes: number, body_sha256: string, captured_at: string, quotes: Array<SourceQuote>, body: string, };

/**
 * Transport wrapper; never used for database events or operation payloads.
 */
export type TrackWire = Track & { workspace: TrackWorkspaceWire };

/**
 * A track's typed workspace. `path` is its single stored path.
 */
export type TrackWorkspace = { kind: TrackWorkspaceKind,
/**
 * Absolute path.
 */
path: string,
/**
 * One-shot, monotonic. `Some` ⇒ neither `path` nor `kind` may change again. The system-area
 * launchpad stays unfrozen because it is repointed.
 */
frozen_at: number | null,
/**
 * The kernel-made git worktree (`<repo_root>/.claude/worktrees/track-<id>`, branch
 * `neige/track-<id>`) an attached track's conversation agents run in. `None` for managed,
 * child and pre-#1830 attached tracks, which run in `path`.
 */
worktree?: string, };

/**
 * Ownership must be explicit because only managed workspaces may be recycled.
 */
export type TrackWorkspaceKind = "managed" | "attached";

export type TrackWorkspaceWire = { isolation: WorkspaceIsolation, kind: TrackWorkspaceKind,
/**
 * Absolute path.
 */
path: string,
/**
 * One-shot, monotonic. `Some` ⇒ neither `path` nor `kind` may change again. The system-area
 * launchpad stays unfrozen because it is repointed.
 */
frozen_at: number | null,
/**
 * The kernel-made git worktree (`<repo_root>/.claude/worktrees/track-<id>`, branch
 * `neige/track-<id>`) an attached track's conversation agents run in. `None` for managed,
 * child and pre-#1830 attached tracks, which run in `path`.
 */
worktree?: string, };

/**
 * `201` body of `POST /api/cards/{id}/planner/attachments`.
 */
export type UploadAttachmentResponse = { attachmentId: AttachmentId, contentType: string, size: number,
/**
 * Absolute REST path the browser reads the bytes back from. Durable only once the attachment is
 * bound: a staged upload never sent is swept after the 24h orphan TTL, after which this path answers 400.
 */
url: string, };

export type WorkerSessionKind = "terminal" | "managed-assistant";

/**
 * Session state machine column (`worker_sessions.state`).
 */
export type WorkerSessionState = "starting" | "running" | "idle" | "turn_pending" | "exited" | "failed" | "superseded";

/**
 * The decoded isolation of a track. This is derived, never stored separately.
 */
export type WorkspaceIsolation = "managed" | "worktree" | "none";

/** Perga /api/v1 schedule contracts; aligned with Perga.Contracts/TrackScheduleDtos.cs. */
export type TrackScheduleDto = {
  id: string; trackId: string; sourceKind: string; scheduleKind: 'once' | 'cron'; title: string; prompt: string;
  cronExpr: string | null; timezone: string; runAtMs: number | null; nextFireAtMs: number | null;
  lastFiredAtMs: number | null; maxOccurrences: number | null; firedCount: number;
  state: 'active' | 'done' | 'canceled'; createdAtMs: number; updatedAtMs: number;
};
export type TrackScheduleRunDto = {
  wakeId: string; scheduleId: string | null; state: string; reason: string | null;
  turnId: string | null; createdAtMs: number; updatedAtMs: number;
};

export type TodayCalendar = { today: string, time_zone: string, start: string, end: string, days: Array<TodayCalendarDay>, schedules: Array<TodayScheduleItem>, };

export type TodayCalendarDay = { date: string, has_daily_track: boolean, has_report: boolean, };

export type TodayScheduleItem = { id: string, date: string, track_id: string, track_title: string, title: string, schedule_kind: "once" | "cron", cron_expr: string | null, timezone: string, next_fire_at_ms: number, state: string, };

export type ReportChangesPage = { date: string, timezone: string, through_event_id: number, changes: Array<ReportChange>, next_cursor: string | null, };

export type ReportChange = { track_id: string, track_title: string, area_id: string, area_name: string, edit_count: number, first_event_id: number, last_event_id: number, summary_before: string, summary_after: string, patch: string, patch_truncated: boolean, };

export type ReportEditsPage = { edits: Array<ReportEditEntry>, next_cursor: string | null, };

export type ReportEditEntry = { event_id: number, at: number, edit: ReportEdit, };

export type ReportEdit = { track_id: string, edit_id: string, summary_before: string, summary_after: string, body_before: string, body_after: string, };

export type BootstrapDto = import('../perga-v1.js').BootstrapDto;

export type AreaDto = import('../perga-v1.js').AreaDto;

export type TrackWorkspaceOptionsDto = import('../perga-v1.js').TrackWorkspaceOptionsDto;

export type TrackAggregateSnapshotDto = import('../perga-v1.js').TrackAggregateSnapshotDto;

export type TrackDto = import('../perga-v1.js').TrackDto;

export type TrackTemplateSnapshot = import('zod').infer<typeof import('../perga-v1.js').trackTemplateSnapshotSchema>;

export type WorkspaceFileResultDto = import('../perga-v1.js').WorkspaceFileResultDto;

export type DiffSummaryDto = import('../perga-v1.js').DiffSummaryDto;

export type ReportReadResultDto = import('../perga-v1.js').ReportReadResultDto;

export type SessionCreationReceipt = import('../perga-v1.js').SessionCreationReceipt;

export type SessionItemsPageDto = import('../perga-v1.js').SessionItemsPageDto;

export type AgentRuntimeStatusDto = import('../perga-v1.js').AgentRuntimeStatusDto;

export type AgentRuntimeTaskDto = import('../perga-v1.js').AgentRuntimeTaskDto;

export type SessionMessageReceipt = import('zod').infer<typeof import('../perga-v1.js').sessionMessageReceiptSchema>;

export type TerminalDto = import('../perga-v1.js').TerminalDto;

export type HostDirListDto = import('../perga-v1.js').HostDirListDto;

export type OverlaysListDto = import('../perga-v1.js').OverlaysListDto;

export type TaskDto = import('../perga-v1.js').TaskDto;

export type AgentSessionDto = import('../perga-v1.js').AgentSessionDto;

export type SystemInfoDto = { releaseId: string; webBuildId: string; hostBuildId: string; bridgeBuildId: string; schemaVersion: number; draining: boolean; activeDispatches: number };

export type SystemDrainResponse = { draining: boolean; activeDispatches: number };

export type SystemReloadResponse = { releaseId: string; webBuildId: string };

export type TrackSchedulesSnapshot = { schedules: TrackScheduleDto[]; recentRuns: TrackScheduleRunDto[] };

export type SessionInteractionResult = import('zod').infer<typeof import('../perga-v1.js').sessionInteractionResultSchema>;

export type ReadReceiptsResponseDto = ReadReceipts;

export type ReadReceiptDto = ReadReceipt;
