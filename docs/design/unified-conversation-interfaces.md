# Perga 统一对话接口设计文档（现状盘点 + 目标 8 原语契约）

> **基线**：`main@186ced1`（PR #98 + PR #97 已合入）
> **配套架构文档**：[unified-conversation-architecture.md](unified-conversation-architecture.md)
> **策略**：Clean-Slate 彻底收口，删除全部旧双轨路由与工具，不保留兼容转发层。

---

## 1. 当前 `main@186ced1` 已实现接口全量盘点

### 1.1 已实现的 HTTP `/api/v1` 会话与交互接口（17 个端点，高度碎片化）

| 现有 HTTP 端点 | 当前后端入口 | 现存割裂问题 | 目标归属 |
| --- | --- | --- | --- |
| `GET /tracks/{trackId}/sessions` | `ListSessionsAsync` | 缺少 `parentSessionId / spawnDepth / configVersion / notifyParent` | **保留并升级 → 原语 #1** |
| `POST /tracks/{trackId}/sessions` | `CreateSessionWithReceiptAsync` | 仅能建普通空/首句对话，不支持 `systemPrompt / notifyParent / task` | **升级合一 → 原语 #2** |
| `POST /tracks/{trackId}/tasks/dispatch` | `DispatchWorkerAsync` | 与上条双轨；pending 时 `sessionId` 为 `null` | **删除（并入 #2）** |
| `GET /sessions/{sessionId}/items` | `GetSessionItemsAsync` | `TurnActivity` 仅 Worker 写入；`WorkerActivityDto` 重复打包审批/设置/消息/错误 | **保留并精简 → 原语 #3** |
| `PATCH /sessions/{sessionId}` | `PatchSessionAsync` | 仅改 `title / provider / model / archived`，无 `configVersion` | **升级合一 → 原语 #4** |
| `PUT /sessions/{sessionId}/model` | `UpdateSessionModelAsync` | 仅普通会话，拒绝 Worker | **删除（并入 #4）** |
| `PUT /sessions/{sessionId}/permission-mode` | `SetSessionPermissionModeAsync` | 仅普通会话，拒绝任务绑定会话 | **删除（并入 #4）** |
| `PATCH /sessions/{sessionId}/settings` | `UpdateWorkerSettingsAsync` | 仅 Worker，独立读 `worker_settings` 表与已失效的 `approvalMode` | **删除（并入 #4）** |
| `POST /sessions/{sessionId}/messages` | `AcceptMessageAsync` / `SendWorkerMessageAsync` | 靠 `body.AttemptId != null` 硬分叉两套入参和回执 | **合并单轨 → 原语 #5** |
| `POST /sessions/{sessionId}/messages/steer` | `SteerMessageAsync` | 与 `/messages` 重复 | **删除（并入 #5）** |
| `POST /sessions/{sessionId}/messages/queue/{inputId}/steer` | `SteerQueuedMessageAsync` | 可直接由 `send(messageId, mode: "steer", turnId)` 表达 | **删除（并入 #5）** |
| `DELETE /sessions/{sessionId}/messages/queue/{inputId}` | `RemoveQueuedMessageAsync` | 路径带冗余 `/queue/` | **精简为 `DELETE /sessions/{id}/messages/{messageId}` → 原语 #6** |
| `GET /sessions/{sessionId}/inputs/{inputId}` | `GetSessionInputAsync` | 旧轮询端点，PR #98 已直出 `items` + `queue` | **删除（或仅在确认无内部引用后移除）** |
| `POST /sessions/{sessionId}/interrupt` | `InterruptSessionAsync` / `InterruptWorkerAsync` | 靠 `AttemptId` 硬分叉两条实现 | **合并 → 原语 #7 (`action: "stop"`)** |
| `POST /sessions/{sessionId}/recover` | `RecoverSessionAsync` | PR #98 新增，但禁用 Worker | **合并 → 原语 #7 (`action: "reconnect" \| "reset_context"`)** |
| `POST /sessions/{sessionId}/compact` | `CompactSessionAsync` | 独立端点，禁止 Worker | **合并 → 原语 #7 (`action: "compact"`)** |
| `POST /tracks/{trackId}/asks/{askId}/answer` & `POST /tracks/{trackId}/executions/{executionId}/approvals/{approvalId}/decision` | `AnswerAskAsync` & `DecideApprovalAsync` | 两条路径分离，且 MCP 无对应入口 | **合并 → 原语 #8** |

### 1.2 已实现的 MCP `perga_*` 工具盘点（`KernelMcpTools.cs`，共 35 个）

- **共享只读/工作工具（11 个，保留）**：`perga_report_read`、`perga_report_commit`、`task_list`、`perga_user_ask`、`mail_list`、`mail_read`、`perga_track_status`、`perga_terminal_open`、`perga_terminal_read`、`perga_terminal_input`、`schedule_list`。
- **Track 治理工具（12 个，保留）**：`perga_task_cancel`、`perga_task_accept`、`perga_task_reject`、`perga_mail_send`、`perga_track_rename`、`perga_track_close`、`template_list`、`template_read`、`perga_template_update_inputs`、`perga_track_set`、`perga_schedule_set`、`schedule_cancel`（注：`task_cancel / accept / reject` 放宽为“Track Lead 或该 Task 的 `parentSessionId`”均可调用）。
- **契约 Worker 交卷工具（2 个，保留）**：`perga_task_done`、`perga_task_fail`。
- **系统升级工具（3 个，保留 Lead-only）**：`perga_system_upgrade_check`、`perga_system_upgrade_apply`、`perga_system_upgrade_rollback`。
- **现有会话操作工具（7 个，全部仅限 Lead 且强绑 Worker `attemptId`，将被替换为统一 8 原语）**：
  - 现有：`perga_session_read`、`perga_session_send`、`perga_session_withdraw`、`perga_session_steer_queued`、`perga_session_interrupt`、`perga_session_update_settings`、`perga_task_dispatch`。
  - **替换后（8 个统一原语）**：`perga_session_list`、`perga_session_create`、`perga_session_read`、`perga_session_update`、`perga_session_send`、`perga_session_withdraw`、`perga_session_control`、`perga_session_respond`。

---

## 2. 目标统一契约：8 个对话原语

所有 8 个原语在 `IPergaKernel` 中各对应**唯一一个方法**，入参均携带 `InteropPrincipal caller`，错误码严格遵循同一映射（`400 invalid_argument` / `403 forbidden` / `404 not_found` / `409 conflict` / `410 gone` / `409 precondition_failed` (HTTP 409，与 `HttpErrorHandling` 和前端 domain 分类一致)）。

---

### 原语 1：`List`（列出对话）
- **HTTP**：`GET /api/v1/tracks/{trackId}/sessions?includeArchived=false&parentSessionId=...`
- **MCP**：`perga_session_list`
  - 入参：`{ "includeArchived"?: boolean, "parentSessionId"?: string }`
- **Kernel**：`Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(string trackId, SessionListFilter filter, InteropPrincipal caller, CancellationToken ct)`
- **返回**：`SessionSummaryDto[]`，包含 `session`（含 `id / role / title / parentSessionId / spawnDepth / boundTaskId / boundAttemptId / provider / model / reasoningEffort / permissionMode / epoch / configVersion / notifyParent / contextUsage / archivedAtMs`）、`state`、`readonlyReason`、`workerPaused`、`lastTurn`（`id / status / errorText / completedAtMs`）、`pendingInteractionCount`。
- **权限**：`Human` 或同 Track 任意 `Session` 只读。

---

### 原语 2：`Create`（创建对话，可选挂载 `task` 契约）
- **HTTP**：`POST /api/v1/tracks/{trackId}/sessions`（Header `Idempotency-Key` 必填）
- **MCP**：`perga_session_create`（`requestKey` 可选，省略时服务端生成）
- **Kernel**：`Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionAsync(string trackId, CreateSessionRequest req, string requestKey, InteropPrincipal caller, CancellationToken ct)`
- **入参（`CreateSessionRequest`）**：
  ```jsonc
  {
    "title": "架构评审",                      // 可选；传 task 且省略时由 prompt 首行截断生成
    "provider": "codex",                     // 可选；默认跟随父会话 / Track 默认
    "model": "gpt-5.4",                      // 可选
    "reasoningEffort": "high",               // 可选
    "permissionMode": "never",               // 可选
    "role": "reviewer",                      // 可选；供模板 routing 解析默认 provider/model
    "systemPrompt": "你是只读架构评审员...",   // 可选；持久化在 agent_sessions.system_prompt，拼入默认系统提示
    "prompt": "请审查本次改动...",             // 可选（传 task 时必填）；首轮指令（替代旧 text / goal）
    "notifyParent": "turn_end",              // 可选："none" | "turn_end" | "task_settled"（无 task 默认 "none"，有 task 默认 "task_settled"）
    "requestKey": "req-001",                 // MCP 可选；HTTP 走 Idempotency-Key Header
    "task": {                                // 可选；不传 = 普通对话，传了 = 绑定 Task 契约
      "access": "read_write",                // 可选："read_only" | "read_write"（继承模板默认）
      "acceptance": "dotnet test 全绿",       // 可选
      "gate": "dotnet test Perga.sln",       // 可选（受模板 configuredGate 约束）
      "dependsOn": ["worker-abc"]            // 可选；同 Track taskKey 列表
    }
  }
  ```
- **返回（`SessionCreationReceipt`）**：
  ```jsonc
  {
    "session": { /* AgentSessionDto：创建瞬间立即可得确定 session.id */ },
    "firstInput": { /* SessionInputReceipt | null：立即启动时非空，空对话或等待依赖时为 null */ },
    "task": { /* TaskDto | null：仅传 task 时非空 */ },
    "requestKey": "req-001"
  }
  ```
- **语义与幂等**：
  - `creation_receipts` 同 `(author, 'session-create', trackId, requestKey)` 同哈希重放返回原回执（HTTP `200`，首次 `201`），异哈希返回 `409 Conflict`。
  - 传入 `task` 时，在同一 SQLite 事务内写入 Report Task 块、Task 行与预分配的 `agent_sessions` 行（`bound_task_id = task.id`、`parent_session_id`、`spawn_depth`），再由调度器在依赖与租约满足时绑定 `bound_attempt_id` 启动首轮。
- **权限**：`Human` 或同 Track 活跃独立对话（`BoundAttemptId == null`）且 `caller.SpawnDepth < MaxSessionSpawnDepth`。

---

### 原语 3：`Read`（观察状态、错误、历史、实时进度与待决交互）
- **HTTP**：`GET /api/v1/sessions/{sessionId}/items?afterId=0&beforeId=...&limit=100&latest=false&activityTurnId=...&afterSequence=...&waitMs=0`
- **MCP**：`perga_session_read`
  - 入参：`{ "sessionId": string, "beforeId"?: integer, "afterId"?: integer, "limit"?: 1..100 (默认 20), "activityTurnId"?: string, "afterSequence"?: integer, "waitMs"?: 0..10000 }`
- **Kernel**：`Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, SessionReadQuery query, InteropPrincipal caller, CancellationToken ct)`
- **返回（统一后的 `SessionItemsPageDto`）**：
  ```jsonc
  {
    "session": { /* AgentSessionDto：含 parentSessionId / spawnDepth / configVersion / notifyParent */ },
    "state": "idle | running | stopping | compacting | unknown",
    "readonlyReason": null,                  // null | "archived" | "track_closed" | "worker_finished"
    "canReopen": false,
    "workerPaused": false,
    "activeTurn": { /* SessionTurnDto | null */ },
    "lastTurn": { /* SessionTurnDto | null：含 status / errorText */ },
    "unknownDetail": { /* SessionUnknownDetailDto | null：reason / detail / canReconnect / canResetContext / recovering / lastError */ },
    "items": [ /* SessionItemDto[] */ ],
    "hasMoreBefore": false,
    "queuedMessages": [ /* QueuedSessionMessageDto[] */ ],
    "canSteer": false,
    "queueHandoffIds": [],
    "activity": {                            // 所有会话统一提供（不再仅限 Worker）
      "turnId": "trn_...",
      "sequence": 12,
      "reset": false,
      "events": [ /* SessionActivityEvent[] */ ],
      "text": "...",
      "truncated": false
    },
    "pendingInteractions": [                 // 统一视图：合并挂起的 approval 与 ask
      {
        "id": "apr_... | ask_...",
        "kind": "approval | ask",
        "sessionId": "ses_...",
        "turnId": "trn_...",
        "executionId": "op_... | null",      // 仅 approval 非空
        "title": "...",
        "toolName": "... | null",            // 仅 approval 非空
        "arguments": {},                     // 仅 approval 非空（截断上限 4096 字符）
        "options": [ /* ApprovalChoiceDto[]，仅 approval */ ],
        "questions": [ /* AskQuestionDto[]，仅 ask */ ],
        "canRespond": true,                  // 基于当前 caller 权限实时计算
        "createdAtMs": 0
      }
    ],
    "capabilities": {                        // 统一能力目录（取代旧 PermissionModes + WorkerProviderConfiguration）
      "models": [ /* WorkerModelOption[] */ ],
      "defaultModel": "...",
      "defaultReasoningEffort": "...",
      "permissionModes": [ /* PermissionModeOption[] */ ],
      "supportsNativeSteer": true,
      "supportsSessionResume": true,
      "supportsCompact": true
    },
    "taskBinding": {                         // 仅绑定 Task 时非空（取代臃肿的 WorkerActivityDto）
      "task": { /* TaskDto */ },
      "attemptId": "att_... | null",
      "attemptPhase": "pending | running | paused | verifying | done | failed | canceled",
      "waitingReason": "waiting_dependencies | waiting_lease | null"
    }
  }
  ```
- **长轮询语义**：当 `waitMs > 0` 且（提供 `activityTurnId + afterSequence` 或 `state` 未达稳态）时，在 `TrackActor` 邮箱外订阅 `EventHub`，收到目标会话的状态/进度/队列/交互变更事件立刻重读返回。
- **权限**：`Human`、自身、父会话或同 Track Lead。

---

### 原语 4：`Update`（修改会话元数据与运行配置）
- **HTTP**：`PATCH /api/v1/sessions/{sessionId}`
- **MCP**：`perga_session_update`
- **Kernel**：`Task<AgentSessionDto> UpdateSessionAsync(string sessionId, UpdateSessionRequest req, InteropPrincipal caller, CancellationToken ct)`
- **入参（`UpdateSessionRequest`）**：
  ```jsonc
  {
    "expectedConfigVersion": 2,              // 修改 provider/model/reasoningEffort/permissionMode 时：MCP 必填，HTTP 推荐携带（版本不匹配返回 409）
    "title": "新标题",                        // 可选，未传不改
    "archived": false,                       // 可选，与 state 正交；归档/恢复
    "provider": "codex",                     // 可选（仅 idle 且非运行中允许改，改 provider 重置为目标 provider 默认 permissionMode）
    "model": "gpt-5.4",                      // 可选；显式 null = 恢复 provider 默认
    "reasoningEffort": "high",               // 可选；显式 null = 恢复默认
    "permissionMode": "never"                // 可选；显式 null = 恢复默认
  }
  ```
- **行为**：
  - 用轻量 `Optional<T>` 区分“字段未传（保留现状）”与“显式 `null`（恢复默认）”。
  - 运行配置变更原子递增 `config_version = config_version + 1` 并持久化 `agent_sessions`；正在运行的 turn 继续沿用启动时的配置，下一轮或空闲复用边界通过 `ConfigureAsync` 确认生效。
  - 若目标为已终态 Worker（`readonlyReason == "worker_finished"`）或已关闭 Track，拒绝写入。
- **权限**：`Human`、父会话或同 Track Lead；禁止 AI 归档或对自身运行 turn 触发重开。

---

### 原语 5：`Send`（发消息 / 排队 / 插队）
- **HTTP**：`POST /api/v1/sessions/{sessionId}/messages`（Header `Idempotency-Key` 发新消息时必填）
- **MCP**：`perga_session_send`
- **Kernel**：`Task<SessionMessageReceipt> SendSessionMessageAsync(string sessionId, SendSessionMessageRequest req, InteropPrincipal caller, CancellationToken ct)`
- **入参（`SendSessionMessageRequest`）**：
  ```jsonc
  // 模式 A：发新消息（content 必填，messageId 省略）
  {
    "content": "请补齐单测",
    "requestKey": "msg-key-1",               // HTTP 走 Idempotency-Key Header；MCP 必填
    "mode": "default",                       // "default" | "steer" | "queue"（省略默认 "default"）
    "turnId": "trn_...",                     // mode == "steer" 时必填；其他模式禁止传入
    "expectedAttemptId": "att_..."           // 可选乐观断言（仅对绑定 Attempt 的对话做防漂移校验）
  }

  // 模式 B：将已排队消息插队到当前 turn（替代旧 steer_queued）
  {
    "messageId": "inp_...",                  // 与 content 互斥
    "mode": "steer",                         // 必须为 "steer"
    "turnId": "trn_...",                     // 必填，精确匹配当前运行 turn
    "expectedAttemptId": "att_..."           // 可选
  }
  ```
- **统一响应（`SessionMessageReceipt`）**：
  ```jsonc
  {
    "messageId": "inp_...",
    "sessionId": "ses_...",
    "turnId": "trn_... | null",
    "delivery": "accepted | queued | delivered | rejected | unknown",
    "replay": false
  }
  ```
- **语义**：
  - `mode: "default"`：空闲时立即开始新轮（暂停的 Worker 同步恢复执行）；忙碌时返回 `409 Conflict`（明确区分，不静默猜排队）。
  - `mode: "queue"`：入队等待下一轮；暂停的 Worker 保持暂停。
  - `mode: "steer"`：强校验 `turnId` 与 `SupportsNativeSteer`，通过 OAR `session/steer` 实时注入当前轮。
- **权限**：`Human`、父会话或同 Track Lead；禁止向自身运行中的会话 `send`。

---

### 原语 6：`Withdraw`（撤回排队/未知消息）
- **HTTP**：`DELETE /api/v1/sessions/{sessionId}/messages/{messageId}`（可选 query `?expectedAttemptId=...`）
- **MCP**：`perga_session_withdraw`
  - 入参：`{ "sessionId": string, "messageId": string, "expectedAttemptId"?: string }`
- **Kernel**：`Task WithdrawSessionMessageAsync(string sessionId, string messageId, string? expectedAttemptId, InteropPrincipal caller, CancellationToken ct)`
- **语义**：幂等撤回处于 `queued` 或无 `turnId` 的 `unknown` 消息（已撤回直接成功，已绑定 turn 返回 `409 Conflict`）。
- **权限**：`Human`、父会话或同 Track Lead。

---

### 原语 7：`Control`（对话状态机跃迁）
- **HTTP**：`POST /api/v1/sessions/{sessionId}/control`
- **MCP**：`perga_session_control`
- **Kernel**：`Task<AgentSessionDto> ControlSessionAsync(string sessionId, SessionControlRequest req, InteropPrincipal caller, CancellationToken ct)`
- **入参（`SessionControlRequest`）**：
  ```jsonc
  {
    "action": "stop | reconnect | reset_context | compact",
    "turnId": "trn_...",                     // action == "stop" 时必填（含已受理启动中的 turnId）
    "expectedEpoch": 1,                      // action == "reconnect" | "reset_context" 时可选，防过期操作
    "expectedAttemptId": "att_..."           // 可选乐观断言
  }
  ```
- **4 个动作精确语义**（1:1 映射 PR #98 5 态控制面与 `/` 命令表）：
  1. `stop`：中断指定 `turnId`（含启动期取消）；对 Worker 额外置 `workerPaused = true` 并保持租约；
  2. `reconnect`：仅 `state == "unknown"` 且 `canReconnect == true` 可用；停旧句柄 → 用原 `NativeThreadId` 重连回 `idle`（普通对话与 Worker 通用）；
  3. `reset_context`：`state` 为 `idle` 或 `unknown` 可用；确认旧进程树停止 → 撤销 MCP → 同事务归档未决消息与会话唤醒 → 插入 `epoch_boundary` → `Epoch + 1`、`NativeThreadId = null` 回 `idle`（Worker 保留当前 Attempt 与工作区租约）；
  4. `compact`：仅 `state == "idle"` 可用；走 OAR 原生 `compact` / Epoch 摘要交接，受 `SessionCompactionTimeout` 保护。
- **权限**：`Human`、父会话或同 Track Lead；严格校验 AI `ExecutionOrigin` 与防自毁红线。

---

### 原语 8：`Respond`（响应挂起交互：Approval 或 Ask）
- **HTTP**：`POST /api/v1/sessions/{sessionId}/interactions/{interactionId}/respond`
- **MCP**：`perga_session_respond`
- **Kernel**：`Task<SessionInteractionResult> RespondInteractionAsync(string sessionId, string interactionId, RespondInteractionRequest req, InteropPrincipal caller, CancellationToken ct)`
- **入参（`RespondInteractionRequest`）**：
  ```jsonc
  // 响应 Approval（executionId + optionId 必填）
  {
    "executionId": "op_...",
    "optionId": "approve"
  }

  // 响应 Ask（answers 必填）
  {
    "answers": [ { "question": "...", "selected": ["..."], "freeText": "..." } ]
  }
  ```
- **语义**：
  - `interactionId` 前缀或内存/DB 查表确定类型（`apr_*` 走执行期审批单次投递锁与审计，`ask_*` 走 SQLite 持久回答并唤醒提问会话）；
  - Approval 严格校验 `executionId`、`optionId`、`sessionId` 匹配、`ExecutionOrigin` 与委派范围，二次校验失败记审计 `delivery = "aborted"` 而不污染 pending 状态；
  - Ask 校验 `targetSessionId` 归属（`Human` 始终可兜底回答）。
- **权限**：`Human`（全权兜底），或满足 `delegateSessionId` / `targetSessionId` 授权的父会话/Lead。

---

## 3. 任务契约层原语（独立于 8 个对话原语）

仅当对话创建时挂载了 `task` 契约时，才涉及以下任务层原语（保持职责正交，不混入对话原语）：
- **子对话交卷（`BoundAttemptId != null` 专有）**：`perga_task_done`、`perga_task_fail`；
- **父方/人类任务治理**：
  - `GET /tracks/{trackId}/tasks/{taskKey}/attempts` & `task_list`
  - `POST /tracks/{trackId}/tasks/{taskKey}/cancel` & `perga_task_cancel`
  - `POST /tracks/{trackId}/tasks/{taskKey}/accept` & `perga_task_accept`
  - `POST /tracks/{trackId}/tasks/{taskKey}/reject` & `perga_task_reject`（`reQueue: true` 时复用同一 `session.id` 升 `Epoch + 1` 绑定新 Attempt）

---

## 4. Clean-Slate 删减清单（验收必查项）

以下旧接口与双轨代码必须在对应阶段**彻底删除**，禁止留存别名或死代码：

| 类别 | 明确删除对象 |
| --- | --- |
| **HTTP 路由** | `POST /tracks/{id}/tasks/dispatch`、`PUT /sessions/{id}/model`、`PUT /sessions/{id}/permission-mode`、`PATCH /sessions/{id}/settings`、`POST /sessions/{id}/messages/steer`、`POST /sessions/{id}/messages/queue/{id}/steer`、`DELETE /sessions/{id}/messages/queue/{id}`、`GET /sessions/{id}/inputs/{id}`、`POST /sessions/{id}/interrupt`、`POST /sessions/{id}/recover`、`POST /sessions/{id}/compact`、`POST /tracks/{id}/asks/{id}/answer`、`POST /tracks/{id}/executions/{e}/approvals/{a}/decision` |
| **MCP 工具** | `perga_task_dispatch`、`perga_session_steer_queued`、`perga_session_interrupt`、`perga_session_update_settings`（被 8 个统一 `perga_session_*` 取代） |
| **Kernel / Storage** | `worker_settings` 表、`IWorkerSettingsStore`、`SqliteTrackStore.WorkerSettings.cs` 中的设置读写、`WorkerSettings` / `UpdateWorkerSettingsRequest` / `WorkerMessageRequest` / `WorkerMessageReceipt` / `WorkerActivityDto` / `DispatchWorkerRequest` / `WorkerDispatchReceipt` / `UpdateSessionModelRequest` / `RecoverSessionRequest`、`WorkerApprovalAuthorization`（及 `PergaHostOptions.WorkerApprovalAuthorizationSha256`） |
| **OAR / AgentKit** | `HarnessLaunchSpec.ConfigureWorker`、`OarAgentSession._configureWorker`、`SessionTurnRunner.PrepareTurnDispatchCoreAsync` Worker 专属分支 |

---

## 5. 前端与 OpenAPI 同步清单（Stage 3）

按 `AGENTS.md` 契约要求，Stage 3 必须同步更新以下消费者并通过全部前端检查：
1. `fe/core/api/generated/openapi.json`：更新为 8 个统一会话端点与新 DTO schema，移除第 4 节全部已删端点。
2. `fe/core/api/perga-v1.ts` 及 `fe/core/api/perga-v1.test.ts`：同步更新 Zod schema（**注意**：属于 frozen API 文件，提交信息必须包含 `OWNERSHIP-CHANGE:` trailer）。
3. `fe/core/domain/` 收口：
   - 合并 `worker-session.ts`、`planner-permission-mode.ts`、`conversation.ts`、`ask.ts`、`approval.ts` 中的会话写操作到 8 个统一 operation（`PATCH /sessions/{id}`、`POST /sessions/{id}/control`、`POST /sessions/{id}/messages`、`DELETE /sessions/{id}/messages/{id}`、`POST /sessions/{id}/interactions/{id}/respond`）；
   - 消费 `SessionItemsPageDto` 顶层 `activity`、`pendingInteractions`、`capabilities`、`taskBinding`。
4. `fe/web/src/app/providers/queries.ts` 与相关组件：移除 `target === undefined ? sendPlannerInput : sendWorkerMessage`、`updateWorkerSettings` + `approvalKey`、`setModel` / `setPermissionMode` 三轨分叉，并支持对处于 `unknown` 的 Worker 直接展示 `reconnect / reset_context` Action 卡片。
