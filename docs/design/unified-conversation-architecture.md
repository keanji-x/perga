# Perga 统一对话内核架构设计（Clean-Slate）

> **基线**：`main@186ced1`（已合入 [PR #98](https://github.com/keanji-x/perga/pull/98) 对话 5 态控制面/活性恢复/迁移 `0019`，以及 [PR #97](https://github.com/keanji-x/perga/pull/97) `@botiverse/oar` 统一运行时）
> **配套接口文档**：[unified-conversation-interfaces.md](unified-conversation-interfaces.md)（含 `main@186ced1` 已实现接口盘点与目标 8 原语精确契约）
> **评审级别**：**L2**（主体授权边界、会话控制状态机、前向迁移 `0021`、`/api/v1` + OpenAPI 与 MCP 公共契约）

---

## 1. 设计目标与反冗余品味

### 1.1 目标
在 PR #98（单一 5 态会话状态机）与 PR #97（单一 OAR 运行时）之上，彻底消除**普通对话 vs Worker 对话**以及**人（HTTP）vs AI（MCP）**的最后两道双轨割裂：
1. **一套对话内核**：所有对话（Planner / Assistant / Worker）共享同一套状态机、消息账本、配置源、实时进度缓冲、恢复引擎与 OAR 启动路径；
2. **8 个正交原语**：人与 AI 通过完全相同的 8 个对话控制原语操作任意授权对话（列、建、读、改、发、撤、控、应）；
3. **一棵归属树**：人作为树根拥有全权，任何活跃独立对话均可派发子对话/Worker，**谁派发谁控制**，Lead 做 Track 级兜底；
4. **Clean-Slate 零双轨**：直接删除旧分叉路由、旧 `worker_settings` 表与 `ConfigureWorker` 特判，不保留历史兼容壳。

### 1.2 实现品味与反过度防御红线（Codex 强制遵守）
实现者必须以**简洁、删除冗余、单一权威**为第一准则，严格禁止以下坏味道：
1. **禁止多余抽象层**：不新增 `ISessionControlService`、`ISessionOrchestrator`、`ISessionNotificationBus`、策略工厂或 Result Monad。`TrackActor` 本身就是单写者聚合根，继续用简洁的 `partial class TrackActor` 分文件组织（单文件尽量控制在 300–500 行内）。
2. **禁止多处重复校验**：格式校验仅在边界（HTTP/MCP 解析）做一次；状态与权限校验仅在 `TrackActor` 单写者邮箱/执行锁内做一次；持久化完整性由 SQLite 约束兜底。不要在 `PergaKernelService`、Helper、Storage 里重复写三遍同样的 `if` 防御。
3. **禁止假想防御与多余拷贝**：`TrackActor` 邮箱内已是单写者串行执行，禁止给仅在邮箱内访问的字典加多余 `lock` 或 `ConcurrentDictionary`；禁止对只读配置做多层深拷贝；禁止为内存态瞬时审批通知引入额外的持久化表或重试调度器。
4. **禁止保留过渡双轨**：不要保留“旧端点转发到新端点”“优先读新列、为空再回退读旧表 `worker_settings`”“同时保留 `WorkerActivityDto` 冗余字段”的防御性兼容代码。
5. **代码净增量克制**：本设计通过消灭双轨、合并端点与移除死代码，多个核心模块应呈现**删改大于堆砌**的结构收敛。测试使用紧凑的参数化 `[Theory]` 直击不变量，禁止复制粘贴数百行重复测试夹具。

---

## 2. 总体架构

```text
   Human (Web UI / HTTP /api/v1)          AI (Agent Session / MCP /mcp/kernel)
                │                                           │
                │ InteropPrincipal.Human (Root)             │ InteropPrincipal.Session
                └────────────────────┬──────────────────────┘
                                     ▼
                    ┌─────────────────────────────────┐
                    │  SessionPolicy (纯静态单点裁决)  │  ← 归属树 + 深度/租约护栏 + 防自毁
                    └────────────────┬────────────────┘
                                     ▼
        ┌────────────────────────────────────────────────────────┐
        │          Unified Session Kernel (TrackActor)           │
        │  1. List    2. Create(+optional task)  3. Read(+wait)  │
        │  4. Update  5. Send(+steer/queue)      6. Withdraw     │
        │  7. Control(stop|reconnect|reset_context|compact)      │
        │  8. Respond(approval|ask)                              │
        └────────────────────────────┬───────────────────────────┘
                                     │
         ┌───────────────────────────┴────────────────────────────┐
         ▼                                                        ▼
  所有对话共享的单一内核                                  可选外置任务契约壳（仅传 task 时）
  - 5 态控制面 (PR #98) + opEpoch 原子恢复                - Report Task 块 + DAG 调度
  - session_messages / session_turns / session_items      - Workspace Lease (read_only|read_write)
  - 统一 TurnActivity 内存进度缓冲                        - perga_task_done / fail & Gate 验证
  - 统一版本化配置 (agent_sessions)                       - workerPaused & worker_finished
  - parent_session_id / spawn_depth 归属树                - 验收 (task_cancel / accept / reject)
  - OAR Bridge 统一能力回报 (无 ConfigureWorker 特判)
```

---

## 3. 五项核心架构不变量

### 3.1 单一对话内核 + 外置任务契约壳
- 所有会话（`planner | assistant | worker`）共用完全一致的 5 态控制面（`idle | running | stopping | compacting | unknown`）、消息账本、回合账本、活动缓冲与恢复引擎。
- **预分配 `session.id`**：当调用 `session_create` 传入可选 `task` 契约时，在同一 SQLite 事务内**同时创建 Task 块与 `agent_sessions` 行**（立即绑定 `bound_task_id`、`parent_session_id`、`spawn_depth`）。即使 Task 仍在等待 `dependsOn` 或租约，调用方在创建瞬间就拿到稳定的 `session.id`；待调度满足时获取工作区租约并绑定 `bound_attempt_id` 启动首轮。
- **Task 重跑复用同一会话升 Epoch**：当任务被 `task_reject(reQueue: true)` 重试产生新 Attempt 时，复用同一个 `session.id`：写入 `epoch_boundary(reason: "new_attempt")`，推进 `Epoch + 1`、`NativeThreadId = null` 并绑定新 `bound_attempt_id`。侧边栏与调用方始终面对“一个任务 = 一条连续对话”，不再堆积历史废弃会话。
- **Worker 开放统一 `control(reconnect | reset_context)`**：
  - `reconnect`：尝试重连原 `NativeThreadId` 继续当前 Attempt；
  - `reset_context`：确认旧进程树停止、撤销旧 MCP 令牌、归档未决消息、写入 `epoch_boundary`、推进 `Epoch + 1` 并清空 `NativeThreadId`，**保留当前 Attempt 与工作区租约及已有文件改动**，回到 `idle` 等待下一条消息（若需放弃工作区改动重开，走 `task_cancel`）。

### 3.2 统一主体与归属树授权（`SessionPolicy`）
用极简 2 分支 discriminated record 取代各处 `caller == null` 隐式判断：
```csharp
public abstract record InteropPrincipal
{
    public sealed record Human : InteropPrincipal;
    public sealed record Session(
        string TrackId,
        string SessionId,
        bool IsLead,
        string? BoundAttemptId,
        int SpawnDepth,
        string Token,
        McpExecutionOrigin? ExecutionOrigin) : InteropPrincipal;
}
```
全部授权逻辑收口在一个纯静态类 `SessionPolicy`（不造接口/工厂）：
1. **派生权（`Create`）**：
   - `Human` 与同 Track 内任意活跃的独立对话（`BoundAttemptId == null`，即 Lead 与普通 Assistant）均可调用 `session_create`（无论是否携带 `task` 块）。
   - 硬护栏：`SpawnDepth >= MaxSessionSpawnDepth`（默认 `2`，可通过 `KernelOptions` 配置）时拒绝继续派生；绑定 `read_write` 租约的 Worker 默认不可再派生 `read_write` 子任务（防工作区写租约嵌套冲突）；默认禁止契约 Worker 派生子对话，保持执行契约纯粹。
2. **控制权（`Read / Update / Send / Withdraw / Control / Respond` 及 Task 治理）**：
   - **`Human`（树根）**：全权控制所有 Track、所有会话，始终保留兜底接管与回答任意 Ask/Approval 的权力；
   - **`Parent`（`target.ParentSessionId == caller.SessionId`）**：对自己派出的直接子对话（普通子对话 & Worker）拥有完整会话控制权及对应 Task 治理权；
   - **`Lead`（Track 治理兜底）**：对同 Track 内任意非自身会话拥有观察与控制兜底权（防止派发者归档或卡死后产生孤儿对话）；
   - **只读自检**：任何会话均可只读 `read` 自己与同 Track 的 `list`。
3. **防自毁与执行期活性红线（不可突破）**：
   - 任何 AI 会话禁止对自己当前正在运行的 turn 执行 `stop`、`reset_context`、`compact`、`archived: true` 或触发重开的 `update`；
   - AI 通过 MCP 发起的 `control`、`respond` 等状态变更必须校验 `ExecutionOrigin`（发起 turn 仍在运行且未 stopping），防止已被中断的旧 turn 发出迟到控制指令；
   - 所有 MCP 写入继续经过模板 `PolicyVm.Before("tool." + toolName)`，允许模板按需收紧或升级 `AskHuman`。

### 3.3 统一观测与事件驱动等待
- **`TurnActivity` 通用化**：取消 `TrackActor.WorkerSessions.cs` 中 `active.Settings is null` 跳过 `TurnActivity` 的分支，所有会话（Planner / Assistant / Worker）在运行 turn 时统一写入有界内存 `TurnActivity`（`sequence / events / liveText / truncated`）。
- **`SessionItemsPageDto` 顶层唯一真相**：HTTP 与 MCP 共用同一响应结构，`state / readonlyReason / lastTurn / unknownDetail / queuedMessages / activity / pendingInteractions / capabilities` 全部位于顶层；原 `WorkerActivityDto` 瘦身为极薄的 `SessionTaskBindingDto`（仅含 `task / attemptId / attemptPhase / waitingReason`）。
- **`waitMs` 通用长轮询**：`GetSessionItemsAsync(..., waitMs)` 对所有会话生效，订阅同 Track `EventHub`（关注 `session.updated`、`turn.completed`、`session.text_delta`、`session.tool_progress`、`approvals.changed`、`ask.changed`、`queue.changed`），任一相关事件到达立即唤醒返回最新快照。

### 3.4 配置源与 OAR 运行时统一（衔接 PR #97）
- 合并 `worker_settings` 到 `agent_sessions` 的版本化配置列（`provider / model / reasoning_effort / permission_mode / config_version`），彻底删除 `worker_settings` 表与 `IWorkerSettingsStore`。
- 鉴于 PR #97 已将全部运行时切换至非交互 `@botiverse/oar`（`OarAgentSession.TryClaimApprovalResponse => null`，无 provider 交互审批）：
  - **删除 `approval_mode` 与 `WorkerApprovalAuthorization`**（连同 `PergaHostOptions.WorkerApprovalAuthorizationSha256` 及 `X-Perga-Worker-Approval-Key` 死路径，Clean-Slate 彻底清理）；
  - **删除 `HarnessLaunchSpec.ConfigureWorker` 与 `OarAgentSession._configureWorker`**：`tools/perga-oar-bridge/src/bridge.ts` 在 `session/open` 时对所有会话统一返回 `sessionConfiguration`（`models / defaultModel / defaultReasoningEffort`），`SessionTurnRunner.cs` 删除 35 行 `PrepareTurnDispatchCoreAsync` Worker 专属协调分支，普通对话与 Worker 走 100% 同一条 `GetOrOpenAgentSessionCoreAsync` + `ConfigureAsync` 确认路径。

### 3.5 异步回调与待决交互（替代 PR #91）
1. **任意会话唤醒（`notifyParent`）**：
   - 在 `session_create` 持久化 `notify_parent` 策略（`"none" | "turn_end" | "task_settled"`）。
   - 扩展 `SessionTurnScheduler.PokeAsync`：允许**任意空闲会话**消费 `track_wakes` 中 `target_session_id == sessionId` 的唤醒（而不再写死 `if (sessionId != _track.LeadSessionId) return null`）；`target_session_id IS NULL` 的 Track 级唤醒仍由当前 Lead 认领。
   - 当子对话回合结束（`turn_end`）或任务结算（`task_settled`）且 `parent_session_id` 存在且未归档时，向 `target_session_id = parent_session_id` 入队唤醒；若父对话已归档则回退不唤醒（或由 Lead 通过 `list` 观察）。
2. **统一待决交互（`pendingInteractions` + `session_respond`）**：
   - `AskDto` 新增 `targetSessionId`（创建时默认指向提问者的 `parentSessionId`，无父会话则为 `null` 面向人类）；回答走统一 `RespondInteractionAsync`，回答后按既有 `TrackWake` 唤醒提问会话。
   - `Approval`（当前来源于 `PolicyVm.AskHuman`，保留 `source` 扩展位供未来 OAR 交互审批接入）保持**纯内存、执行期绑定**语义：
     - 补全 `ApprovalDto` 的结构化负载（`source / toolName / arguments / delegateSessionId`），人机读同一份对象；
     - 审批通知采用**内存瞬时集合**通知 `delegateSessionId`（绝不写 `track_wakes` 的 `Mail` 类别，重启零残留、过期/被人类处理立即清除）；
     - 权限检查在单写者/受保护状态快照内完成（不无锁跨线程读 `_track`）；若写库审计后二次校验失败（审批者 turn 刚结束），写入 `delivery = "aborted"` 而非 `"expired"`，保持审批仍 pending 等待人类决定。

---

## 4. 数据库前向迁移（`0021_unified_session_interop.sql`）

严格遵守 `main@270de0e` 已发布迁移 `0001`–`0020` 字节冻结原则（保留上游 `0020_read_receipts.sql`）：

```sql
-- 1. 统一会话归属树、配置版本与完成回调
ALTER TABLE agent_sessions ADD COLUMN parent_session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;
ALTER TABLE agent_sessions ADD COLUMN spawn_depth INTEGER NOT NULL DEFAULT 0 CHECK (spawn_depth >= 0);
ALTER TABLE agent_sessions ADD COLUMN config_version INTEGER NOT NULL DEFAULT 0 CHECK (config_version >= 0);
ALTER TABLE agent_sessions ADD COLUMN notify_parent TEXT NOT NULL DEFAULT 'none'
    CHECK (notify_parent IN ('none', 'turn_end', 'task_settled'));
ALTER TABLE agent_sessions ADD COLUMN system_prompt TEXT;

-- 2. 回填既有 worker_settings 与历史 worker-dispatch 归属关系
UPDATE agent_sessions
SET config_version = COALESCE((SELECT version FROM worker_settings w WHERE w.session_id = agent_sessions.id), 0),
    model = COALESCE((SELECT model FROM worker_settings w WHERE w.session_id = agent_sessions.id), model),
    reasoning_effort = COALESCE((SELECT reasoning_effort FROM worker_settings w WHERE w.session_id = agent_sessions.id), reasoning_effort)
WHERE EXISTS (SELECT 1 FROM worker_settings w WHERE w.session_id = agent_sessions.id);

UPDATE agent_sessions
SET parent_session_id = (
        SELECT r.author_id FROM creation_receipts r
        WHERE r.operation = 'worker-dispatch'
          AND r.scope_id = agent_sessions.track_id
          AND r.resource_id = agent_sessions.bound_task_id
          AND EXISTS (SELECT 1 FROM agent_sessions p WHERE p.id = r.author_id)
        LIMIT 1
    ),
    spawn_depth = 1,
    notify_parent = 'task_settled'
WHERE bound_task_id IS NOT NULL;

DROP TABLE IF EXISTS worker_settings;

-- 3. Ask 支持路由到上级会话（NULL 表示面向人类）
ALTER TABLE asks ADD COLUMN target_session_id TEXT REFERENCES agent_sessions(id) ON DELETE SET NULL;

-- 4. 索引补充
CREATE INDEX IF NOT EXISTS idx_sessions_parent
    ON agent_sessions(track_id, parent_session_id) WHERE parent_session_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_creation_receipts_resource
    ON creation_receipts(operation, scope_id, resource_id);
```

---

## 5. 三阶段落地与 L2 评审流程

所有阶段在同一条 Track 分支上按顺序推进，**每阶段执行：Codex 实现 + Gate 全绿 → 两轮独立 L2 Review（视角 1：架构/权威/持久化/恢复；视角 2：反冗余品味/接口一致/测试有效性）→ 修复并复核 → 再进入下一阶段**：

| 阶段 | 交付范围 | 核心门禁（Gate） |
| --- | --- | --- |
| **Stage 1：内核与存储统一** | 迁移 `0021`；`InteropPrincipal` + `SessionPolicy`；统一 `TrackActor` 8 原语内核实现（含预分配 `session.id`、重跑升 Epoch、Worker `recover(reconnect\|reset_context)` 保留租约、全会话 `TurnActivity`、任意会话 `target_session_id` 唤醒、`notifyParent`）；移除 `worker_settings`、`WorkerApprovalAuthorization`、`ConfigureWorker` 与 `PrepareTurnDispatchCoreAsync` 双轨 | `tools/perga-oar-bridge` (`npm run typecheck && npm run build && npm test`) + `dotnet test Perga.sln` 全绿 |
| **Stage 2：MCP 8 原语与待决交互** | `KernelMcpTools` 收口为 8 个统一 `perga_session_*` 工具（移除 `perga_session_steer_queued / update_settings / perga_task_dispatch`）；支持非 Lead 独立对话按归属树派生与控制；落地 `AskDto.targetSessionId`、`ApprovalDto` 结构化字段与内存瞬时审批通知 | `dotnet test Perga.sln` 全绿（含真实 Test Host `/mcp/kernel` 多级派发、错误观测、stop/reconnect/reset、Ask/Approval 响应、越权与防自毁测试 + 关键权限守卫 Mutation 验证） |
| **Stage 3：HTTP Clean-Slate、OpenAPI 与前端对齐** | `ApiV1Endpoints` 彻底收口为 8 个统一会话路由（删除旧 `/tasks/dispatch`、`/model`、`/permission-mode`、`/settings`、`/messages/steer`、`/messages/queue/*/steer` 等旧路由）；同步 `fe/core/api/generated/openapi.json`、`fe/core/api/perga-v1.ts`（带 `OWNERSHIP-CHANGE` trailer）与 `fe/core/domain/*`、`fe/web/*` | `dotnet test Perga.sln` + `(cd fe && npm run lint && npm run build && npm test && npm run test:browser)` 全绿 |
