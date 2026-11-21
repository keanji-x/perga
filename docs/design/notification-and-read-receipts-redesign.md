# Perga 统一通知、状态指示器与已读回执设计文档

> **关联**：[Issue #92](https://github.com/keanji-x/perga/issues/92) · [PR #98](https://github.com/keanji-x/perga/pull/98) · 配合 [对话状态机与 5 区 UI 重构设计](conversation-state-and-ui-redesign.md)
> **目标**：彻底清除 Neige 遗留通知体系与前端本地假未读双轨逻辑，围绕 Perga 原生 5 态对话状态机建立**单一真相源、跨端一致、语义纯粹**的通知与状态指示体系。

---

## Delivery boundary

This delivery implements the backend and frontend in sections 3–5: durable receipts and transient events, actor/store overlays, published frontend schemas/OpenAPI, unified accessible indicators, view-driven acknowledgements, and removal of the floating notification system. Workspace overlay reads include closed tracks so their unread watermarks survive. `TrackStatusDto.UnreadMailCount` remains available to MCP.

Manual unread clears once when a view is first presented for a visit (including initial receipt readiness), never in response to receipt cache changes. Failed acknowledgements remain owed until an external retry trigger; cache reconciliation cannot cause a POST loop.

Final backend details:

- The migration is forward-only; migrations 0001–0019 remain byte-frozen. The baseline uses SQLite wall-clock Unix milliseconds and is seeded once, including on upgrades.
- `MarkReadRequest.ReadThroughMs` is nullable; omitted or null means zero, clearing manual unread without advancing an existing watermark. Negative values, unsupported kinds, blank target IDs, and invalid JSON bodies return 400. Receipts are keyed by opaque target IDs without requiring the target to exist.
- Workspace phases follow the existing stored JSON numeric `WorkspacePreparationPhase` contract (`intent_json.Phase`), rather than adding a phase column: Pending/Provisioning/Mounting/Initializing/Provisioned count as working; Failed/Unknown count as failed.
- A blocked worker requires a bound attempt whose `finished_at_ms` is null. Workers never enter `NotifiableSessions`; their own session watermark still follows the universal completed/failed turn rule. Compaction turns stored as completed also follow that rule. Interrupted and unknown turns do not advance it, and resets preserve the historical maximum.
- Active actor overlays are read through the actor mailbox. Pending approvals are separate from open asks; actor workspace state takes precedence over SQLite. Closed actor tracks clear working, red and yellow fields while preserving session watermarks.
- Receipt writes use the SQLite single writer transaction; only successful committed writes publish transient `read_receipt.updated` events. These events have no durable event ID and are not replayed. Clients reconcile using GET after reconnect.

## 1. 背景与现存根因

在统一对话状态机（`Session.state ∈ {idle, running, stopping, compacting, unknown}` + 单槽 Action Area）后，外层通知与小圆点系统暴露出 6 个结构性问题：

1. **蓝点未读失真且无法跨端同步**：生产环境 `ReadReceiptsProvider` 走 `localOnly={pergaMode}`（存 `localStorage`，`baselineMs = 0`），后端无持久化；Track/Conversation 高水位均误用了 `updatedAtMs`（含改名、Pin、自己发消息、Actor 心跳），且列表与详情高水位口径不同。
2. **新对话状态机对外层失明**：后端 `ListTrackOverlaysAsync` 只查 `queued | running`，漏掉 `stopping`、`compacting` 与 `unknown`；前端只在当前已打开对话上打了 `local.stalled` 补丁，切换/关闭即消失。
3. **红黄优先级与领域模型错乱**：`tasks.status = 'failed'` 被当成 Track 级红点（但 Perga 中 Task 失败会自动 `PokeLeadAsync` 交由 Lead 消化，历史失败行永远留库导致 Track 终身挂红点）；同时 `track.ts` 又写成 `openAsk > failed`，与 `activityStateOf` 的 `failed > attention` 反向打架。
4. **右下角悬浮通知铃铛半残冗余**：与对话内 Action Area 重复；有独立第二套 `localStorage` 已读状态；`×` 关闭按钮打的是后端不存在的 404 接口 `/api/tracks/{id}/activity/dismissals`；`task-failed` 在面板内被误画成黄点；残留死类型 `planner_down` / `planner_turn_timeout`。
5. **侧边栏实时刷新断链**：`fe/core/events/invalidation-plan.ts` 中 `session.updated` / `turn.completed` / `ask.changed` / `approval.changed` / `task.updated` / `track.updated` 均未 invalidate `overlaysByKind('track')`，导致侧边栏点位更新滞后。
6. **分组口径分裂**：桌面 `Focus`、移动端 `waiting/running/unread` 与 `Today` 总览各自拼接过滤条件。

---

## 2. 第一性原理与 4 态统一模型

全站每一个指示器位（侧边栏 Track、移动端 Track、Track 标题、对话列表、Report 视图对话入口）共用唯一词汇表与严格优先级：

```
🔴 failed (异常阻断)  >  🟡 attention (等你操作)  >  ⚪ working (进行中)  >  🔵 unread (未读)  >  quiet (无点)
```

| 视觉 | Token / 状态名 | 核心语义 | 谁负责消除 | 权威来源 |
|---|---|---|---|---|
| 🔴 红点 | `failed` | 系统/会话卡死，**必须你手动点修复**才能继续 | **修复动作**（看一眼不消） | 1. 活跃未归档 `Session.state === 'unknown'`<br>2. Track `workspacePreparation ∈ {'failed', 'unknown'}` |
| 🟡 黄点 | `attention` | Agent 正常暂停，**等你决策或授权** | **提交操作**（看一眼不消） | 1. 未关闭 Track 的 `Ask.status === 'open'`<br>2. 内存中活跃 `PendingApproval` |
| ⚪ 灰转圈 | `working` | 后台正在干活，**不用你管** | **自动结束** | 1. 未归档 `Session.state ∈ {'queued', 'running', 'stopping', 'compacting'}`<br>2. `Task.status ∈ {'running', 'verifying'}`<br>3. `workspacePreparation ∈ {'pending', 'provisioning', 'mounting', 'initializing'}` |
| 🔵 蓝点 | `unread` | 刚完成一轮 Agent 产出，**你还没看** | **查看对话即消**（手动标未读同理） | 1. 未归档、非 Worker 对话有 `lastNotifiableTurnAtMs > max(baselineMs, readThroughMs)`<br>2. 用户手动 `manualUnread = true` |

> **已关闭 Track (`closed_at_ms != null`) 规则**：不再上报红点、黄点或运转态（`hasBlockedSession = false`、`workspaceFailed = false`、`openAskCount = 0`、`pendingApprovalCount = 0`、`isWorking = false`）；仅保留未读蓝点（若有未读产出或手动标未读）。

### 明确排除的来源（不再作为通知）

- ❌ **`Task.status === 'failed'`**：Task 失败由内核自动唤醒 Lead 消化，仅在 Task/Progress 列表中作为该 Task 自身的执行状态显示，**不再**拉红 Track 或弹出通知。
- ❌ **单轮 `Turn.status === 'failed'`（但 `Session.state === 'idle'`）**：Session 健康可继续对话；此轮失败属于“本轮已结束待你查看”，归入 **🔵 蓝点（未读）**，打开对话在底部 Turn 行可见红色失败回执，看过即消。
- ❌ **主动中断 `Turn.status === 'interrupted'`**：用户自己点 `/stop` 打断的 Turn 不触发未读。
- ❌ **后台 Task 自动拉起的 Worker Subagent (`bound_task_id IS NOT NULL`)**：其 Turn 结束回报给 Lead，**不**直接触发 Track 未读蓝点（若 Worker 产生 `PendingApproval` 则正常触发黄点；若 Worker 自身进入 `unknown` 且未归档且所属尝试仍活跃可按常规 session 归类，已结束/归档不触发）。
- ❌ **`planner_down` / `planner_turn_timeout` / `UnreadMailCount`**：彻底移除。
- ❌ **改名、Pin、归档、换模型/权限、用户发消息/排队、Actor 心跳、`/reset`**：绝不推进未读高水位。

---

## 3. 后端架构设计（SQLite + Kernel + Host）

### 3.1 SQLite 前向迁移：`0020_read_receipts.sql`

严格保持 `0001`–`0018` 与 `0019_session_unknown_state.sql` 字节冻结，新增 `src/Perga.Storage.Sqlite/Migrations/0020_read_receipts.sql`：

```sql
CREATE TABLE read_receipt_meta (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    baseline_ms INTEGER NOT NULL CHECK (baseline_ms >= 0)
);

INSERT INTO read_receipt_meta (id, baseline_ms)
VALUES (1, CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER));

CREATE TABLE read_receipts (
    kind TEXT NOT NULL CHECK (kind IN ('track', 'conversation')),
    target_id TEXT NOT NULL,
    read_through_ms INTEGER NOT NULL DEFAULT 0 CHECK (read_through_ms >= 0),
    manual_unread INTEGER NOT NULL DEFAULT 0 CHECK (manual_unread IN (0, 1)),
    updated_at_ms INTEGER NOT NULL,
    PRIMARY KEY (kind, target_id)
);
```

- **升级零噪音保证**：迁移执行时初始化 `baseline_ms = 当前 Unix 毫秒`，存量数据库升级后不会让历史 Track/对话瞬间全亮蓝点；任何升级后新完成的 Turn 均有 `completed_at_ms > baseline_ms`，正常触发蓝点。
- **单调幂等语义**：
  - `MarkReadAsync(kind, targetId, readThroughMs)`：`ON CONFLICT(kind, target_id) DO UPDATE SET read_through_ms = MAX(read_receipts.read_through_ms, excluded.read_through_ms), manual_unread = 0, updated_at_ms = excluded.updated_at_ms`。
  - `MarkUnreadAsync(kind, targetId)`：`ON CONFLICT(kind, target_id) DO UPDATE SET manual_unread = 1, updated_at_ms = excluded.updated_at_ms`（保留已有 `read_through_ms`）。

### 3.2 Contracts 变更（`src/Perga.Contracts/Contracts.cs`）

1. **新增已读回执 DTO**：
   ```csharp
   public sealed record ReadReceiptDto(
       string Kind,
       string TargetId,
       long ReadThroughMs,
       bool ManualUnread);

   public sealed record ReadReceiptsResponseDto(
       long BaselineMs,
       IReadOnlyList<ReadReceiptDto> Receipts);

   public sealed record MarkReadRequest(long? ReadThroughMs = null);
   ```

2. **扩展 `AgentSessionDto`**：
   - 增加可选字段 `long? LastNotifiableTurnAtMs = null`（JSON: `lastNotifiableTurnAtMs`）。
   - 定义：该 Session 下 `LOWER(status) IN ('completed', 'failed') AND completed_at_ms IS NOT NULL` 的 `MAX(completed_at_ms)`。
   - **一致性要求**：
     - SQLite 读取（`GetTrackAggregateSnapshotAsync`、`ListSessionsAsync`、`GetSessionAsync` 等所有 `MapSession` 查询）均带出 `LastNotifiableTurnAtMs`。
     - `TrackActor` 内存缓存 `_sessions[sessionId]` 在 Turn 以 `completed` 或 `failed` 结束落库后同步更新 `LastNotifiableTurnAtMs = Math.Max(existing ?? 0, completedAtMs)`，确保 `GetSnapshotAsync`、`ListSessionsAsync` 及 `SessionUpdated` 事件立即反映最新高水位。

3. **重构 `TrackOverlayDto`**：
   ```csharp
   public sealed record NotifiableSessionWatermarkDto(
       string SessionId,
       long LastNotifiableTurnAtMs);

   public sealed record TrackOverlayDto(
       string TrackId,
       string AreaId,
       bool IsWorking,
       int OpenAskCount,
       int PendingApprovalCount,
       bool HasBlockedSession,
       bool WorkspaceFailed,
       long UpdatedAtMs,
       IReadOnlyList<NotifiableSessionWatermarkDto>? NotifiableSessions = null);
   ```
   - 移除 `TrackOverlayDto.HasFailedTask` 与 `TrackOverlayDto.UnreadMailCount`（注意：`TrackStatusDto.UnreadMailCount` 仍为 MCP `track_status` 保留，不受影响）。
   - `ListTrackOverlaysAsync` 计算口径：
     - `closed_at_ms IS NOT NULL` 时：`IsWorking = false`、`OpenAskCount = 0`、`PendingApprovalCount = 0`、`HasBlockedSession = false`、`WorkspaceFailed = false`。
     - `closed_at_ms IS NULL` 时：
       - `IsWorking`：存在未归档 `agent_sessions` 处于 `('queued', 'running', 'stopping', 'compacting')`，或 `tasks` 处于 `('running', 'verifying')`，或 `track_workspace_preparations.intent_json.Phase` 处于 `Pending / Provisioning / Mounting / Initializing / Provisioned`（数值 `0, 1, 2, 3, 7`）。
       - `HasBlockedSession`：存在未归档 `agent_sessions` 处于 `status = 'unknown'` 且（`bound_task_id IS NULL` 或对应 `bound_attempt_id` 的 `finished_at_ms IS NULL`）。
       - `WorkspaceFailed`：`track_workspace_preparations.intent_json.Phase` 为 `Failed / Unknown`（数值 `4, 5`）。
       - `OpenAskCount`：`asks` 中 `LOWER(status) = 'open'` 的数量。
       - `NotifiableSessions`：单次查询加载所有满足 `s.archived_at_ms IS NULL AND s.bound_task_id IS NULL` 且 `MAX(t.completed_at_ms WHERE LOWER(t.status) IN ('completed', 'failed') AND t.completed_at_ms IS NOT NULL) IS NOT NULL` 的 `(trackId, sessionId, lastNotifiableTurnAtMs)` 分组挂载。
     - `PergaKernelService.GetOverlaysAsync` 与活跃 `TrackActor` 内存态合并（在 actor mailbox 中 hydrate 后读取；已关闭 Track 清除工作、红点和黄点字段，保留会话高水位）：
       - `IsWorking` 由 actor 的运行中会话、任务及 `_workspacePreparation.Phase` 计算；工作区工作阶段与 SQLite 相同，不保留旧库标记
       - `OpenAskCount` 由 actor 的 open asks 计算；`PendingApprovalCount = actor.PendingApprovalCount`
       - `WorkspaceFailed` 由 actor 的 `_workspacePreparation.Phase is Failed or Unknown` 计算（内存权威优先于旧库状态）
       - `HasBlockedSession` 由 actor 的未归档 unknown 会话及活跃 attempt 计算，不保留旧库标记

### 3.3 REST API 与实时事件

1. **新增 `/api/v1/read-receipts` 路由（`src/Perga.Host/Http/ApiV1Endpoints.cs`）**：
   - `GET /api/v1/read-receipts` → `200 OK` 返回 `ReadReceiptsResponseDto`
   - `POST /api/v1/read-receipts/{kind}/{targetId}/read` → body `MarkReadRequest`（校验 `kind ∈ {'track', 'conversation'}`，`targetId` 非空，`readThroughMs >= 0`；非法参数返回 `400`），写入后返回 `200 OK` `ReadReceiptDto`
   - `POST /api/v1/read-receipts/{kind}/{targetId}/unread` → 同上校验，写入后返回 `200 OK` `ReadReceiptDto`
   - 空目标路径由 `/read-receipts/{kind}/{**emptyTarget:regex(^/(?:read|unread)$)}` 单独匹配并返回 `400`，不捕获其他未知路径。
2. **新增跨端实时同步事件 `read_receipt.updated`**：
   - `KernelEventKinds.ReadReceiptUpdated = "read_receipt.updated"`（加入 `TransientKinds`）。
   - `MarkReadAsync` / `MarkUnreadAsync` 在 SQLite 事务提交成功后，通过 `_eventBus.PublishTransient(areaId: null, trackId: kind == "track" ? targetId : null, KernelEventKinds.ReadReceiptUpdated, JsonHelpers.ToElement(receipt), nowMs)` 广播到全局（无 Track 过滤）WebSocket 订阅。Track 过滤订阅仅接收相同 `trackId` 的 Track 回执，不接收 `trackId: null` 的 Conversation 回执；需要跨 Track/Conversation 同步的客户端必须使用全局订阅。
   - **安全/隔离**：读回执写入失败时不广播事件；客户端断线重连或窗口 refocus 时仍会全量 `GET /api/v1/read-receipts` 兜底对齐。

---

## 4. 前端架构设计（Core + Events + Web UI）

### 4.1 契约与事件失效（冻结路径，需带 `#92` `OWNERSHIP-CHANGE` trailer）

1. **`fe/core/api/perga-v1.ts` & `fe/core/api/schemas.ts` & `fe/core/api/generated/openapi.json`**：
   - `agentSessionDtoSchema` 增加 `lastNotifiableTurnAtMs: z.number().nullish().transform(v => v ?? null)`。
   - `trackOverlayItemDtoSchema` 更新为：`trackId`, `areaId`, `isWorking`, `openAskCount`, `pendingApprovalCount`, `hasBlockedSession`, `workspaceFailed`, `updatedAtMs`, `notifiableSessions: z.array(z.object({ sessionId: z.string(), lastNotifiableTurnAtMs: z.number() })).default([])`。
   - `readReceiptDtoSchema` / `readReceiptsResponseDtoSchema` 接入 `/api/v1/read-receipts`。
   - `pergaTransientEventKindSchema` & `decodePergaEventEnvelope` & `decodeWireEvent` 新增 `read_receipt.updated`。
2. **`fe/core/events/query-keys.ts` & `fe/core/events/invalidation-plan.ts`**：
   - 将 `readReceipts = () => ['read-receipts'] as const` 收入 `core/events/query-keys.ts` 并复用至 `app/providers/query-keys.ts`。
   - 补齐 `overlaysByKind('track')` 实时失效：
     - `track.updated`、`session.updated`、`turn.completed`、`ask.changed`、`approval.changed`、`task.updated` 全部追加 invalidate `eventKeys.overlaysByKind('track')`！
   - 新增 `'read_receipt.updated'` 策略：invalidate `[eventKeys.readReceipts()]`。

### 4.2 Domain 唯一真相源（`fe/core/domain/`）

1. **`fe/core/domain/read-receipts.ts`**：
   - 切换 API 路径至 `/api/v1/read-receipts`、`/api/v1/read-receipts/${kind}/${id}/read`、`/api/v1/read-receipts/${kind}/${id}/unread`。
   - 新增纯函数 `isTrackUnread(state, trackId, notifiableSessions)`：
     ```ts
     export function isTrackUnread(
       state: ReadState,
       trackId: string,
       notifiableSessions: readonly NotifiableSessionWatermark[],
     ): boolean {
       if (state.receipts[receiptKey('track', trackId)]?.manualUnread === true) return true;
       return notifiableSessions.some((s) => isUnread(state, 'conversation', s.sessionId, s.lastNotifiableTurnAtMs));
     }
     ```
2. **`fe/core/domain/activity.ts`**：
   - 删除死代码：`dismissActivityItemOperation`、`DISMISS_FAILURES`、`DISMISS_TEXT`、以及 `planner_down` / `planner_turn_timeout` / `task_failed` 通知类型。
   - 保持唯一 4 态优先级：`failed > attention > working > unread > quiet`。
   - 修正可读语义标签，确保 `failed`（阻断异常）与 `attention`（等你操作）语义清晰不倒置。
3. **`fe/core/domain/track.ts`**：
   - `TrackActivity` 使用 `notifiableSessions: readonly NotifiableSessionWatermark[]`；删除 `activityAt`、`progress`、`eta`、`now` 与 `NotificationSource`。`trackOverlaysOperation()` 直接读取原生 `TrackOverlayItemDto[]`。
   - `trackActivityFromOverlay(item)` / `trackActivitiesFromOverlays(items)` 与 `trackActivityFromSnapshot(snapshot)` **100% 统一计算规则**：
     - **红点优先于黄点**：`attention = (hasBlockedSession || workspaceFailed) ? 'failed' : (openAskCount + pendingApprovalCount > 0) ? 'input' : 'none'`（彻底修复原先 `openAsk > failed` 反向覆盖 Bug！）。
     - `working`：包含 `queued | running | stopping | compacting`、`task running | verifying`、`workspacePreparation === 'pending'`。
     - `cards`（每条对话的状态映射）：
       - `session.status === 'unknown'`（且未归档，Worker 还须绑定当前运行/验证任务的最新 attempt）→ `'failed'`；不依赖该对话是否打开
       - 该会话存在 `pendingApproval` 或 `openAsk` → `'input'`（注：若同会话同时 `unknown`，按 `failed > input` 保持 `'failed'`）
       - `session.status ∈ {'queued', 'running', 'stopping', 'compacting'}` → `'working'`
     - `notifiableSessions`：过滤 `s.archivedAtMs == null && s.boundTaskId == null && s.lastNotifiableTurnAtMs != null`，输出 `{ sessionId, lastNotifiableTurnAtMs }`。
     - Track 活动直接投影原生 DTO；`OverlayWire` / `trackOverlayPayload` 仅供插件 report-view live tables，不再伪造 Track 通知载荷。
4. **`fe/core/domain/conversation.ts`**：
   - `agentSessionDtoToConversation`：`lastTurnCompletedAt: dto.lastNotifiableTurnAtMs ?? null`（彻底停止误用 `dto.updatedAtMs`！）。

### 4.3 已读回执 Provider 与消除时机（`fe/web/src/app/`）

1. **`fe/web/src/app/providers/read-receipts.tsx`**：
   - 彻底移除 `localOnly` 分支与 `preferences.readReceiptsJson()` 本地持久化。
   - 生产环境统一走 `/api/v1/read-receipts`（带写入串行化、乐观更新、离线重试队列、以及 `read_receipt.updated` 事件跨端同步）。
   - 提供 `isTrackUnread(trackId, notifiableSessions)`、`hasManualUnread('track', trackId)` 辅助方法。
2. **`fe/web/src/app/providers/ui-preferences.tsx`**：
   - 删除 `readReceiptsJson` / `setReadReceiptsJson` 与 `trackNotificationRead` / `setTrackNotificationRead`。
3. **精准消除时机**：
   - **对话未读消除**（`router/public.tsx` 中 `useConversationPane`）：当某对话抽屉可见（`panePresented && threadPresented && store.historyReady && !store.historyLoading && store.historyError === null`）且页面 `document.visibilityState === 'visible'`，且 `openActivity.lastTurnCompletedAt !== null`（或该对话存在 `manualUnread`）时，调用 `markRead('conversation', sessionId, lastTurnCompletedAt)`；若当前 Track 存在 `manualUnread`，一并清除 Track 的 `manualUnread`。
   - **Track 手动未读消除**（`TrackReportRegion`）：打开 Track 页面时，**仅当** `receipts.hasManualUnread('track', trackId)` 为 `true` 时调用 `markRead('track', trackId, 0)` 清除手动标记；**绝不**自动消掉尚未打开的对话未读！

### 4.4 砍掉右下角悬浮铃铛 & 统一 UI 入口（`fe/web/src/features/track/` & `app/shell/`）

1. **彻底删除右下角悬浮通知面板**：
   - 移除 `fe/web/src/features/track/page/public.tsx` 中的 `inputNotifications`、`TrackInputNotification`、`needsInputNotice` 浮层、未读数徽章、`onDismiss`、`onNotificationReadChange` 与 `notificationAnnouncement`。
   - 删除 `fe/web/src/features/track/page/notification.module.css`。
   - 移除 `fe/web/src/app/providers/queries.ts` 中 `dismissActivityItem` mutation。
   - 移除 `fe/web/src/app/router/public.tsx` 中 `trackNotifications`、`notificationIsRead`、`inputNotifications` 组装逻辑。
2. **Report 视图下的轻量统一入口**：
   - 当 Track 页对话抽屉关闭时，顶部“讨论这项工作”按钮（桌面端）与底部聊天入口（移动端）旁展示当前 Track 对话层的最高优先级 `ActivityIndicator`（红 > 黄 > 灰转圈 > 蓝）。
   - 点击按钮时智能打开**最需要处理的对话**（优先级：`unknown` 会话 → 有 `Approval`/`Ask` 会话 → 未读对话 → Lead 会话），直接进入该对话底部的 Action Area 或最新消息。
3. **统一全站所有分组与标题口径**：
   - `TrackPage` 标题旁 `ActivityIndicator` 使用真实 `isTrackUnread(track)`（不再硬编码 `false`），与侧边栏保持 100% 一致。
   - `Today / PergaOverview`（`fe/web/src/app/router/perga-overview.tsx`）与侧边栏 `waiting` 统一口径：`track.closedAt === null && (hasFailed(track) || needsUserAttention(track))`（红 + 黄）。
   - 对话列表 `ChatList`（`router/public.tsx`）直接由 `track.cards` + `unreadIds` 驱动（审批和 Ask 已投影入 `cards`），移除仅限打开态的 `local.stalled` 补丁，使符合阻断条件的 `unknown` 对话无论是否打开都稳定显示红点。
   - 清理 `session-header.tsx` 颜色冲突：`running` / `compacting` 徽章改用 `neutral`（配合运转态语义）而非 `info` 蓝色，让蓝色在全站明确归属“未读/主交互”。

---

## 5. 验证与评审标准（L2）

因涉及 SQLite 新迁移 `0020`、`/api/v1` 契约、后端 Kernel/Host 持久化与跨端实时事件、以及前端多组件清理，按 `AGENTS.md` 执行 **L2 审查与验证**：

1. **后端门禁（`dotnet test Perga.sln`）**：
   - 迁移 `0020_read_receipts.sql` 单测：升级建表初始化 `baseline_ms`、历史迁移 `0001`–`0019` 字节不变、`MarkRead` 单调递增清 `manualUnread`、`MarkUnread` 置位保留 `readThroughMs`、跨重启持久化。
   - `TrackOverlayDto` & `AgentSessionDto.LastNotifiableTurnAtMs` 集成测试：
     - `completed` / `failed` Turn 推进 `LastNotifiableTurnAtMs`；`interrupted` / 发消息 / 改名 / Pin / 归档 / Worker Subagent 不推进。
     - `Session.state === 'unknown'` 与 `workspacePreparation === 'failed' | 'unknown'` 触发红点；修复后消失；`Task.status === 'failed'` 不触发红点。
     - `stopping` / `compacting` / `workspacePreparation === 'pending'` 触发 `IsWorking`。
     - `Ask` / `PendingApproval` 触发黄点，解决后消失；已关闭 Track 不报红/黄/运转态。
   - Host HTTP + WebSocket 集成测试：`GET/POST /api/v1/read-receipts` 校验与 `read_receipt.updated` 事件广播。
2. **前端门禁（`npm run lint && npm run build && npm test` + 浏览器测试）**：
   - `isTrackUnread`、多对话独立未读聚合、手动未读与自动消除边界单测。
   - `invalidation-plan.ts` 确认 `session.updated` / `turn.completed` / `ask.changed` / `approval.changed` / `task.updated` / `track.updated` 均失效 `overlaysByKind('track')`，`read_receipt.updated` 失效 `readReceipts()`。
   - 浏览器测试验证：右下角悬浮铃铛已移除；Report 视图对话入口点位可见且点击直达需处理会话；跨对话红/黄/转圈/蓝点与打开后蓝点消除闭环。
   - Ownership 审计检查通过（所有修改冻结路径均带准确 `OWNERSHIP-CHANGE: <path> — <reason> (#92)` trailer）。
3. **L2 双独立只读评审**：后端 + 前端两路独立审查无阻塞项后再更新 PR #98。
