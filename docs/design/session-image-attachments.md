# Session Image Attachments: Clipboard Paste, Preview, Lightbox & End-to-End Delivery

- **Review Tier**: `AGENTS.md · L2` (forward SQLite migration `0026`, binary blob lifecycle & orphan GC, upload/download security boundaries, cross-layer contract changes spanning `Perga.Storage.Sqlite`, `Perga.Contracts`, `Perga.Kernel`, `Perga.Host`, `Perga.AgentKit`, `tools/perga-oar-bridge`, and `fe`).
- **Status**: Approved design specification for implementation (`backend-attachments` → `frontend-paste-lightbox`).

---

## 1. Goal & Unified Scope

Provide a single, consistent image attachment mechanism across **every composer surface** in Perga:

1. **Existing Session (Desktop)** — `ConversationComposer` in drawer / inline pane (`fe/web/src/app/router/conversation-pane.tsx`).
2. **Existing Session (Mobile)** — `ConversationComposer` (`layout="mobile"`) in mobile dock & bottom sheet (same Web component stack).
3. **New Session Draft** — pre-session composer inside an existing Track (`conversation-pane.tsx` draft mode + `draft-actions.ts`).
4. **New Track Page** — pre-track composer inside an Area (`fe/web/src/features/area/new-track/public.tsx` + `new-track-route.tsx`).

Core user-facing invariants:
- Works identically on **both managed workspaces (`vcs` / `kernel-overlayfs`) and attached/direct workspaces (`direct`)** because image bytes are stored under Host-managed `{DataDir}/attachments/...`, never inside user repositories.
- Supports clipboard paste (pure screenshot, multi-image rapid paste, mixed text+image without losing text or injecting HTML), drag-and-drop with highlight, and `<input type="file" multiple>`.
- Shows instant optimistic `ObjectURL` thumbnails with upload progress, per-item failure retry/remove, and prevents premature sends while uploads are in flight or failed.
- Supports **full-chain click-to-zoom (`Lightbox`)** with zoom/pan, multi-image prev/next gallery navigation, and `Esc` dismissal in composer drawers, sent messages, queued/sending messages, and draft previews.
- **Fail-closed delivery**: images are never silently dropped when a runtime does not support them or when remote SSH staging fails.

---

## 2. Verified Dependency APIs

### 2.1 `@botiverse/oar@0.124.0` (`tools/perga-oar-bridge`)

Verified directly against `tools/perga-oar-bridge/node_modules/@botiverse/oar/dist/`:

- **Image input contract** (`dist/contracts/input.d.ts`, `dist/contracts/session.d.ts`):
  ```ts
  export interface InputImage {
    /** Absolute path on the machine the runtime runs on. */
    readonly path: string;
    /** `image/png`, `image/jpeg`, `image/gif` or `image/webp`; inferred from extension if omitted. */
    readonly mediaType?: string;
  }
  export interface InputOptions {
    readonly inputId?: string;
    readonly images?: readonly InputImage[];
    readonly origin?: InputOrigin;
  }
  export interface PromptOptions extends InputOptions {
    readonly carries?: readonly string[];
  }
  ```
  - Both `session.prompt(input, options)` and `session.steer?(input, options)` accept `options.images`.
  - Empty text (`input === ""`) is explicitly valid whenever `(images?.length ?? 0) > 0` (`dist/shared/control-input.js`).
  - OAR reads files from `image.path` on the **machine where the runtime executes** (`accessSync(R_OK)` / `readFileSync` in `dist/shared/input-images.js`). Because `codex` passes `{ type: "localImage", path: image.path }` directly to the Codex CLI and ACP passes `uri: pathToFileURL(image.path).href` plus base64 `data`, files MUST exist at an absolute path on the execution target with a canonical extension (`.png`/`.jpg`/`.gif`/`.webp`) and explicit `mediaType`.
- **Fail-closed refusal** (`dist/shared/input-images.js`):
  - `inputImagesRefusal` / `withInputImages` rejects the entire turn with `{ kind: "rejected", code: "unsupported" | "error", reason }` if `!session.capabilities.images`, media type is not in `{png, jpeg, gif, webp}`, or file is unreadable. OAR never strips images and delivers text alone.
- **`capabilities.images` semantics** (`dist/contracts/runtime.d.ts`, `dist/contracts/session.d.ts`):

| OAR Runtime | `runtime.sessionCapabilities(inst)?.images` (pre-open) | `session.capabilities.images` (post-open, authoritative) | Native Wire Format |
|---|---|---|---|
| `codex` | `true` | `true` | `{ type: "localImage", path }` (`prompt` & `steer`) |
| `claude` | `true` | `true` | Base64 image blocks via `withInputImages` |
| `antigravity` (ACP) | `undefined` (handshake-only) | `initialize.agentCapabilities.promptCapabilities.image === true` | ACP `{ type: "image", mimeType, data, uri }` |
| `opencode` (ACP) | `undefined` (handshake-only) | `initialize.agentCapabilities.promptCapabilities.image === true` | ACP `{ type: "image", mimeType, data, uri }` |
| `cursor`, `factory`, `grok`, `pi` | `true` | `true` | Runtime-native base64/image blocks |
| `qoder`, `qwen`, `pi-durable` | `false` | `false` | Always rejected `unsupported` |

  - **Rule**: `undefined` in `runtime.sessionCapabilities()` means *unknown until handshake*, **never** `false`. `probe` MUST emit tri-state `supportsImages: true | false | null`, while `session/open` MUST emit authoritative boolean `supportsImages: session.capabilities.images === true`.

### 2.2 `@astryxdesign/core@0.6.3` (`fe`)

Verified directly against `fe/node_modules/@astryxdesign/core/src/` (shared by desktop & mobile Web builds):

- **`ChatComposer`** (`src/Chat/ChatComposer.tsx`):
  - Slots: `drawer`, `headerActions`, `headerContext`, `input`, `footerActions`, `sendActions`, `sendButton`, `status`.
  - **Built-in constraint**: `handleSubmit` bails when `!value.trim() || isDisabled`, and `canSend` is `currentValue.trim().length > 0 && !isDisabled`. Therefore image-only sends MUST use a custom `sendButton` / `sendActions` trigger and `onKeyDownCapture` Enter interception (already established in `features/chat/thread/public.tsx` and `features/area/new-track/public.tsx`).
- **`ChatComposerInput`** (`src/Chat/ChatComposerInput.tsx`):
  - Props: `handleRef` (`insertText`, `insertToken`, `focus`, `getValue`), `onFiles?: (files: File[]) => void`, `onPaste?: (event, text) => boolean | void`, `onKeyDown`, `triggers`, `isDisabled`.
  - **Built-in gotcha #1 (mixed paste)**: `handlePaste` (lines 773–778) checks `Array.from(e.clipboardData.files)` first and immediately calls `e.preventDefault(); onFiles?.(files); return;`. When a clipboard holds **both** text and an image (or exposes screenshots only in `clipboardData.items`), `ChatComposerInput` drops the accompanying plain text. **Fix (zero Astryx patch)**: handle paste in `onPasteCapture` on the composer input wrapper before `ChatComposerInput` runs, inserting `text/plain` via `handleRef.current.insertText(...)` when mixed with images.
  - **Built-in gotcha #2 (drag highlight)**: `handleDragOver`/`handleDrop` prevent default navigation on the editable area only and expose no drag-over highlight state. **Fix**: wrap the composer container with a counter-balanced `useDropzoneHighlight()` handler.
- **`Thumbnail`** (`src/Thumbnail/Thumbnail.tsx`):
  - Props: `src`, `alt`, `label`, `onClick`, `onRemove`, `isLoading`, `isDisabled`, `showRemoveOn?: 'hover' | 'always'`.
  - **Built-in optimistic upload support** (lines 316–317, 434–438): passing **both** `src={objectUrl}` and `isLoading={true}` renders the local image preview beneath a translucent scrim + `<Spinner size="sm" shade="onMedia" />` while keeping `onRemove` active (`hasRemove = onRemove != null && !isDisabled`).
  - **Click isolation** (lines 390–393): `removeButtonEl` calls `e.stopPropagation(); onRemove(e);`, so clicking `×` never triggers `onClick` (Lightbox). On coarse/touch pointers, `showRemoveOn="hover"` automatically stays visible.
  - When `isLoading={true}`, `Thumbnail` disables `onClick` (`isInteractive = onClick != null && !isDisabled && !isLoading`). Once `ready` (or for clickable `failed` items with `isLoading={false}`), `onClick` renders a keyboard-accessible `<button type="button">` opening the Lightbox.
- **`Lightbox` & `useLightbox`** (`src/Lightbox/Lightbox.tsx`, `useLightbox.tsx`):
  - Native `<dialog>` via `showModal()` (top-layer, focus trap, scroll lock, trigger focus restoration, `useLayerDismissal` for `Esc` and mobile back gesture).
  - `useLightbox({ media, hasZoom: true })`: `media: { src: string; alt: string; caption?: ReactNode }[]`. Supports double-click / `Enter` / `Space` / `+` / `-` 1x↔2x zoom, pointer-drag & arrow-key pan when zoomed, `ArrowLeft` / `ArrowRight` gallery navigation when 1x, and backdrop/close/`Esc` dismissal.

---

## 3. SQLite Migration `0026_session_attachments.sql` & Storage Lifecycle

### 3.1 Schema (`src/Perga.Storage.Sqlite/Migrations/0026_session_attachments.sql`)

```sql
CREATE TABLE session_attachments (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT REFERENCES tracks(id) ON DELETE CASCADE,
    session_id          TEXT REFERENCES agent_sessions(id) ON DELETE CASCADE,
    status              TEXT NOT NULL CHECK (status IN ('staged', 'bound')),
    content_type        TEXT NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')),
    size_bytes          INTEGER NOT NULL CHECK (size_bytes > 0),
    sha256              TEXT NOT NULL,
    original_filename   TEXT,
    storage_rel_path    TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL,
    bound_at_ms         INTEGER,
    CHECK (
        (status = 'staged' AND bound_at_ms IS NULL)
        OR (status = 'bound' AND track_id IS NOT NULL AND session_id IS NOT NULL AND bound_at_ms IS NOT NULL)
    ),
    CHECK (session_id IS NULL OR track_id IS NOT NULL)
);

CREATE INDEX idx_session_attachments_session_status
    ON session_attachments(session_id, status, created_at_ms)
    WHERE session_id IS NOT NULL;

CREATE INDEX idx_session_attachments_staged_gc
    ON session_attachments(status, created_at_ms)
    WHERE status = 'staged';

CREATE INDEX idx_session_attachments_track
    ON session_attachments(track_id)
    WHERE track_id IS NOT NULL;

CREATE TABLE session_message_attachments (
    message_ordinal     INTEGER NOT NULL REFERENCES session_messages(ordinal) ON DELETE CASCADE,
    position            INTEGER NOT NULL CHECK (position >= 0 AND position < 8),
    attachment_id       TEXT NOT NULL REFERENCES session_attachments(id) ON DELETE CASCADE,
    PRIMARY KEY (message_ordinal, position),
    UNIQUE (message_ordinal, attachment_id)
);

CREATE INDEX idx_session_message_attachments_attachment
    ON session_message_attachments(attachment_id);

CREATE TRIGGER session_message_attachments_same_session
BEFORE INSERT ON session_message_attachments
WHEN NOT EXISTS (
    SELECT 1
    FROM session_messages m
    JOIN session_attachments a ON a.id = NEW.attachment_id
    WHERE m.ordinal = NEW.message_ordinal
      AND a.session_id = m.session_id
      AND a.status = 'bound'
)
BEGIN
    SELECT RAISE(ABORT, 'attachment does not belong to message session or is not bound');
END;
```

Notes:
- `session_messages.content` (`0006_session_messages.sql`) is already `TEXT NOT NULL`, which accepts `""` for image-only messages without modifying released migrations.
- `session_message_attachments` is many-to-many `(message_ordinal, position, attachment_id)`, preserving attachment display/delivery order while allowing **re-send / regenerate** reuse of already-bound attachments within the same `session_id`.
- The DB trigger mirrors `session_items_message_insert` (`0006_session_messages.sql`) as a fail-closed invariant preventing cross-session or unbound links.

### 3.2 Attachment States & Crash-Safe Promotion

| Surface | Upload Endpoint | Initial DB State | Initial Disk Path | Atomic Binding Transition |
|---|---|---|---|---|
| Existing Session (Desktop/Mobile) | `POST /api/v1/sessions/{sid}/attachments` | `staged`, `track_id=T`, `session_id=S` | `attachments/{T}/{id}.{ext}` | `POST /sessions/{S}/messages` → `bound` + `session_message_attachments` |
| New Session Draft | `POST /api/v1/attachments?trackId={T}` | `staged`, `track_id=T`, `session_id=NULL` | `attachments/{T}/{id}.{ext}` | `POST /tracks/{T}/sessions` → `session_id=S`, `bound` + `session_message_attachments` |
| New Track Page | `POST /api/v1/attachments` | `staged`, `track_id=NULL`, `session_id=NULL` | `attachments/_staged/{id}.{ext}` | `POST /tracks` → promote to `attachments/{T}/{id}.{ext}`, `track_id=T, session_id=leadS, staged`; then `POST /sessions/{leadS}/messages` → `bound` + `session_message_attachments` |

**Crash-safe `_staged` → `{trackId}` promotion protocol** (zero split-brain):
1. **Before SQLite transaction commit**: hard-link (`File.CreateHardLink` / `link`, with atomic temp-copy fallback if cross-device) `{DataDir}/attachments/_staged/{id}.{ext}` → `{DataDir}/attachments/{trackId}/{id}.{ext}`.
2. **Inside `SqliteSingleWriter` transaction**: verify every attachment exists, matches ownership preconditions, update `track_id`, `session_id`, `storage_rel_path`, `status`, `bound_at_ms`, and (when binding to a message) insert `session_message_attachments`.
3. **Post-commit**: unlink `{DataDir}/attachments/_staged/{id}.{ext}` best-effort.
4. **On transaction rollback / failure**: remove newly linked `{DataDir}/attachments/{trackId}/{id}.{ext}` best-effort; the original `_staged` file remains intact for idempotent retry.
5. **On process crash between commit and post-commit unlink**: `{trackId}/{id}.{ext}` is already durable, and `MaintenanceRepository` sweeps the unreferenced `_staged` leftover on its next pass.

### 3.3 Queued Batch Promotion & Transcript Hydration

- **Sending (`SessionInputsRepository.PromoteQueuedBatchAsync`)**:
  - When multiple queued messages are combined into one turn, `PersistedSessionInput` carries `Attachments`: the ordered union across promoted `message_ordinal`s sorted by `(message_ordinal, position)` (deduplicated by `attachment_id`).
  - Prompt text joins non-empty contents (`string.Join("\n\n", messages.Select(m => m.Content).Where(c => c.Length > 0))`) so image-only messages do not introduce spurious blank lines; if all messages in the batch are image-only, `Prompt` is `""`.
- **Reading (`SessionRepository.ListSessionItemsAsync` & `SessionMessagesRepository.ListQueueAsync`)**:
  - Hydrate attachments in **one batched query** per page (`WHERE sma.message_ordinal IN @Ordinals ORDER BY sma.message_ordinal, sma.position`), avoiding N+1 queries and covering normal turns, promoted queue batches, AND steered messages (`message_ordinal` is populated for all three).

### 3.4 Orphan TTL Cleanup & O(1) Track Deletion

- **Track deletion (`TrackDeletionRepository.DeleteTrackAsync`)**:
  - `DELETE FROM tracks WHERE id = @TrackId;` cascades to `agent_sessions`, `session_messages`, `session_message_attachments`, and `session_attachments`.
  - Post-commit calls `AttachmentBlobStore.DeleteTrackAttachmentsDirectory(trackId)` → O(1) `Directory.Delete("{DataDir}/attachments/{trackId}", recursive: true)` (mirroring `ColdBlobStore.DeleteTrackBlobsDirectory`).
  - For SSH execution targets, best-effort invokes `env.Files.CleanupTrackAttachmentsAsync(trackId)` to remove `{WorkspacesRoot}/.perga-attachments/{trackId}`.
- **Maintenance GC (`MaintenanceRepository.RunMaintenancePassAsync`)**:
  1. **Expired staged rows (24h TTL)**: delete `session_attachments WHERE status = 'staged' AND created_at_ms < nowMs - 86_400_000` in batches of 250 (`RETURNING storage_rel_path`) inside `SqliteSingleWriter`, then unlink the returned files post-commit.
  2. **Unreferenced physical files on disk**: enumerate `attachments/_staged/*` and `attachments/{trackId}/*` older than a 10-minute grace period, re-verify against `session_attachments.storage_rel_path` on the `SqliteSingleWriter` queue (same two-phase race-free pattern as `SweepUnreferencedBlobsAsync`), unlink unreferenced files, and prune empty `{trackId}` directories.
  3. Report count via `DbMaintenanceStatsDto.ReclaimedAttachments` (default `0`).

---

## 4. Binary Storage, Sniffing, Quotas & Security Boundaries

### 4.1 Physical Storage (`AttachmentBlobStore` in `Perga.Storage.Sqlite`)

- Root directory: `SqliteStorageOptions.ResolveAttachmentsRootDir()` → `{DataDir}/attachments`.
- Attachment ID format: server-minted `att_{32 lowercase hex}` (`Guid.NewGuid():N`), strictly validated against `^att_[0-9a-f]{32}$`.
- Filename on disk: `{attachmentId}.{ext}` where `{ext}` is derived **solely** from server-sniffed MIME type (`.png`, `.jpg`, `.gif`, `.webp`), never from client headers or original filename.
- Atomic write: stream body to `.{attachmentId}.tmp.{nonce}` inside target directory while computing SHA-256 and enforcing byte limit; `File.Move(tmp, final, overwrite: false)` on completion; delete temp file on any error/cancellation.

### 4.2 Authoritative Server-Side Magic-Byte Sniffing

Client `Content-Type` is untrusted (only rejected upfront if explicitly non-binary JSON/text). The first 12 bytes of the uploaded stream determine the stored MIME type:

| Sniffed Format | Required Leading Bytes | Stored `content_type` | Disk Extension |
|---|---|---|---|
| PNG | `89 50 4E 47 0D 0A 1A 0A` | `image/png` | `.png` |
| JPEG | `FF D8 FF` | `image/jpeg` | `.jpg` |
| GIF | `47 49 46 38 37 61` (`GIF87a`) or `47 49 46 38 39 61` (`GIF89a`) | `image/gif` | `.gif` |
| WebP | `52 49 46 46` (`RIFF`) at 0..3 **and** `57 45 42 50` (`WEBP`) at 8..11 | `image/webp` | `.webp` |

- Empty body, SVG (`image/svg+xml`), HTML, BMP, ICO, AVIF, PDF, or mismatched bytes → **rejected `400 Bad Request`** (`code: "invalid_argument"`), temp file deleted, zero DB rows written.

### 4.3 Size & Quota Limits (Configurable via `SqliteStorageOptions` / `PergaHostOptions`)

| Limit | Default | Error Response |
|---|---|---|
| Single image max size | **10 MiB** (`10_485_760` bytes) | `413 Payload Too Large` (`code: "payload_too_large"`) — enforced streaming while reading `Request.Body` + endpoint `IHttpMaxRequestBodySizeFeature` |
| Max attachments per message / create | **8** | `400 Bad Request` (`code: "invalid_argument"`) |
| Session total attachment quota | **64 MiB** (`SUM(size_bytes)` per `session_id`) | `400 Bad Request` (`code: "attachment_quota_exceeded"`) |
| Unowned staged global quota (`session_id IS NULL`) | **64 MiB** and **64 items** | `400 Bad Request` (`code: "attachment_quota_exceeded"`) |
| Orphan staged TTL | **24 hours** (`86_400_000` ms) | Reclaimed by `MaintenanceRepository` |

*(Note: `400`, `403`, `404`, `413` align 1:1 with `PLANNER_ATTACHMENT_FAILURES` in `fe/core/domain/conversation-commands.ts`, where all four classify cleanly as `refused`.)*

### 4.4 Path Traversal, IDOR & Security Headers

- **Path containment**: validate `attachmentId` (`^att_[0-9a-f]{32}$`) and `trackId` (reject `.`, `..`, `/`, `\`, `\0`); verify `Path.GetFullPath(candidate)` starts with `AttachmentsRootPrefixWithSep`; reject symlinks (`FileAttributes.ReparsePoint` / `O_NOFOLLOW`).
- **Anti-IDOR**:
  - `GET /api/v1/sessions/{sessionId}/attachments/{attachmentId}` filters `WHERE id = @AttachmentId AND session_id = @SessionId`. Cross-session or unknown IDs uniformly return `404 Not Found`.
  - `GET /api/v1/attachments/{attachmentId}` filters `WHERE id = @AttachmentId AND status = 'staged' AND session_id IS NULL`. Once bound to a session, this route returns `404 Not Found` so bound session files cannot bypass session scoping.
  - All routes sit under `/api/v1`, inheriting Host authentication and `RequestOriginGuard`.
- **Mandatory response headers on every attachment `GET`**:
  - `Content-Type: <sniffed_content_type>`
  - `X-Content-Type-Options: nosniff`
  - `Content-Security-Policy: sandbox`
  - `Content-Disposition: inline`
  - `Cache-Control: private, max-age=31536000, immutable` (bound) / `private, no-store` (staged)
  - `ETag: "\"<sha256>\""` (with `If-None-Match` → `304 Not Modified`)

---

## 5. `/api/v1` Contract, Kernel, AgentKit, OAR Bridge & SSH Adaptation

### 5.1 HTTP Endpoints (`src/Perga.Host/Http/ApiV1Endpoints.cs`)

| Method & Path | Purpose | Request | Response |
|---|---|---|---|
| `POST /api/v1/sessions/{sessionId}/attachments` | Upload image scoped to existing session (requires open Track & unarchived session) | Raw binary stream (≤ 10 MiB), optional `X-Attachment-Filename` | `201 Created`: `{ attachmentId, contentType, size, url }` where `url = /api/v1/sessions/{sessionId}/attachments/{attachmentId}` |
| `GET /api/v1/sessions/{sessionId}/attachments/{attachmentId}` | Stream session-owned attachment (staged or bound) | — | `200 OK` / `304 Not Modified` / `404 Not Found` + security headers |
| `POST /api/v1/attachments` | Upload pre-creation staged image (`?trackId={trackId}` optional for New Session draft; omitted for New Track) | Raw binary stream (≤ 10 MiB), optional `X-Attachment-Filename` | `201 Created`: `{ attachmentId, contentType, size, url }` where `url = /api/v1/attachments/{attachmentId}` |
| `GET /api/v1/attachments/{attachmentId}` | Stream unowned staged attachment (`status='staged' AND session_id IS NULL`) | — | `200 OK` / `404 Not Found` + security headers |

### 5.2 Request & Response DTO Changes

All `/api/v1` changes MUST be mirrored synchronously in `src/Perga.Contracts/Contracts.cs`, `src/Perga.Kernel/`, `fe/core/api/perga-v1.ts` (note `.strict()` schemas!), and `fe/core/api/generated/openapi.json` (per root `AGENTS.md`).

- **New shared DTO (`Perga.Contracts`)**:
  ```csharp
  public sealed record SessionAttachmentDto(
      string Id,
      string ContentType,
      long Size,
      string Url);

  public sealed record UploadAttachmentResponseDto(
      string AttachmentId,
      string ContentType,
      long Size,
      string Url);
  ```
- **`SendSessionMessageRequest`** (`POST /api/v1/sessions/{sessionId}/messages`):
  - Add `IReadOnlyList<string>? AttachmentIds = null`.
  - `ApiV1Endpoints.cs`: require `Idempotency-Key` when `request.Content is not null || request.AttachmentIds is { Count: > 0 }`.
  - Allow image-only send (`string.IsNullOrWhiteSpace(request.Content)` is valid when `AttachmentIds is { Count: > 0 }`, normalized to `content = ""`).
  - Reject `AttachmentIds` when `MessageId is not null` (steering an already-queued message uses that queued message's own bound attachments).
  - Validate `AttachmentIds.Count <= 8`, no duplicate IDs, valid ID format, and ownership (`(staged && session_id == S)` OR `(bound && session_id == S)` for regenerate/re-send reuse).
  - Capability guard: if `AttachmentIds` is non-empty and the session/provider definitively reports `SupportsImages == false`, reject `409 precondition_failed`.
  - **Idempotency hash**: preserve existing `version = 4` hash when `AttachmentIds` is null/empty (rolling-upgrade safe); use `version = 5` including ordered `AttachmentIds` when non-empty.
- **`CreateSessionRequest`** (`POST /api/v1/tracks/{trackId}/sessions`):
  - Add `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null`.
  - Allow `Prompt` to be `null` or `""` when `AttachmentIds is { Count: > 0 }` (creates `firstInput` with `content = ""` and binds attachments atomically in `SessionInputsRepository.CreateAsync`).
  - Include `AttachmentIds` in both `session-create` `receiptIdentity` hash and `firstHash`.
  - Accept staged attachments with `session_id IS NULL AND (track_id IS NULL OR track_id == trackId)`, promoting into `{trackId}/` if needed.
- **`CreateTrackRequest`** (`POST /api/v1/tracks`):
  - Add `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AttachmentIds = null`.
  - Included in `CreateKey("track", ...)` hash when non-empty.
  - Validate `AttachmentIds.Count <= 8`, no duplicates, all in `status = 'staged' AND track_id IS NULL AND session_id IS NULL`.
  - Atomically promote blobs from `_staged/` → `{trackId}/` and adopt rows to `track_id = track.Id, session_id = leadSession.Id, status = 'staged'` inside `CreateTrackAsync` (so the subsequent initial send to `leadSessionId` binds them to the first message, and Track deletion cleans them in O(1)).
- **Read DTOs**:
  - `SessionItemDto`: add `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SessionAttachmentDto>? Attachments = null`.
  - `QueuedSessionMessageDto`: add `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SessionAttachmentDto>? Attachments = null`.
  - `SessionCapabilitiesDto`: add `bool SupportsImages = false`.
    - Resolution in `TrackActor.ReadCapabilitiesCore`:
      - If runtime handle is open: `handle.Agent.SupportsImages` (authoritative OAR `session.capabilities.images`).
      - If unopened: `EffectiveAgentRuntime.SupportsImages(session.Provider) != false` (returns `true` for `codex`/`claude` and handshake-determined `acp:antigravity`/`acp:opencode`; returns `false` for statically unsupported runtimes like `qoder`/`qwen`/`pi-durable`).
  - `ProviderStatus` & `TrackProviderEntryDto`: add `bool? SupportsImages = null` (`true` | `false` | `null` for handshake-determined) so New Track & New Session draft UIs can disable attachments when a statically image-incapable provider is selected.

### 5.3 `Perga.AgentKit` & `tools/perga-oar-bridge`

- **`Perga.AgentKit/Contracts.cs`**:
  - `public sealed record InputImageRef(string Path, string MediaType);`
  - `TurnInput`: add `IReadOnlyList<InputImageRef>? Images = null`.
  - `IAgentSession`:
    - `bool SupportsImages => false;`
    - `Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, IReadOnlyList<InputImageRef>? images, CancellationToken ct)` (default 3-arg overload delegates with `images: null`).
  - `ProviderStatus`: add `bool? SupportsImages = null`.
  - `IAgentRuntime`: add `bool? SupportsImages(string provider) => null;`.
- **`tools/perga-oar-bridge/src/bridge.ts` JSON-RPC extensions**:
  - `probe` response: add `supportsImages: typeof caps?.images === "boolean" ? caps.images : null`.
  - `session/open` response: add `supportsImages: session.capabilities.images === true`.
  - `session/prompt` params: add optional `images?: Array<{ path: string; mediaType?: string }>`; forward `{ inputId, ...(images.length > 0 ? { images } : {}) }` to `session.prompt(effectivePrompt, ...)`.
    - Note: when `this._pendingSystemPrompt` is prepended to an image-only prompt (`rawPrompt === ""`), `effectivePrompt` becomes `this._pendingSystemPrompt` while `images` still travel in `PromptOptions.images`.
  - `session/steer` params: add optional `images?: Array<{ path: string; mediaType?: string }>`; allow `message === ""` when `images.length > 0`; forward `images` to `session.steer(message, { inputId, ...(images.length > 0 ? { images } : {}) })`.
  - **Refusal mapping**: when OAR returns `{ kind: "rejected", code: "unsupported" | "error", reason }`, bridge already returns `{ status: "failed", code: outcome.code, message: outcome.reason, isRetryable: false, beforePrompt: true }` → `OarAgentSession` emits `TurnError(..., IsConclusive: true, BeforePrompt: true)` so the turn settles cleanly as failed with the exact runtime reason.

### 5.4 Local vs. SSH Execution Target Adaptation

Because OAR reads `InputImage.path` on the **runtime's host machine**:

- Extend `IExecutionFileSystem` (`src/Perga.Kernel/Ports/IExecutionEnvironment.cs`):
  ```csharp
  Task<string> StageAttachmentAsync(
      string trackId,
      string attachmentId,
      string extension,
      string localPhysicalPath,
      string sha256,
      CancellationToken ct = default) => Task.FromResult(localPhysicalPath);

  Task CleanupTrackAttachmentsAsync(string trackId, CancellationToken ct = default) => Task.CompletedTask;
  ```
- **Local (`LocalExecutionFileSystem`)**: returns `localPhysicalPath` directly (zero copy).
- **SSH (`SshExecutionFileSystem`)**:
  - Stages into `{_config.WorkspacesRoot}/.perga-attachments/{trackId}/{attachmentId}.{ext}` (outside user git worktrees so `git status` is never dirtied).
  - Validates `trackId`, `attachmentId` (`^att_[0-9a-f]{32}$`), `extension` (allowlist `.png`/`.jpg`/`.gif`/`.webp`), and `sha256` (64 lowercase hex).
  - Runs a single `python3` SSH helper that:
    1. Checks if target file already exists and matches `sha256` → returns canonical remote path immediately (idempotent across retries/re-sends).
    2. Otherwise reads binary bytes from SSH stdin into `.{attachmentId}.tmp.{nonce}` (`0600`), verifies SHA-256, atomically `os.replace()`s into final path, and prints canonical path.
  - Invoked by `SessionTurnRunner` (before `ObserveTurnAsync`) and `TrackActor.Steering` (before `SteerAsync`). If staging fails, the turn fails with `BeforePrompt: true` and never dispatches without images.

---

## 6. Frontend Unified Composer Paste & Lightbox Architecture

Strictly follows `fe/AGENTS.md` (`app → features → systems → ui → core`; no cross-feature imports; no `shared/` directory; no `index.*` barrels; `Object.freeze(...)` for static module arrays).

### 6.1 Layer Placement

| Layer | Files | Responsibility |
|---|---|---|
| `fe/core/api/` | `perga-v1.ts`, `generated/openapi.json` | Zod schemas & OpenAPI definitions for attachment upload/read DTOs, `attachmentIds`, `SessionItemDto.attachments`, `QueuedSessionMessageDto.attachments`, `SessionCapabilities.supportsImages`, `ProviderStatus.supportsImages`. |
| `fe/core/domain/` | `composer-attachments.ts` (new), `conversation-commands.ts`, `conversation-display.ts`, `conversation.ts`, `track.ts` | Pure draft attachment state machine (`uploading` \| `ready` \| `failed`, 8-item cap, 10 MiB pre-check, order preservation); binary upload `ApiOperation`s (`uploadSessionAttachmentOperation`, `uploadStagedAttachmentOperation` — leveraging existing `Uint8Array` support in `createFetchTransport` and header override in `performApiRequest`); wire projection for transcript items & queued messages. |
| `fe/web/src/ui/image-attachments/` | `clipboard-files.ts`, `attachment-strip.tsx`, `attach-button.tsx`, `public.tsx`, `image-attachments.module.css` | Domain-neutral UI primitives importable by `features/chat`, `features/planner`, `features/area`, and `app`: clipboard extraction (`onPasteCapture`), `useDropzoneHighlight`, `<ImageAttachButton>` (`<input type="file" multiple>`), `<DraftAttachmentStrip>` (Astryx `Thumbnail` + `useLightbox({ hasZoom: true })` + per-item Retry/Remove), `<AttachmentGallery>` (shared read-only thumbnail list + Lightbox). |
| `fe/web/src/features/` | `planner/attachments.tsx`, `chat/thread/public.tsx`, `chat/thread/message-entry.tsx`, `chat/thread/pending-queue.tsx`, `area/new-track/public.tsx` | Wire composer paste/drop/drawer/button slots without cross-feature imports; render `<AttachmentGallery>` in `MessageEntry` and `PendingQueue`; omit empty `<OutgoingMessage>` bubble when `text === '' && attachments.length > 0`; enable image-only queueing in `ChatComposer` `sendDoor`. |
| `fe/web/src/app/` | `providers/planner-mutations.ts`, `conversations/store.ts`, `conversations/outbox.ts`, `conversations/draft-actions.ts`, `conversations/registry-model.ts`, `router/conversation-pane.tsx`, `router/new-track-route.tsx`, `router/new-track-drafts.tsx`, `router/conversation-initial-send.ts` | Bind real upload mutations (session-scoped & staged), derive `attachmentsSupported` from `SessionCapabilities.supportsImages` / provider `supportsImages`, remove `busy` gating on `onFiles`, manage `ObjectURL` lifecycle across switches/unmounts, pass `attachmentIds` on send / create-session / create-track + initial send. |

### 6.2 Clipboard Paste, Rapid Multi-Paste & Drag-and-Drop

1. **Unified `onPasteCapture` (`clipboard-files.ts`)**:
   - Inspects `e.clipboardData.items` (`kind === 'file' && type.startsWith('image/')` → `getAsFile()`) merged and deduplicated with `e.clipboardData.files`, plus `plainText = e.clipboardData.getData('text/plain')`.
   - **Pure image(s)** (`images.length > 0 && plainText === ''`): `preventDefault()` + `stopPropagation()` → enqueue all images. Never pastes rich HTML `<img>` tags into `contentEditable`.
   - **Mixed text + image(s)** (`images.length > 0 && plainText !== ''`): `preventDefault()` + `stopPropagation()` → enqueue all images **and** insert `plainText` at caret via `ChatComposerInputHandle.insertText(plainText)` (with controlled-draft fallback).
   - **Non-image files only** (`images.length === 0 && otherFiles.length > 0 && plainText === ''`): `preventDefault()` + `stopPropagation()` → show refusal banner (`PNG, JPEG, GIF or WebP only`).
   - **Pure text**: untouched in capture phase; Astryx native paste / `pasteAsToken` runs normally.
2. **Rapid sequential paste (fixing dropped pastes during `busy`)**:
   - **Never** set `onFiles={undefined}` or disable `<ImageAttachButton>` merely because `busy` is true (`conversation-pane.tsx:452`, `attachments.tsx:155`). Only gate on `!support.available || disabled || atCapacity` (where `atCapacity` counts `ready + uploading >= 8`).
   - Each pasted/picked `File` synchronously allocates a `localId`, creates an `ObjectURL`, appends an `uploading` slot in exact paste order, and queues into the scope's upload lane (serial or bounded concurrency = 2, preserving slot order by `localId`).
3. **Drag-and-drop & multi-select**:
   - `useDropzoneHighlight()` attaches to the composer shell (desktop & mobile) with an enter/leave depth counter, rendering a semantic accent ring (`--color-accent`) while dragging files and calling `preventDefault()` on drop everywhere inside the composer.
   - `<input type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple />` passes all selected files to `attachMany(Array.from(files))` and resets `event.target.value = ''`.

### 6.3 Optimistic `ObjectURL` Lifecycle, Retry & Send Guards

- **Optimistic preview**: `URL.createObjectURL(file)` is created synchronously at enqueue time so `<Thumbnail src={item.previewUrl} isLoading />` shows the picture + spinner immediately.
- **Strict `URL.revokeObjectURL` contract**:
  - Tracked in a per-hook/per-store `Set<string>` of live object URLs.
  - Revoked deterministically when: (a) user clicks Remove on the item, (b) the message/draft/track is sent and attachments are released, (c) draft is discarded, or (d) owning provider/hook unmounts.
  - While `status === 'ready'` in the composer, keeping `previewUrl` until send/remove avoids a flicker swap to the remote URL; the `File` reference is dropped immediately upon `ready` to release raw file memory, and retained only while `uploading` or `failed` to power in-place Retry.
- **Per-item Retry & Remove**:
  - Failed items remain visible in `<DraftAttachmentStrip>` with their local thumbnail, error state, an inline **Retry** button (re-enqueues the retained `File` in place), and a **Remove** button (clears the slot and revokes its `ObjectURL`).
  - Removing an `uploading` item marks its `localId` cancelled (aborts or ignores late response so a deleted image never reappears).
- **Send guards**:
  - `hasUploading`: disables send / Enter submit and surfaces inline status `"图片上传中，请稍候…"` so unfinished images are never silently omitted.
  - `hasFailed`: disables send / Enter submit with clear error notice prompting Retry or Remove.
  - `allowEmptyText`: true whenever `readyAttachments.length > 0 && !hasUploading && !hasFailed` across all 4 surfaces (including image-only queueing while running in `ChatComposer`, image-only New Session draft in `draft-actions.ts`, and image-only New Track creation in `NewTrackForm`).

### 6.4 Full-Chain Lightbox Click-to-Zoom

All image views use `@astryxdesign/core/Lightbox` (`useLightbox({ media, hasZoom: true })`):

1. **Composer Drawer** (`<DraftAttachmentStrip>` on Existing Session desktop/mobile, New Session draft, New Track page):
   - Clicking any non-loading thumbnail opens gallery Lightbox at that item's index (`media` built from preview/canonical URLs of clickable items).
   - Clicking `×` remove button stops propagation (`Thumbnail` built-in) and never opens Lightbox.
2. **Sent Messages (`MessageEntry`)**:
   - Consumes `attachments` hydrated from `SessionItemDto.attachments` (and optimistic echoes in `outbox.ts`). Clicking any thumbnail opens Lightbox with zoom/pan and prev/next navigation.
3. **Queued & Sending Messages (`PendingQueue`)**:
   - Renders `<AttachmentGallery>` for both `PendingQueueEntry.attachments` and optimistic `PendingQueueSend.attachments`, supporting click-to-zoom Lightbox and image-only queued items.
4. **New Session Draft Echo (`conversation-pane.tsx`)**:
   - Renders `<AttachmentGallery>` beside `<OutgoingMessage>` while the first turn is creating.

---

## 7. Verification Matrix

All checks below run on the final tree from the repository root (`AGENTS.md` L2 requirement):

| Layer | Scope & Key Assertions | Command |
|---|---|---|
| **.NET Unit & SQLite** | Migration `0026` checksum & idempotency; `AttachmentBlobStore` magic-byte sniffing (PNG/JPEG/GIF/WebP pass; SVG/HTML/empty/corrupt reject); 10 MiB / 64 MiB quotas; path traversal rejection; crash-safe `_staged`→`{trackId}` promotion; restart durability (reopen SQLite DB in fresh host & read hydrated items/queue); `PromoteQueuedBatchAsync` attachment ordering; Track deletion O(1) dir removal; `MaintenanceRepository` 24h staged TTL & disk orphan sweep. | `dotnet test tests/Perga.Storage.Tests/Perga.Storage.Tests.csproj` |
| **.NET Kernel & Host Integration** | `POST/GET /api/v1/sessions/{sid}/attachments` & `POST/GET /api/v1/attachments`; cross-session IDOR → `404`; bound attachment on `/api/v1/attachments/{id}` → `404`; `nosniff` + `CSP: sandbox` + `Cache-Control` headers; `413` on >10 MiB stream; image-only `SendSessionMessage` / `CreateSession` / `CreateTrack`; idempotency hash replay & conflict (`409`); `SessionCapabilitiesDto.supportsImages` (open vs unopened vs unsupported); SSH `StageAttachmentAsync` idempotency & fail-closed before prompt. | `dotnet test Perga.sln` (focused: `tests/Perga.Kernel.Tests`, `tests/Perga.Host.IntegrationTests`, `tests/Perga.AgentKit.Tests`) |
| **OAR Bridge** | `probe` tri-state `supportsImages` (`true`/`false`/`null`); `session/open` authoritative `supportsImages`; `session/prompt` & `session/steer` forwarding `images[]` (including empty prompt); OAR `unsupported` refusal propagation (`beforePrompt: true`); bundle build. | `(cd tools/perga-oar-bridge && npm run typecheck && npm test && npm run build)` |
| **Frontend Static & Unit** | Architecture gates (`depcruise`, `no-module-runtime-state`, CSS/contrast, ESLint zero warnings); `composer-attachments.ts` state machine (8-item cap, rapid paste order, retry/remove, send guards); `conversation-display.ts` item & queue attachment projection; `perga-v1.ts` Zod strict decode; `ObjectURL` revoke accounting. | `(cd fe && npm run lint && npm run build && npm test)` |
| **Frontend Browser (Chromium Desktop + Coarse/Mobile)** | Paste pure screenshot, mixed text+image (text preserved, no HTML), rapid multi-image paste while uploading, drag-and-drop highlight, `<input multiple>`; optimistic thumbnail → spinner → ready; upload failure → retry succeeds / remove revokes URL; send blocked while uploading; image-only send across Existing Session (desktop & mobile), New Session draft, and New Track; click thumbnail → Lightbox opens, `ArrowLeft`/`ArrowRight` navigates, zoom toggles, `Esc` closes, clicking `×` removes without opening Lightbox; queued & sent message Lightbox. | `(cd fe && npm run test:browser)` |
