# Perga 统一调度图与模板防呆架构设计（v3 · 定稿）

> **基线**：`main` `666da20`（已含 PR #107/#114/#115 模板 v2.1 底座与 PR #112 `0023_unify_task_session_ownership.sql`）
> **核心理念**：一份 `flow.json` 作为 Track 唯一调度事实源（Source of Truth），用极简正交模型 **`when`（触发）× `do`（动作）** 统一编排一次性任务、周期巡检、轻量唤醒与 0-Token 外部等待（如 CI/CD 绿灯）；**节点 = 步骤/关卡，会话 = 可复用的长寿工位**；禁止 AI 静默删除活跃节点（取消必须显式标 `canceled: true`），已完结的一次性节点由**内核后台自动 GC 归档**，彻底解决长期 Track 文件膨胀。

---

## 一、现状代码诊断（`main` `666da20`）

PR #112 已将底层理顺为单向 `Task -> SessionId` 并退役了 `TaskAttempt`，但上层编排、会话复用与模板护栏仍存在 5 处结构断层：

1. **派发与 Report 强耦合**：`TrackActor.WorkerDispatch.cs:91-136` 伪造 `report_commit` 往 Report 底部塞 `perga-block task`，再由 `TaskBlockSynchronizer.cs:113` 反向解析进 `tasks`，Report 堆满孤立任务卡片。
2. **定时与外部等待割裂**：`track_schedules`（`0018_track_schedules.sql`）独立存了一套 `title/prompt/cron_expr`，与任务图割裂；等待 CI/CD 绿灯等外部条件没有一等原语，只能靠人工盯梢或 Agent 轮询烧 token。
3. **会话被历史任务永久锁死、重跑清空上下文**：
   - `TrackActor.cs:2455` 的 `TryGetOwningTaskCore` 用 `_tasks.FirstOrDefault(t => t.SessionId == sessionId)` 假设 1 Session : 1 Task，导致全代码库 14 处（含 `TrackActor.SessionInterop.cs:40-62, 119-124`、`TrackActor.Views.cs:54`）在任务结算后仍把会话锁为只读或冲突；
   - `TrackActor.cs:2000-2006` 在重跑时硬设 `NativeThreadId = null` 并升 `Epoch`，会抹掉原上下文，无法带原上下文续审。
4. **缺少领域中性的业务结论**：`task_done` 只有进程结束态；`TaskGraphScheduler.cs:115` 仅有旧的 `SemanticVerdict`，无法统一表达「评审通过/打回」「CI 绿/红」「指标正常/越界」。
5. **Report 缺少骨架锁、模板缺少编译期视图校验**：`TrackActor.cs:2963` 未保护模板声明的 `widget` 块、未禁运行期私插 `perga-block html`；`PolicyVm.Compile` 未试跑 `report.views` 校验输出契约。

---

## 二、核心架构：单一 `Data → Logic → View` 闭环

```text
┌─────────────────────────────────────────────────────────────────────────┐
│                          DATA 层（两份正交数据）                          │
│                                                                         │
│  ① Track.Flow（封闭 Schema · 唯一活跃调度事实源）                          │
│     · Spec 文件：{DataDir}/tracks/{trackId}/flow.json（含 rev）          │
│       正交声明：when(after / cron / at) × do(run / wake / watch)         │
│       安全边界：取消显式标 canceled: true；终结孤立节点由后台自动 GC 归档   │
│     · Status & History（SQLite 内核独占运行态与已归档历史任务记录）        │
│                                                                         │
│  ② Track.Data（开放 Schema · 业务领域数据，#114/#115 已落地）             │
│     · 估值、财报 Expect、KR 总账、阶段摘要（经 data_patch 写入）          │
│     · 通过 t.ref("task") 外键关联 ① 中的节点 id                          │
└───────────────────┬─────────────────────────────────┬───────────────────┘
                    │ 唯一门禁（仅 2 个参数）           │ Fail-Closed 校验
                    ▼ flow_commit({dryRun, expectedRev}) ▼ data_patch(expectedDataRev)
┌─────────────────────────────────────────────────────────────────────────┐
│                          LOGIC 层（单一内核调谐器）                       │
│  · TrackFlowReconciler：全图校验（无环/防误删活跃节点/模型路由锁/cron），  │
│    在单 SQLite 事务内落盘 flow_spec_json + flow_rev++，单向驱动：         │
│      TaskGraphScheduler（outcome + 活跃租约）+ 纯时钟触发器 + Watch 探针  │
│  · Background GC：终结且无未完成下游的一次性节点自动归档并清理出 flow.json │
│  · Template 包（template.perga.js）：低频复用规则 + Report 骨架 + View 锁 │
└───────────────────┬─────────────────────────────────────────────────────┘
                    │ 纯只读实时投影 GET /api/v1/tracks/{id}/views
                    ▼ （输入：{ flow: Spec + Status + Archived, data }）
┌─────────────────────────────────────────────────────────────────────────┐
│                          VIEW 层（Report 纯出版物）                       │
│  · 内置零配置 builtin:flow：直接渲染活跃 DAG + 等待进度 + 常驻脚注        │
│  · 模板 report.views(data, flow)：按需把业务数据叠到流程或原生骨架上       │
│  · report_commit 收窄为纯正文编辑：锁模板 widget/flow，禁私插 html/task   │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 三、五个核心架构拍板

### 1. 唯一调度文件 + 防误删 + 后台安全 GC（彻底解决文件膨胀）
- **文件位置与权限**：`{DataDir}/tracks/{trackId}/flow.json` 由 Host 托管。能访问该路径的 Lead 可直接编辑草稿。SSH Lead 可用 `flow_read(format: "spec")` 读取已提交的规格和版本，再通过 MCP 提交 JSON 文本。
- **统一提交门禁**：
  ```ts
  flow_commit({
    dryRun?: boolean,           // 默认 false；true = 全图校验并返回 Plan Diff，零副作用
    expectedFlowRev?: number,   // 可选 CAS；省略时校验文件头 rev == 当前 flow_rev
    content?: string,          // JSON 草稿；省略时读取 Host 上的 flow.json
  })
  ```
- MCP 和 HTTP 使用同一校验与提交路径。显式提交文本时，SQLite 事务记录提交时 Host 文件的 hash，随后投影规范化文件。若文件在这期间更新，保留更新的草稿。重启可在 hash 仍匹配时完成投影。
- **防误删与显式 `canceled`**：
  - 任何**尚未终结的节点**（`pending` / `running` / `verifying` / `waiting`）或**活跃周期规则（`cron`）**，**禁止直接从 `flow.json` 删掉**——若 `flow_commit` 发现活跃节点缺失，直接拒绝提交，防止 AI 编辑漏行误杀正在跑的工作。
  - 意图取消必须显式设置 `"canceled": true` 并 `flow_commit`（内核中止活跃 Run / 停用触发器）。
- **后台自动 GC（根治膨胀 + 绝不覆盖草稿）**：
  - **GC 条件**：一次性节点已处于终态（`done` / `failed` / `canceled`）**且**当前 `flow.json` 中没有任何非终态下游依赖它；或周期节点已被显式标为 `canceled: true` 且无活跃 Run。
  - **归档不丢历史**：GC 时在 SQLite `tasks` 表打上 `archived_at_ms` 标记（历史摘要、结论、Session 外键永久保留，UI 折叠区可查），同时从 `tracks.flow_spec_json` 中移除该节点。
  - **草稿安全门禁**：内核记录每次写盘 `flow.json` 的内容 hash（`flow_file_sha256`）。后台 GC 仅在磁盘文件未被改脏（当前磁盘 hash == `flow_file_sha256`）时写回精简后的 `flow.json`；若 Lead 正在改草稿，后台不动磁盘，留到下次 `flow_commit` 顺带完成归档清理。
- **消除双份 Spec**：
  - `track_schedules` 降级为纯**触发时钟表**（仅维护 `node_key` / `next_fire_at_ms` / `last_fired_at_ms` / `fired_count` / `state`），不再存储第二份 `title`/`prompt`/`cron_expr`；
  - 模板注入的节点在 `flow.json` 中保留 `${inputs.*}` 占位符，触发和投影时动态插值，避免 `template_update_inputs` 与 Lead 抢写文件。
- **Codex/OAR 本地 MCP 免审批**：在 OAR / Codex 启动配置层信任 Perga 本地注入的 `perga` MCP server，避免子会话在受限审批策略下无法回报 `task_done` / `data_patch`。

### 2. 节点 = 步骤/关卡，会话 = 可复用的长寿工位
顺着 PR #112 的单向 `Task -> SessionId`，彻底把 **「活跃任务租约」** 与 **「历史追溯外键」** 拆开：
- **核心不变量**：`tasks.session_id` 在任务终结后仅作**只读追溯外键**（方便 UI 跳对话）；所有写锁、只读权限、状态投影改为查询 **当前未终结的活跃任务（`Pending / Running / Verifying`）**，不再使用会撞到历史行的 `_tasks.FirstOrDefault(t => t.SessionId == sessionId)`。
- **取消 `task_finished` 永久只读封存**：
  - 仅在 `Pending`（`waiting_task`）与 `Verifying`（`task_verifying`）两个短暂窗口冻结会话输入；
  - 任务一结算，释放会话活跃租约，会话立刻回归普通可写对话——人类随时可以点进去追问。
- **带原上下文复用已有会话**：
  - 当新节点（如 `recheck-a`）指定 `do: { kind: "run", session: "ses_reviewer_a", ... }` 时，内核校验该会话归属当前 Track 且当前未被其他活跃任务占用，然后将新任务租约挂到该会话上，**直接在其现有 `NativeThreadId` 上续发新一轮 turn（不清空 `NativeThreadId`、不升 `Epoch`）**；
  - `r1-review` 和 `r2-recheck` 在图上是两个清晰的无环步骤节点，但共享同一个 Reviewer 工位上下文。

### 3. 正交模型 `when × do` + 领域中性 `outcome`
- **`when`（触发条件，省略 = 提交即执行一次）**：
  - `{ after: string[] | Array<{ node: string, on?: Outcome | "any" }>, on?: Outcome | "any" }`：上游结算触发（省略 `on` 默认 `"ok"`）。复用 `tasks.run_count` 作为波次号，通过 `trigger_run_key = "<node>#<runCount>"` 保证重启幂等，**天然同时支持一次性上游和周期性 `cron` 上游**！
  - `{ cron: string, tz?: string, maxOccurrences?: number }`：周期定时触发，每周期推进一次新 `run_count`。
  - `{ at: string }`：一次性定时触发（ISO-8601）。
  - Stage 3 可按需扩展 `{ event: "mail.received" }` 等内核原生推送事件。
- **`do`（执行动作，三选一）**：
  - **`kind: "run"`**：执行带完成边界的契约步骤——既可派给会话（`session: "new"` 省略默认，或指定已有 `"ses_xxx"`），带可选 `role` / `access` / `provider` / `model` / `gate` / `prompt` / `acceptance`；也可执行 0-Token 确定性命令（`cmd: "..."`）。
  - **`kind: "wake"`**：轻量唤醒指定会话（`session: "lead" | "ses_xxx"`, `prompt`），无重型契约/Gate，本轮 turn 结束即以 `ok` 结算。
  - **`kind: "watch"`**：**0-Token 外部条件等待节点**（与任务同构，可挂在 `fix-r2` 之后、可分叉下游 `ok` / `attention` / `blocked`）：
    - **内置探针 `prChecks`**：复用 Perga 现成 VCS 适配器（`vcs_pr_checks`），支持绑定 `headSha`（避免刚 push 时 0 checks 误判绿灯），检查全绿 → `ok`，任一失败 → `attention`，超时 → `blocked`；
    - **通用命令探针 `cmd`**：覆盖 K8s 发布、训练任务、线上指标、行情阈值等任意长尾系统，遵循 UNIX 退出码约定：`0` = `ok`（达成），`75`（`EX_TEMPFAIL`）= 继续静默等待，其他非零 = `attention`（截取输出尾行作为 `outcomeSummary`）。
- **领域中性 `outcome`（收编旧 `SemanticVerdict`）**：
  - 统一为 **`ok` | `attention` | `blocked`**（`none` 表示尚未结算）：
    - `ok`：达标通过，放行默认下游；
    - `attention`：运行完成但需处理（评审打回、CI 红灯、指标越界、财报偏离），阻断默认 `ok` 下游，可触发专门的 `on: "attention"` 修复/归因分支，并在 `builtin:flow` 高亮待介入；
    - `blocked`：外部阻塞或探针超时，阻断下游并唤醒 Lead。
  - `task_done(outcome?, summary?)` 默认 `outcome = "ok"`；人工/Lead 验收覆写（`task_accept` / `task_reject`）直接映射更新同一套 `outcome` 语义，不再与 `SemanticVerdict` 并存两套结论字段。

### 4. `flow.json` 极简 Schema 与双场景示例

每个节点仅有 **5 个顶层字段**：`id`、`title`、`when`、`do`、`canceled`（及可选 `source: "agent" | "user" | "template"`）。

#### 示例 A：研发多轮交付 + 等 CI 绿灯自动复审 + 红灯自动修 + 复用原 Reviewer 上下文
```json
{
  "rev": 3,
  "nodes": [
    {
      "id": "fix-r2",
      "title": "修复 R1 拦截项并推送",
      "do": {
        "kind": "run",
        "role": "impl",
        "access": "read_write",
        "gate": "dotnet test Perga.sln",
        "prompt": "修复 R1 拦截项并推送到 PR #115 分支。"
      }
    },
    {
      "id": "wait-ci",
      "title": "等待 PR #115 CI",
      "when": { "after": ["fix-r2"] },
      "do": {
        "kind": "watch",
        "prChecks": 115,
        "interval": "60s",
        "timeout": "30m"
      }
    },
    {
      "id": "fix-ci",
      "title": "CI 红灯自动修复",
      "when": { "after": ["wait-ci"], "on": "attention" },
      "do": {
        "kind": "run",
        "role": "impl",
        "access": "read_write",
        "prompt": "拉取 PR #115 CI 失败日志并修复。"
      }
    },
    {
      "id": "recheck-a",
      "title": "复审 A（复用原上下文）",
      "when": { "after": ["wait-ci"] },
      "do": {
        "kind": "run",
        "session": "ses_reviewer_a",
        "role": "review",
        "access": "read_only",
        "prompt": "基于原上下文复审最新 diff。"
      }
    },
    {
      "id": "notify-lead",
      "title": "全过唤醒 Lead 准备合并",
      "when": { "after": ["recheck-a"] },
      "do": {
        "kind": "wake",
        "session": "lead",
        "prompt": "CI 已绿且复审通过，请向用户汇报准备合入。"
      }
    }
  ]
}
```
> 整条链跑完结算后，后台 GC 自动将这 5 个已完成节点归档进 SQLite（`archived_at_ms`），`flow.json` 恢复干净，准备迎接下一轮编排。

#### 示例 B：长期 OKR 周期巡检 + 越界自动归因（常驻不膨胀）
```json
{
  "rev": 1,
  "nodes": [
    {
      "id": "daily-ci-metrics",
      "title": "每日 CI 指标采集",
      "source": "template",
      "when": { "cron": "0 9 * * *", "tz": "Asia/Shanghai" },
      "do": { "kind": "run", "cmd": "${inputs.checkCmd}" }
    },
    {
      "id": "ci-breach-triage",
      "title": "指标越界自动归因",
      "source": "template",
      "when": { "after": ["daily-ci-metrics"], "on": "attention" },
      "do": {
        "kind": "run",
        "role": "triage",
        "access": "read_only",
        "prompt": "定位过去 24h Top3 失败用例并输出归因结论。"
      }
    }
  ]
}
```

### 5. 高 Taste 防丑护栏（正交独立，可率先单独上线）
- **运行期 Report 骨架锁**（增强 `TrackActor.cs:2950-2978`）：
  - 模板激活时，`report_commit` 禁止 AI 删除、移动、修改模板声明的 `perga-block widget`（含 `builtin:flow`），禁止运行期私插 `perga-block html`；
  - 阶段收编后彻底退役 `report_commit` 写入 `perga-block task`。
- **编译期 View 契约门禁 + 模板目录流**（增强 `PolicyVm.Compile` & `track_set`）：
  - 编译期用 `data.initial` + 空 `flow` 试跑全部 `report.views`，校验每个 `builtin:*` 视图严格符合前端契约，违规直接编译拒绝；
  - `track_set` 增加 `path` 参数支持直接加载本地模板包目录 + `dryRun: true` 预览校验；
  - 不写脆弱正则扫描 `./widgets/*.html` 色值，靠无样式参数的封闭原生组件（`builtin:flow` / `stepper` / `list` / `table` / `detail`）兜底审美下限。

---

## 四、View 天然派生：`builtin:flow`

- `GET /api/v1/tracks/{trackId}/views` 统一透出 `{ templateId, dataRev, flowRev, flow, views, widgets, diagnostics }`，其中 `flow` 包含：
  - `nodes`：活跃节点声明（`id`, `title`, `when`, `do`）+ 最新运行态（`runCount`, `status`, `outcome`, `outcomeSummary`, `model`, `sessionId`, `gates`, `nextFireAtMs`, `firedCount`, `waitingOn`, `watchProgress`）；
  - `archived`：最近后台 GC 归档的一次性节点摘要（供折叠查看，不干扰主画布）。
- 前端原生出版物组件 **`builtin:flow`**：
  - 按 `when.after` 自动拓扑成图，零配置呈现 5 种状态：已完成（`✓`）、运行中（跳动点）、等待前置、等待外部探针（如 `PR #115 · 3/5 checks · 4m`）、**需介入（橙色高亮 `attention` / `blocked` + 摘要）**；
  - 常驻 `cron` 规则收口在底部轻量脚注区（含下次触发时间与已运行次数）；
  - 点击任意节点在下方出版物详情区查看最新产出摘要、Gate 脚注日志，并一键直达对应 `Session` 对话（历史过程天然在对话中）。
  - 诚实边界：每个 `tasks` 行保留最新一次 Run 的结构化结果（不新建沉重的 `flow_runs` 表），多轮细节通过对应 `Session` 对话历史追溯。

---

## 五、数据库迁移（`0025_track_flow.sql`）与独立交付路线

遵循 `AGENTS.md`（`0001`–`0024` 字节冻结；当前模板未上线，不做历史回填）：

```sql
ALTER TABLE tracks ADD COLUMN flow_spec_json TEXT NOT NULL DEFAULT '{"rev":0,"nodes":[]}';
ALTER TABLE tracks ADD COLUMN flow_rev INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tracks ADD COLUMN flow_file_sha256 TEXT;

ALTER TABLE tasks ADD COLUMN trigger_run_key TEXT;
ALTER TABLE tasks ADD COLUMN outcome TEXT NOT NULL DEFAULT 'none';
ALTER TABLE tasks ADD COLUMN outcome_summary TEXT;
ALTER TABLE tasks ADD COLUMN archived_at_ms INTEGER;

ALTER TABLE track_schedules ADD COLUMN node_key TEXT;
```

### 拆分为 3 个正交、小步快跑的 PR（先功能干净，再删旧路，最后加护栏）

| PR | 范围 | 独立验收 |
|---|---|---|
| **PR 1：统一 Flow 底座 + `watch` + 会话复用 + 只读 `builtin:flow`** | • 迁移 `0025_track_flow.sql`；<br>• `{DataDir}/tracks/{trackId}/flow.json` 托管 + `flow_commit({dryRun, expectedFlowRev})` + 防误删活跃节点（取消须显式 `canceled: true`）+ 后台安全 GC（`archived_at_ms` + hash 保护不覆写草稿）；<br>• `TrackFlowReconciler`（`when: after/cron/at` × `do: run/wake/watch`，含 `prChecks` 与 `cmd` 探针、`outcome: ok/attention/blocked`）；<br>• 拆开活跃租约与历史外键（解除 `task_finished` 永久封存 + 复用原 `NativeThreadId` 不清上下文）；<br>• Codex/OAR 本地 `perga` MCP 免审批；<br>• `GET /views` 透出 `flow` + 前端纯只读出版物级 `builtin:flow`（含多行均衡周期卡片，并彻底移除 Report 编辑/`Dispatch Worker`/节点取消按钮）。 | 1) 误删活跃节点被 `flow_commit` 拦截，显式 `canceled: true` 正常取消；<br>2) 终结孤立链由后台自动 GC 归档并清理 `flow.json`（不覆写脏草稿）；<br>3) 端到端跑通 `fix -> watch(prChecks) -> attention(fix-ci) / ok(recheck 复用原会话) -> wake(lead)`；<br>4) 跑通 `cron -> cmd(attention) -> triage` 跨波次幂等周期流水线。 |
| **PR 2：旧入口全量收编 + 三套模板瘦身** | • 退役 `perga-block task` 写入与 `WorkerDispatch` 伪造 `report_commit`；<br>• 清理后端无用 Report 单块编辑端点与双套 `SemanticVerdict`；<br>• `schedule_set` / UI 日程 / `task_cancel` / 模板初始 `flow` 蓝图全量收编至 `TrackFlowReconciler`（`track_schedules` 降级为纯触发时钟表）；<br>• 重构 `templates/agents-delivery/`、`equity-research/`、`okr-loop/` 直接基于 `flow` + `builtin:flow`。 | 1) Report 彻底消灭底部孤立任务卡片；<br>2) `template_update_inputs` 动态插值不改写 `flow.json`；<br>3) 三套内置模板均由统一 `flow` 驱动与呈现。 |
| **PR 3：模板防呆护栏（零迁移）** | • 运行期 Report 骨架锁（锁模板 `widget`/`flow` 块，禁运行期私插 `perga-block html`）；<br>• `PolicyVm.Compile` 试跑 `report.views` 校验 `builtin:*` 契约；<br>• `track_set(path, dryRun: true)` 本地模板目录校验与预览。 | 1) AI 试图删改模板 `widget` 或私插 `html` 被 `PolicyDenied` 拦截；<br>2) 坏视图模板在 `track_set(path, dryRun: true)` 阶段即被编译拦截。 |

### PR 2 Stage 1 implementation

Report writes validate the resulting document and preserve historical task blocks on
read. The Kernel creates new contracts through Flow and retains existing task
history. Flow task cancellation goes through the same commit and shutdown path
as an edit to a node's `canceled` field.

Applying a template seeds missing blueprint nodes, with template provenance, into
the existing Flow. A previously committed node keeps its definition and history.
Template application persists Flow, tasks and clocks within the template update
transaction before projecting the canonical file. Input patches resolve Flow
placeholders at execution time and preserve the committed blueprint text and
uncommitted drafts. The clock runner selects only Flow-owned rows. Released
migration files and historical database rows remain readable.
