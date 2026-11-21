# Perga 模板系统底层架构设计（v2.1 · 封闭原语 × 开放模板包 × 方案 B 统一 VO）

> **目标**：结合 Perga 现有内核（`Perga.PolicyVm`、`Perga.ReportDoc`、`Perga.Kernel`、`Perga.Storage.Sqlite`、`fe/`），将「单向软件交付」「长期个股投研」「长期 OKR Loop」三类典型场景抽象为一套**边界清晰、强校验、单入口多文件可拆、极简且可无限扩展**的模板平台底座。

---

## 零、工程可行性评估：这套设计在当前 Perga 代码库上好实现吗？

结合实际代码检视（已落地原型验证），整体**非常顺滑、与现有代码接缝高度契合**，无任何推倒重来的大改动：

| 模块 | 实现难度 | 代码库真实接缝与实测结论 |
|---|---|---|
| **多文件模板包 + `setup(inputs)` + Schema Fail-Closed 校验 + View 投影** | **低（已跑通）** | `Perga.PolicyVm` 已内置 Jint 4.2.2 ES Module 引擎，只需挂 30 行内存 `BundleModuleLoader`（`TemplateBundle.cs`），并以 `// @perga-bundle-v1` 打包存进现有 `TemplateSnapshotSource` 字段，**零 SQLite 表结构迁移**即可完整支持目录包 + 单文件兼容。 |
| **Track Data 持久化与热更新** | **低（已跑通）** | `Track.TemplateState` 已完整打通 SQLite → `TrackActor` → HTTP/WS → `fe/core`；在其上挂 `perga_data_patch`（MCP + `PATCH /api/v1/tracks/{id}/data`）与 `GET /api/v1/tracks/{id}/views` 即可实现 0-Token 数据刷新与实时投影。 |
| **方案 B 双轨 VO + 无框沉浸 4 消息 HTML Widget Bridge** | **低（已跑通）** | 仓库已有 React 文字骨架（`prose`/`table`/`checkpoints`）；需注意一个真实边界：**现有 `/api/v1/tracks/{id}/files/raw` 返回 `Content-Security-Policy: sandbox`（禁用脚本）**，而模板包自带的图形 Widget 通过 `<iframe srcDoc={html} sandbox="allow-scripts">` 承载，既零权限隔离、又不污染用户工作区 git 树，且 4 消息 Bridge（`data`/`theme`/`resize`/`intent`）+ 透明底 + Seamless Chrome 直接消除传统 iframe 的底部白色方框。 |
| **Phase 3：统一 Trigger 引擎（`run_cli` / `data_change` / `schedule_at`）** | **中（后续重点）** | 需复用 `ScheduleService` 持久化唤醒、补齐 CLI 环境变量白名单沙箱、`maxDepth=2` 防递归与 Host 重启幂等恢复测试。 |

---

## 一、现状诊断：现有模板实现为什么不好用？

检视仓库现有三个内置模板（[dev.perga.js](../../templates/dev.perga.js)、[investment-research.perga.js](../../templates/investment-research.perga.js)、[long-term-goal.perga.js](../../templates/long-term-goal.perga.js)）与运行时契约（[Contracts.cs](../../libs/Perga.PolicyVm/Contracts.cs)、[perga-template.d.ts](../../templates/perga-template.d.ts)），存在 4 个结构性瓶颈：

1. **Report 沦为纯文本倾倒场**：`initialMarkdown` 只有 4～5 个空泛 H1 标题，AI 只能反复把状态、数字、进度手抄进长篇 Markdown，极易陈旧漂移。
2. **`TemplateState` 对人不可见、无强校验、不可绑定 VO**：`Track.TemplateState` 目前是无类型 `JsonElement`，只拼进 prompt 给模型看，前端无法订阅，AI 写错字段也不会被拦截。
3. **`schedules` 只能定时唤醒 Lead LLM**：现有 `ScheduleSpec` 只有 `(cron, wakePrompt)`，导致每日 CI 采样、股价刷新等确定性采集每次都烧 Token，且无法表达数据越界触发（`successRate < 98.5%`）或前置任务完成自动派发。
4. **图形扩展与原生交互割裂**：现有 `perga-block html` 是写死高度、带底框和底部 `Open` 栏的哑 iframe，无法订阅 Data、同步主题或打开 Perga 原生阅读器与 Worker 对话。

---

## 二、第一性原理：划清「封闭原语」与「开放模板」的边界

设计模板平台最容易犯的错，是把内核系统事实与业务领域概念揉成一个大杂烩（例如捏出一个包含 `dateOrOrder`、`group`、`verdict`、`expectations` 的“上帝结构体” `TrackSlot`）：结果每个模板被迫背一堆无关的可选字符串，内核既没法校验，也违背了 [AGENTS.md](../../AGENTS.md)「通用组件不耦合特定模板语义」的原则。

因此本架构先确立一条**封闭 vs 开放分割线**：
> **影响安全边界、执行权威、持久化一致性或原生 UI 联动的 → 封闭（Kernel 原语，强类型、不可篡改）**；
> **属于具体业务领域的概念（阶段、财报、目标价、KR、Loop、评估词表、专用图形）→ 开放（Template 自主声明 Schema、投影与规则，由内核按模板声明做 Fail-Closed 校验）**。

| 分层 | **封闭部分（Kernel 提供的有限原语，强校验）** | **开放部分（Template 自由定义）** |
|---|---|---|
| **01 · Logic** | 有限触发源（`cron` / `data_change` / `task_settled` / `schedule_at`）、3 种副作用（`run_cli` / `dispatch_worker` / `wake_lead`）、硬护栏（路由 `enforce: true`、CLI 环境变量白名单、最大递归链深 `maxDepth`） | 业务触发条件 `when(data, event)`、角色模型路由映射、Prompt 与 CLI 参数拼接 |
| **02 · Data** | **系统事实实体**（AI 不可伪造）：`Task / Session`（真实 `model` / `sessionId` / `status` / `gate`）、`Run`（触发审计）、`FileRef`、自动元字段 `_meta.{updatedAt, revision, source}` | **模板自定义业务 Schema**：开发只声明 `stages[]`，投研声明 `valuation + events[]`，OKR 声明 `krs[] + loops[]`；用 `ref("task")` / `ref("file")` 关联封闭系统事实 |
| **03 · VO（方案 B）** | **统一 VO 契约 + 极小闭集 React 原生文字骨架**（`prose` / `table` / `stepper` / `list`）+ **封闭 4 消息 HTML Bridge 协议**（`data` / `theme` / `resize` / 只读 `intent`） | **Report 排版组装 + 纯函数投影 `project(data, kernel)` + 无限扩展的无框沉浸式 HTML 图形 Widget**（K 线事件图、估值轴、架构图、热力图等） |

> **交互式架构图（HTML/SVG 渲染版）**：点击打开 **[template-architecture-diagram.html](template-architecture-diagram.html)**（支持切换「全景 / ① 0-Token 采集与 Schema 校验 / ② 越界拉起 Worker / ③ 方案 B 双轨 VO 与深潜」链路，或点击任意节点查看边界与职责；该图亦已内嵌在当前 Track Report 的 **「架构总览」** 中）。

---

## 三、三层详细设计

### 1. Data 层：封闭系统事实 × 开放业务 Schema（彻底淘汰上帝对象）

#### A. 封闭的 Kernel System Entities（内核唯一真相，AI 严禁手填）
- **`kernel.tasks` / `kernel.sessions`**（复用现有 Perga 内核模型）：真实 `taskKey`、`sessionId`、`model`、`provider`、`access`、`status`、`gateResult`（命令、退出码、输出摘要）。
- **`kernel.runs`**：每次 Trigger 触发时间、执行类型、结果与错误审计。
- **`_meta` 自动元信息**：每条开放业务记录由内核自动维护 `_meta.updatedAt`（UTC 时间戳，前端直接渲染为 `更新时间`，杜绝 AI 手填假时间）、`_meta.revision`（CAS 乐观锁）与 `_meta.source`（`cli` 或 `sessionId`）。

#### B. 开放的 Template Data Store（模板声明 Schema + 强外键钩子）
内核不认识什么叫 `KR`、什么叫 `Expect`、什么叫 `valuation`。模板在 `.perga.js` 中用轻量强类型 Schema 声明自己的集合，并可使用内核提供的两个**封闭引用类型**：
- `t.ref("task")`：外键指向 `kernel.tasks[taskKey]`。渲染时内核自动关联真实的 `model`、`sessionId`（点击直达 Worker 对话）与 `gate` 状态。
- `t.ref("file")`：外键校验路径落在当前 Track Workspace 内且无越界（`..`）。

#### C. 「写错了怎么办？」—— 三道 Fail-Closed 校验防线
1. **模板加载期（`perga_track_set` / `dryRun`）**：校验 `triggers` / `routing` 语法，并用 `initialData` 试跑所有 VO 的 `project(data, emptyKernel)`，验证产出 Props 严格满足封闭契约；不合法直接拒绝加载。
2. **运行期写入期（`perga_data_patch` / CLI stdout 回写）**：
   - 按模板声明的 Schema 做**原子强校验（Fail-Closed）**。
   - AI 把 `verdict` 写成 `"还不错"`（不在枚举内）、把 `date` 写成 `"11月下旬"`（非 `YYYY-MM-DD`）、试图篡改 `_meta.updatedAt` 或填不存在的 `taskRef` → **整次写入直接拒绝落盘**，返回精确字段路径与合法取值，驱动 AI 立即自纠。
   - CLI 输出脏 JSON 或不匹配目标路径 Schema → **保留上一个合法快照不覆盖**，记一次失败 `Run`（可配置告警唤醒 Lead）。
3. **运行期投影/渲染期**：若某条 `project()` 在极端数据下抛错或产出非法 Props，隔离在单个 VO Block 内展示可诊断错误，不崩溃整篇 Report，也绝不静默吞错。

---

### 2. VO 层（最终决策：方案 B —— 上层契约统一，底层按「文字骨架 vs 图形扩展」双轨挂载）

**决策依据**：对比了「方案 A（除 Markdown 外全走 iframe）」与「方案 B（上层契约统一，底层双轨挂载）」在维护成本、性能、拓展性与功能边界上的真实权衡后，确定采用 **严格克制的方案 B**——不按“官方/第三方”切分，而是按 **文字阅读骨架 vs 图形可视化** 切一刀：

#### A. 极小闭集：React 原生文字骨架（不再继续膨胀）
只保留强依赖 **`Ctrl+F` 页内搜索、移动端 `__NC_BUNDLED__` 渲染、内联 `<ProseBlock>` 蓝色引用与悬停预览、0ms 首屏零闪烁、以及浮层不被 iframe 裁切** 的 4 个阅读骨架：
- `prose`：叙述正文 + 蓝色文字引用（对人类只读，仅 AI 可编辑）。
- `table`：出版物表格（天然覆盖 4 列混合 KR 总账、Expect 对比表、触发规则表）。
- `stepper`：有限有序阶段横轨（透明底 Flex 细线、无选中下划线、底部脚注式 Gate）。
- `list`：纵向条目列表（支持可选分组与展开详情，覆盖无终点事件时间线与 OKR Loop Roadmap）。
- *(注：仓库已有的原生 SVG [candles/public.tsx](../../fe/web/src/features/report/candles/public.tsx) 与 [series/public.tsx](../../fe/web/src/features/report/series/public.tsx) 保持现状可用，但**今后不再为了新图表修改 C# `ReportDoc` 或新增前端 React 组件**。)*

#### B. 无限扩展：无框沉浸式 HTML 图形 Widget（Data-Bound HTML Bridge）
所有新图形、领域可视化（估值区间轴 `range-axis`、财报事件叠加 K 线、架构图、热力图、CI 耗时火焰图、桑基图等）**100% 走统一的沙箱 HTML Widget**。

在现有 [html/public.tsx](../../fe/web/src/features/report/html/public.tsx)（保持 `sandbox="allow-scripts"`、**永不加 `allow-same-origin`**、零 cookie/API 权限）之上，增加一层极薄的 **封闭 4 消息 `postMessage` Bridge**，彻底消除传统 iframe 的「孤岛感」与「底部白色方框」：

| 方向 | 消息 | 职责与沉浸式保障 |
|---|---|---|
| **Host → Widget** | `perga:data` | 推送模板 `project(data, kernel)` 算好的只读 JSON；Data 更新即热推送，无需重写 HTML |
| **Host → Widget** | `perga:theme` | 实时注入当前 `light/dark` 模式与 Perga 纸面 CSS 变量（`--bg` / `--ink` / `--hairline` / `--accent` / `--font-serif`），配合 iframe `background: transparent` 消除一切底框色差 |
| **Widget → Host** | `perga:resize` | 上报精确 `scrollHeight`（带上限防撑爆），像素级贴合高度，**彻底消灭底部多余白框与双滚动条** |
| **Widget → Host** | `perga:intent` | 仅放行封闭只读导航意图：`open_file(path)`（打开原生文件阅读器）、`open_session(sessionId)`（打开对应 Worker 对话）；Host 校验归属当前 Track，**绝不开放任何写操作** |

此外，`widget` 模式默认采用 **Seamless Chrome**（去掉传统 `html` 块底部强制的 `title + Open` 外框栏，仅在悬停右上角时浮现极轻的全屏查看图标），使 HTML 图形与上下 React 文字骨架融为同一张出版物纸面。

#### C. 上层契约 100% 统一
在 Report 与模板眼里，无论是 React 文字骨架还是 HTML 图形 Widget，都通过统一的 `bind` + `project(data, kernel)` 绑定 Data（复用 [document/public.tsx](../../fe/web/src/features/report/document/public.tsx#L75) 的 `resolveOverlay` 接缝）：当每日 CLI 刷新 `data.krs` 或股价时，WebSocket 推送 Data 增量 → 对应 Block 实时热更新，**全程 0 LLM Token、不改动 Report `docRev`**。

---

### 3. Logic 层：封闭执行原语 × 开放业务规则

将分散的 `schedules`、`tasks.routing`、`tasks.onSettled`、`guardToolCall` 统一收口为声明式 `triggers[]` + `routing[]`，坚持 **「确定性 CLI 先行，异常/决策才唤醒 LLM」**：

- **封闭触发源**：
  - `{ kind: "cron", expr, timezone? }`
  - `{ kind: "data_change", path }`
  - `{ kind: "task_settled", taskKey?, role? }`（直接绑定内核真实 Task 状态，消除模糊的 `slot_settled`）
  - `{ kind: "schedule_at", dataPath, dateField }`（监听开放业务数据中强类型日期字段到期，如财报发布日）
- **封闭副作用动作（仅 3 种）**：
  - `{ kind: "run_cli", command, writeDataPath? }`（受控沙箱 + 显式环境变量白名单 + 硬超时）
  - `{ kind: "dispatch_worker", role, taskKey, goal }`（统一经 `ApplyTemplateRoutingCore`，`enforce: true` 硬拦截违规模型）
  - `{ kind: "wake_lead", prompt }`
- **三个工程不变量**：
  1. **展示与执行单一数据源**：前端「自动监测 · N」与未开始阶段的调度表直接从编译后的 `manifest.triggers` 投影，绝不手写两套。
  2. **防递归与幂等**：`data_change` 施加最大链深（`maxDepth = 2`）与冷却去重，防止写 Data 触发死循环。
  3. **崩溃可恢复**：Trigger 唤醒与执行审计落盘 SQLite，Host 重启不漏触发、不重复派发 Worker。

---

## 四、编写体验（DX）与单入口多文件模板包规范（v2.1 实装）

从严苛的用户与 AI 编写者视角审视，我们在 v2.1 额外收口了 4 个关键 DX 约束并已全部实装进 `Perga.PolicyVm`：

1. **单入口约定 + 多文件目录包（兼容简单单文件）**：
   - 简单模板继续支持单个 `*.perga.js`；复杂模板采用目录包，约定**唯一入口 `template.perga.js`**，按职责拆分为 `report.md`（纯 Markdown，告别 JS 字符串数组拼接）、`schema.js`、`views.js`、`triggers.js`、`widgets/*.html`、`scripts/*`；`BundleModuleLoader` 仅允许包内相对路径 `import`。
2. **统一纯 JS `setup(inputs)`，彻底消灭假字符串插值 `"${inputs.xxx}"`**：
   - 模板不再混用假插值字符串与回调函数；所有配置均在 `setup(inputs)` 内用原生 JS 变量引用 `inputs.*`。
3. **封闭且显式的 `event` 契约（零魔法隐式字段）**：
   - `cron` → `{ firedAt }`；`data_change` → `{ path, prev, next, at }`（如涨跌幅显式用 `(event.next - event.prev) / event.prev` 计算）；`task_settled` → `{ taskKey, role, status, sessionId }`；`schedule_at` → `{ path, id, date, record }`。
4. **编译期 / `dryRun` 跨文件一键静检**：
   - 加载模板时自动校验：`report.md` 中所有 `perga-block widget` 引用的 `view` 必须在 `views.js` 中存在、所有 `./widgets/*.html` 必须在包内存在、`data.initial()` 必须通过 `data.schema(t)`、且每个 `view` 试跑不抛错。

### 已迁移落地的三个真实多文件模板包

三个打磨好的场景已直接按本规范迁入仓库 `templates/` 目录，并在 `Perga.PolicyVm.Tests` 全量通过打包、编译、Schema 校验与 View 投影测试：

1. **[agents-delivery/](../../templates/agents-delivery/template.perga.js)**：`template.perga.js` + [report.md](../../templates/agents-delivery/report.md) + [schema.js](../../templates/agents-delivery/schema.js) + [views.js](../../templates/agents-delivery/views.js) + [triggers.js](../../templates/agents-delivery/triggers.js)（原生 `builtin:stepper`）。
2. **[equity-research/](../../templates/equity-research/template.perga.js)**：`template.perga.js` + [report.md](../../templates/equity-research/report.md) + [schema.js](../../templates/equity-research/schema.js) + [views.js](../../templates/equity-research/views.js) + [triggers.js](../../templates/equity-research/triggers.js) + 无框沉浸 HTML 图形 Widget [widgets/range-axis.html](../../templates/equity-research/widgets/range-axis.html)（方案 B 典型混搭：HTML 图形轴 + 原生 `builtin:list` 事件时间线）。
3. **[okr-loop/](../../templates/okr-loop/template.perga.js)**：`template.perga.js` + [report.md](../../templates/okr-loop/report.md) + [schema.js](../../templates/okr-loop/schema.js) + [views.js](../../templates/okr-loop/views.js) + [triggers.js](../../templates/okr-loop/triggers.js)（原生 `builtin:table` 四列 KR 总账 + 原生 `builtin:list` 分组 Loop Roadmap）。

---

## 五、三个场景设计对照（参考原稿）

可以看到：淘汰 `TrackSlot` 后，每个模板**只声明自己真正需要的业务字段**，类型严格可校验，VO 统一用 `project(data, kernel)` 映射到原生文字骨架或无框 HTML 图形 Widget。

### 示例 1：单向阶段交付（`agents-delivery.perga.js`）

```js
export default defineTemplate({
  id: "agents-delivery",
  title: "AGENTS.md 阶段交付协议",
  inputs: {
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "审查强制模型" },
    testCmd: { type: "string", default: "dotnet test Perga.sln", label: "回归门禁命令" },
  },

  // 1. Data（开放业务 Schema：只有 5 个真正需要的字段，零冗余字符串）
  data: {
    schema: (t) => ({
      stages: t.array(t.object({
        id: t.id(),
        title: t.string(),
        role: t.enum(["design", "impl", "review"]),
        taskRef: t.ref("task").optional(),   // 封闭外键 → 自动带出真实 model / sessionId / gates
        summary: t.markdown().optional(),    // 正文可含蓝色文件引用
      })),
    }),
    initial: () => ({
      stages: [
        { id: "design", title: "设计", role: "design" },
        { id: "impl-1", title: "实现一", role: "impl" },
        { id: "impl-2", title: "实现二", role: "impl" },
        { id: "review-l1", title: "L1 Review", role: "review" },
        { id: "review-l2", title: "L2 Review", role: "review" },
      ],
    }),
  },

  // 2. VO（纯原生文字骨架：prose + stepper，0ms 渲染、支持 Ctrl+F 与内联蓝色引用）
  report: {
    humanReadOnly: true,
    initialMarkdown: [
      "# Target",
      "",
      "用一两句 Markdown 锚定唯一交付目标与验收口径。",
      "",
      "# 交付阶段",
      "",
      '```perga-block widget',
      '{"use":"builtin:stepper","view":"deliveryStepper"}',
      '```',
    ].join("\n"),
    views: {
      deliveryStepper: (data, kernel) => ({
        items: data.stages.map((s) => {
          const task = s.taskRef ? kernel.tasks[s.taskRef] : undefined;
          return {
            id: s.id,
            title: s.title,
            status: task?.status ?? "pending",
            sessionId: task?.sessionId,
            model: task?.model,
            gates: task?.gates ?? [],
            bodyMarkdown: s.summary,
          };
        }),
      }),
    },
  },

  // 3. Logic（模型路由硬约束 + 前置任务完成自动派发）
  tasks: {
    requireGateForReadWrite: true,
    routing: [
      { when: { role: "review" }, model: "${inputs.reviewModel}", access: "read_only", enforce: true },
      { when: { role: "impl" }, access: "read_write" },
    ],
  },
  triggers: [
    {
      id: "auto-l2-review",
      label: "实现二与 L1 Review 完成后自动派发 L2 独立审查",
      on: { kind: "task_settled" },
      when: ({ kernel }) =>
        kernel.tasks["impl-2"]?.status === "done" &&
        kernel.tasks["review-l1"]?.status === "done" &&
        !kernel.tasks["review-l2"],
      action: {
        kind: "dispatch_worker",
        role: "review",
        taskKey: "review-l2",
        goal: "执行第二轮独立审查（全量 Diff 与边界复核）并回写 stages[review-l2].summary。",
      },
    },
    {
      id: "final-regression",
      label: "L2 Review 通过后运行全量回归门禁",
      on: { kind: "task_settled", taskKey: "review-l2" },
      when: ({ event }) => event.status === "done",
      action: { kind: "run_cli", command: "${inputs.testCmd}" },
    },
  ],
});
```

---

### 示例 2：个股长期投研（`equity-research.perga.js` — NVDA 示例）

```js
export default defineTemplate({
  id: "equity-research",
  title: "个股长期投研",
  inputs: {
    ticker: { type: "string", default: "NVDA", label: "标的代码" },
    reviewModel: { type: "string", default: "gpt-6.2-soar", label: "异动独立复核模型" },
  },

  // 1. Data（强类型数字、严格 ISO 日期、强枚举 verdict，写错立即 Fail-Closed 拦截）
  data: {
    schema: (t) => ({
      valuation: t.object({
        unit: t.string(),
        currentPrice: t.number(),
        targets: t.array(t.object({
          price: t.number(),
          emphasis: t.boolean().optional(),
          assumption: t.string(),
        })),
      }),
      events: t.array(t.object({
        id: t.id(),
        date: t.isoDate(),                                          // 强制 YYYY-MM-DD
        title: t.string(),
        verdict: t.enum(["pending", "beat", "inline", "miss"]),     // 强枚举，杜绝自由字符串
        taskRef: t.ref("task").optional(),
        docRef: t.ref("file").optional(),
        summary: t.markdown().optional(),
        expects: t.array(t.object({
          metric: t.string(),
          expected: t.string(),
          actual: t.string().optional(),
          status: t.enum(["pending", "beat", "inline", "miss"]),
        })).optional(),
      })),
    }),
    initial: () => ({
      valuation: {
        unit: "$",
        currentPrice: 137.5,
        targets: [
          { price: 118, assumption: "云厂 CapEx 放缓、ASIC 分流加速，毛利率回落至 70%" },
          { price: 172, emphasis: true, assumption: "Blackwell / Rubin 按期迭代，DC 维持 ~35% 增速" },
          { price: 225, assumption: "推理 Token 指数级扩张，主权 AI 放量" },
        ],
      },
      events: [], // 临近财报/事件前由 Lead 动态 append 并填好事前 expects
    }),
  },

  // 2. VO（方案 B 典型混搭：图形轴走无框 HTML Widget，事件时间线走原生 list）
  report: {
    humanReadOnly: true,
    initialMarkdown: [
      "# 价格区间",
      "",
      '```perga-block widget',
      '{"use":"./widgets/range-axis.html","view":"valuationAxis","chrome":"seamless"}',
      '```',
      "",
      "# 事件时间线",
      "",
      '```perga-block widget',
      '{"use":"builtin:list","view":"eventChronicle","showTriggers":true}',
      '```',
      "",
      "# 深度分析正文",
      "",
      "持续维护核心护城河、行业格局与证伪红线，拆解底稿通过蓝色文字引用内联。",
    ].join("\n"),
    views: {
      valuationAxis: (data) => data.valuation,
      eventChronicle: (data, kernel) => ({
        items: [...data.events]
          .sort((a, b) => b.date.localeCompare(a.date))
          .map((e) => {
            const task = e.taskRef ? kernel.tasks[e.taskRef] : undefined;
            return {
              id: e.id,
              meta: e.date,
              title: e.title,
              badge: e.verdict,
              sessionId: task?.sessionId,
              model: task?.model,
              bodyMarkdown: e.summary,
              detailTable: e.expects?.length
                ? { columns: ["指标", "事前预期", "实际兑现", "判定"], rows: e.expects }
                : undefined,
            };
          }),
      }),
    },
  },

  // 3. Logic（零 Token 定时刷价 → 异动/到期才拉起指定模型核验 Expect）
  tasks: {
    routing: [
      { when: { role: "earnings" }, model: "claude-opus-4", access: "read_only" },
      { when: { role: "event_review" }, model: "${inputs.reviewModel}", access: "read_only", enforce: true },
    ],
  },
  triggers: [
    {
      id: "weekly-price-refresh",
      label: "每周一 08:30 刷新现价与同行快照",
      on: { kind: "cron", expr: "30 8 * * 1", timezone: "America/New_York" },
      action: {
        kind: "run_cli",
        command: "python scripts/equity_snapshot.py --ticker ${inputs.ticker}",
        writeDataPath: "valuation.currentPrice",
      },
    },
    {
      id: "price-shock-triage",
      label: "单日涨跌幅 ≥ 8% 时拉起独立大事件排查",
      on: { kind: "data_change", path: "valuation.currentPrice" },
      when: ({ event }) => Math.abs(event.changePct ?? 0) >= 8,
      action: {
        kind: "dispatch_worker",
        role: "event_review",
        taskKey: ({ event }) => `shock-${event.date}`,
        goal: ({ inputs, event }) =>
          `${inputs.ticker} 单日异动 ${event.changePct}%：新增事件条目、核查核心证伪红线并归档快评。`,
      },
    },
    {
      id: "settle-due-event",
      label: "预挂财报/大事件到期后自动按事前 Expect 核查兑现",
      on: { kind: "schedule_at", dataPath: "events", dateField: "date" },
      when: ({ event }) => event.record.verdict === "pending",
      action: {
        kind: "dispatch_worker",
        role: "earnings",
        taskKey: ({ event }) => `event-${event.record.id}`,
        goal: ({ event }) =>
          `结算事件 '${event.record.title}'：抓取原始公告，逐条核对 expects 并更新 verdict。`,
      },
    },
  ],
});
```

---

### 示例 3：长期 OKR Loop 追踪（`okr-loop.perga.js` — CI/CD 示例）

```js
export default defineTemplate({
  id: "okr-loop",
  title: "长期 OKR 优化闭环",
  inputs: {
    checkCmd: { type: "string", default: "python scripts/ci_daily_check.py", label: "每日指标采集脚本" },
    triageModel: { type: "string", default: "gpt-6.2-soar", label: "异常归因与回滚模型" },
  },

  // 1. Data（混合型 KR + 强枚举优先级的非时间序 Loop；更新时间由 _meta.updatedAt 自动提供）
  data: {
    schema: (t) => ({
      krs: t.array(t.object({
        code: t.string(),                                          // 如 "KR1"
        label: t.markdown(),                                       // 支持定性里程碑内联蓝色文件引用
        current: t.string(),
        numericValue: t.number().optional(),                       // 供 Trigger 做确定性阈值比较
        target: t.string(),
        status: t.enum(["met", "on_track", "at_risk"]),
      })),
      loops: t.array(t.object({
        id: t.id(),
        title: t.string(),
        state: t.enum(["active", "queued", "closed", "rolled_back"]),
        priority: t.enum(["P0", "P1", "P2"]).optional(),
        verdict: t.string().optional(),
        taskRef: t.ref("task").optional(),
        summary: t.markdown().optional(),
        localExpects: t.array(t.object({                           // 仅存本实验局部预期，绝不复述全局 KR
          metric: t.string(),
          expected: t.string(),
          actual: t.string().optional(),
          status: t.enum(["pending", "beat", "inline", "miss"]),
        })).optional(),
      })),
    }),
    initial: () => ({
      krs: [
        { code: "KR1", label: "P95 执行时长", current: "14.8 min", numericValue: 14.8, target: "≤ 8.0 min", status: "at_risk" },
        { code: "KR2", label: "过去 24h 成功率", current: "96.4%", numericValue: 96.4, target: "≥ 98.5%", status: "at_risk" },
      ],
      loops: [],
    }),
  },

  // 2. VO（纯原生出版物骨架：1 句稳定 Objective + 4 列 KR table + 分组 Loop list）
  report: {
    humanReadOnly: true,
    initialMarkdown: [
      "# Objective",
      "",
      "用一句话锚定本季度稳定的核心目标。",
      "",
      "# Key Results",
      "",
      '```perga-block widget',
      '{"use":"builtin:table","view":"krLedger"}',
      '```',
      "",
      "# 优化路线图",
      "",
      '```perga-block widget',
      '{"use":"builtin:list","view":"loopRoadmap","showTriggers":true}',
      '```',
    ].join("\n"),
    views: {
      krLedger: (data) => ({
        columns: ["Key Result", "当前", "目标", "更新时间"],
        rows: data.krs.map((kr) => [
          `${kr.code} ${kr.label}`,
          kr.current,
          kr.target,
          kr._meta.updatedAt, // 内核自动维护，AI 不可手填
        ]),
      }),
      loopRoadmap: (data, kernel) => ({
        groups: [
          { key: "active", title: "当前优化 Loop" },
          { key: "queued", title: "下一轮 Roadmap（按优先级）" },
          { key: "closed", title: "已闭环验证" },
          { key: "rolled_back", title: "已回滚实验" },
        ],
        items: data.loops.map((lp) => {
          const task = lp.taskRef ? kernel.tasks[lp.taskRef] : undefined;
          return {
            id: lp.id,
            group: lp.state,
            meta: lp.priority ?? lp._meta.updatedAt,
            title: lp.title,
            badge: lp.verdict,
            sessionId: task?.sessionId,
            model: task?.model,
            bodyMarkdown: lp.summary,
            detailTable: lp.localExpects?.length
              ? { columns: ["局部观测", "预期", "实测", "判定"], rows: lp.localExpects }
              : undefined,
          };
        }),
      }),
    },
  },

  // 3. Logic（每日 09:00 零 Token CLI 刷新 KR；越界触发归因；Loop 闭环重排 Roadmap）
  tasks: {
    routing: [
      { when: { role: "triage" }, model: "${inputs.triageModel}", access: "read_only", enforce: true },
      { when: { role: "optimize" }, model: "claude-opus-4", access: "read_write" },
    ],
  },
  triggers: [
    {
      id: "daily-ci-check",
      label: "每日 09:00 采集过去 24h CI 指标并刷新 KR 总账",
      on: { kind: "cron", expr: "0 9 * * *", timezone: "Asia/Shanghai" },
      action: { kind: "run_cli", command: "${inputs.checkCmd}", writeDataPath: "krs" },
    },
    {
      id: "kr-breach-triage",
      label: "成功率 < 98.5% 或 Flaky > 1.0% 时自动归因并评估回滚",
      on: { kind: "data_change", path: "krs" },
      when: ({ data }) =>
        (data.krs.find((k) => k.code === "KR2")?.numericValue ?? 100) < 98.5 ||
        (data.krs.find((k) => k.code === "KR3")?.numericValue ?? 0) > 1.0,
      action: {
        kind: "dispatch_worker",
        role: "triage",
        taskKey: ({ event }) => `triage-${event.date}`,
        goal: "定位过去 24h Top3 失败/超时用例，判断最近 Loop 是否需要回滚，必要时通过 perga_data_patch 增补护栏 KR。",
      },
    },
    {
      id: "replan-roadmap",
      label: "当前优化 Loop 结算后若 KR 仍有缺口则重排 Roadmap",
      on: { kind: "task_settled", role: "optimize" },
      when: ({ data }) => data.krs.some((k) => k.status !== "met"),
      action: {
        kind: "wake_lead",
        prompt: "当前优化 Loop 已结算但仍有未达标 KR：请对照最新指标缺口重排下一轮 Roadmap 优先级。",
      },
    },
  ],
});
```

---

## 五、结合现有代码的分阶段工程落地方案

严格遵循仓库 [AGENTS.md](../../AGENTS.md) 边界（单向无环依赖、已发布 SQLite 迁移字节冻结、契约同步）：

| 阶段 | 涉及层 | 具体改造内容 | 依赖与风险控制 |
|---|---|---|---|
| **Phase 1：Typed Data Store + Schema Fail-Closed 校验器** | `Perga.Storage.Sqlite`、`Perga.PolicyVm`、`Perga.Kernel`、`Perga.Host` | 新增前向迁移记录结构化 Track Data 与 `_meta.{updatedAt, revision, source}`；实现 `t.*` Schema 校验器（含 `ref("task")` / `ref("file")` 封闭外键校验）；新增 `perga_data_patch` MCP 工具、`GET /api/v1/tracks/{id}/data` 与 WebSocket Data 增量推送。 | 不破坏现有 `TemplateState`；旧模板零改动兼容。 |
| **Phase 2：统一 `widget` 块 + 无框沉浸式 HTML Bridge（方案 B）** | `Perga.ReportDoc`、`fe/core`、`fe/web/src/features/report/` | 将现有 `checkpoints` 收敛为 `builtin:stepper` / `builtin:list` + `builtin:table`（极小 React 文字骨架闭集）；在现有 [html/public.tsx](../../fe/web/src/features/report/html/public.tsx) 上升级 **4 消息 Bridge（`data` / `theme` / `resize` / `intent`）+ 透明底 + Seamless Chrome**；接入 `resolveOverlay` Live 投影。 | 保持 `sandbox="allow-scripts"` 零权限边界；`perga:intent` 严格校验 Track 归属；移动端文字骨架 100% 原生可用。 |
| **Phase 3：统一 Trigger 引擎与 CLI 先行执行** | `Perga.PolicyVm`、`Perga.Kernel` | 在 `CompiledTemplateManifest` 扩展 `Triggers` 解析；在 `TrackActor` 与 `ScheduleService` 承接 `cron` / `data_change` / `task_settled` / `schedule_at`；实现显式环境变量白名单的 `run_cli` 执行器与 `maxDepth` 防死循环。 | 严格覆盖 Host 重启恢复与幂等集成测试，防重复派发 Worker。 |
| **Phase 4：内置模板升级** | `templates/` | 用上述三套 v2 模板范式升级 `dev` / `investment-research` / `long-term-goal`（随模板打包 `widgets/range-axis.html` 等无框图形件），跑通 `tsc` 与 Host 端到端集成测试。 | 保留旧模板 ID 兼容或提供显式迁移路径。 |
