// Frontend presentation models. HTTP DTOs are generated in core/api/generated/wire.ts.

export type AgentProvider = string;

export type AskAction = { "kind": "objective_approval_v2", objective: string, standard_hash: string, } | { "kind": "objective_approval", objective: string, standard_hash: string, } | { "kind": "reopen_track", closed_at: number, } | { "kind": "grant_admin" } | { "kind": "track_plugin_access", plugin_id: string, enabled: boolean, manifest_sha256: string, expected_revision: number, } | { "kind": "forge_action", idempotency_key: string, payload_hash: string, };

export type AskAnswer = { "option": number } | { "text": string };

export type AskQuestion = { title: string,
/**
 * Complete plain-text context frozen with this question. Never clipped by title bounds.
 */
detail?: string, options: Array<string>, };

export type AttachmentId = string;

export type BlockMention = {
/**
 * The block's heading in the Report outline; a block with no heading is not offered.
 */
label: string, block_id: string,
/**
 * The title of the track whose report holds the block.
 */
track_title: string, track_id: TrackId, insert: string, };

export type CardId = string;

export type ConversationCursor = { contract: ConversationReadContract, card_id: string, source: ConversationCursorSource, position: number, };

export type ConversationCursorSource = "presentation" | "historical_raw";

export type ConversationDisplayIdentity = { "kind": "human", message_id: string, } | { "kind": "system", observation_id: string, } | { "kind": "native", thread_id: string, item_id: string, } | { "kind": "imported", provenance: ImportedProvenance, };

export type ConversationDisplayItem = { cursor: ConversationCursor, source: ConversationDisplaySource, };

export type ConversationDisplaySource = { "kind": "human", message_id: string, revision: number, author: ConversationHumanAuthor, text: string, attachments: Array<PlannerAttachment>, created_at_ms: number, delivery_error?: string | undefined, supplemental?: boolean | undefined, message_status?: "queued" | "sending" | "processing" | "completed" | "failed" | "unconfirmed" | undefined, } | { "kind": "system", observation_id: string, presentation: SystemInputPresentation, text: string, created_at_ms: number, } | { "kind": "native", item: HarnessItem, } | { "kind": "imported_item", item: HarnessItem, provenance: ImportedProvenance, } | { "kind": "historical_raw", item: HarnessItem, } | { "kind": "removed", identity: ConversationDisplayIdentity, };

export type ConversationHumanAuthor = { user_id: string, display_name: string, };

export type ConversationLiveReplies = { contract: ConversationReadContract, turn: ConversationLiveTurn | null, };

export type ConversationLiveTurn = { thread_id: string, turn_id: string, items: Array<HarnessLiveReply>, };

export type ConversationReadContract = "conversation_v1";

export type GitHubPreview = { kind: GitHubPreviewKind, number: number, title: string, state: GitHubPreviewState, author: string, labels: Array<string>, excerpt: string, changes: GitHubPullChanges | null, };

export type GitHubPreviewKind = "issue" | "pull";

export type GitHubPreviewState = "open" | "closed" | "merged" | "draft";

export type GitHubPullChanges = { additions: number, deletions: number, changed_files: number, };

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

export type HarnessInputPresentation = "user" | "system" | "system_worker_turn_finished" | "system_report_edited" | "system_task_completed" | "system_task_failed" | "system_mail" | "system_planner_turn_budget" | "system_planner_context_usage";

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

export type HarnessInterruptionReason = "max_turn_duration";

export type HarnessItem = { id: number, worker_session_id: string, card_id: CardId, track_id: TrackId, thread_id: string, turn_id: string | null, item_uuid: string | null, item_type: string | null, method: string, params: string, input_segments?: Array<HarnessInputSegment>, created_at_ms: number,
/**
 * Readable reason for a `turn/completed` row: the provider's reduced `error.message`, or a
 * confirmed kernel interruption cause when the provider gives no readable message. `params`
 * retains the raw provider error and kernel cause metadata. `null` without a readable reason.
 */
turn_error_text: string | null, };

export type HarnessLiveReplies = {
/**
 * The turn the items belong to: the turn that started last, until it ends with nothing left to
 * store; `null` before any turn of this harness and after one that ended so. A turn that ends
 * without settling (`turn/aborted`, an interrupt that times out and wedges) or whose partial
 * reply failed to store keeps its id here, with the replies it still holds, until the next turn
 * starts.
 */
turn_id: string | null, items: Array<HarnessLiveReply>, };

export type HarnessLiveReply = {
/**
 * The `item.id` its `item/completed` row will carry as `item_uuid`.
 */
item_id: string, text: string, };

export type HarnessPhaseTag = "pending_thread_start" | "idle" | "issuing_turn" | "compacting" | "issuing_interrupt" | "turn_running" | "turn_completed" | "resumed" | "wedged";

export type HarnessQueueEntryRef = { id: string, rev: number, };

export type HarnessTimeoutEvidence = { "version": "v1", request: HarnessTimeoutRequest, ended_at_ms: number, };

export type HarnessTimeoutRequest = { session_id: string, card_id: string, track_id: string, thread_id: string, turn_id: string, reason: HarnessInterruptionReason, requested_at_ms: number, effective_elapsed_ms: number, budget_ms: number, };

export type ImportedProvenance = { "kind": "unknown_legacy", original_row_id: number, worker_session_id: string, card_id: string, track_id: string, thread_id: string, turn_id: string | null, item_uuid: string | null, item_type: string | null, method: string, created_at_ms: number, original_digest: string, import_boundary_sequence: number, };

export type KeepConversationInputRequest = { carrier_id: string, submission_id: string, attempt_id: string,
/**
 * Exact decimal revision from the run read, never a JavaScript number.
 */
revision: string, };

export type MentionCandidates = { tags: Array<TagMention>, tracks: Array<TrackMention>, blocks: Array<BlockMention>, };

export type PlannerAttachment = { id: AttachmentId,
/**
 * Derived from `id`, never stored separately.
 */
contentType: string, size: number,
/**
 * Where to read the bytes; derived by [`attachment_url`]. The server's host path is NOT here and must not be.
 */
url: string, };

export type PlannerPermissionMode = string;

export type ReadReceipt = { kind: ReadReceiptKind, targetId: string, readThroughMs: number, manualUnread: boolean, };

export type ReadReceiptKind = "track" | "conversation";

export type ReadReceipts = { baselineMs: number, receipts: Array<ReadReceipt>, };

export type SourceOrigin = { "kind": "plugin", plugin_id: string, tool: string, args_sha256: string,
/**
 * Canonicalization version behind `args_sha256` (`v1`: compact serde_json text with sorted keys).
 */
args_canon: string, content_id?: string, } | { "kind": "manual", url?: string, content_id?: string, };

export type SourceProvenance = "full_text" | "summary" | "web_page" | "manual";

export type SourceQuote = { id: string, text: string, start: number, end: number, };

export type SystemInputPresentation = Exclude<HarnessInputPresentation, 'user'>;

export type TagMention = {
/**
 * The tag.
 */
label: string,
/**
 * How many of the area's reports carry the tag.
 */
track_count: number, insert: string, };

export type TrackId = string;

export type TrackMention = {
/**
 * The track title.
 */
label: string, track_id: TrackId, insert: string, };

export type TrackSourceDetail = { source_id: string, provenance: SourceProvenance, origin: SourceOrigin, title: string, published_at?: string, content_id?: string, url?: string, body_bytes: number, body_sha256: string, captured_at: string, quotes: Array<SourceQuote>, body: string, };

export type UploadAttachmentResponse = { attachmentId: AttachmentId, contentType: string, size: number,
/**
 * Absolute REST path the browser reads the bytes back from. Durable only once the attachment is
 * bound: a staged upload never sent is swept after the 24h orphan TTL, after which this path answers 400.
 */
url: string, };

export type WorkspaceIsolation = "managed" | "worktree" | "none";
