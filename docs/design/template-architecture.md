# Perga 模板架构

Flow 定义工作如何执行，模板定义业务数据、Report 视图和运行策略。内核持有任务、会话、门禁及调度状态。Report 从这些事实和已保存的数据派生，给人提供只读的阅读界面。

| 层 | 输入 | 职责 |
|---|---|---|
| Kernel Flow | Track 的 `flow.json` | 校验、持久化并调度 `when × do`，投影 `builtin:flow` |
| Template Data | `data.schema(t)`、`data.initial(inputs)`、`data_patch` | 保存模板特有的业务数据并校验变更 |
| Template View | `report.md`、`report.views`、包内 HTML | 将业务数据与内核事实排成可阅读的 Report |
| Template Policy | 输入、路由、门禁要求、只读策略钩子 | 指定当前 Track 的任务和会话策略 |

## Flow

Lead 通过 `flow_read` 查看当前定义，通过 `flow_commit` 提交流程。模板可在 `flow` 声明初始节点，绑定模板时由内核使用同一套规则校验和写入。模板自带的定时任务随模板绑定启停，独立节点仍由 Track 管理。

`when` 可表达立即执行、依赖及其结果、周期或单次时间。动作包括 `run`、`wake` 和 `watch`。执行结果、任务租约、计划时间和归档记录由内核维护，Report 通过 `builtin:flow` 显示这些事实。支持插值的 Flow 字段在使用时解析 `${inputs.key}`，因此更新输入可以刷新相关文字与命令。

完整模型见 [统一 Track Flow 架构](unified-track-flow-architecture.md)和 [Flow Schema](../schemas/flow.v1.schema.json)。

## 模板包

简单模板可以使用单文件，多文件包以 `template.perga.js` 为入口。常见配套文件包括 `report.md`、`schema.js`、`views.js` 和 `widgets/*.html`。模块只能在包内通过相对路径导入。文件内容与继承链保存在 Track 的模板快照中。运行时复用解析后的只读包，在每次求值时创建新的 JavaScript 沙箱，应用当前输入并执行相应逻辑。

`inputs` 声明字符串、数值、布尔值或枚举及其默认值。`setup(inputs)` 可用有效输入生成配置，包括数据、视图和路由。新建 Track 时，内核读取 `report.template` 指定的 Markdown，并将正文中的 `${inputs.key}` 替换为有效输入。重新绑定模板或更新输入时，视图可重新投影，已持久化的正文仍由 Report 自己维护。

编译时验证入口、输入与 Flow 定义、Report 对包内文件和视图的引用，以及初始数据和视图函数能否正常执行。模板声明见 [perga-template.d.ts](../../templates/perga-template.d.ts)。

## Data 与 Policy

模板用 `data.schema(t)` 定义字段、必填项、枚举等约束，用 `data.initial` 初始化业务数据。可用 `t.ref("task")` 引用 Track 任务，用 `t.ref("file")` 引用 Track 工作区中的相对路径。写入走 `data_patch` 或 `PATCH /api/v1/tracks/{trackId}/data`。内核校验合并后的数据，可用 `expectedDataRev` 防止并发覆盖。

`report.views` 函数接收当前业务数据和内核事实，后者包括实际任务、会话、门禁及 Flow 状态。策略钩子使用当前 `{ trackId, inputs, data }` 的独立快照，钩子中的修改不会写回。Report 提交和任务派发会校验审批所依据的数据快照。`before` 可允许、拒绝或请求人确认，MCP 动作使用 `tool.<toolName>`。`turnContext` 与 `before("turn.before", …)` 也接收会话快照，`headerView` 投影 Track 头部信息。`tasks.routing` 可提供执行默认值或强制模型、访问模式，`requireGateForReadWrite` 要求写任务携带门禁。

模板快照直接存储源码或包，保留继承所需的冻结源码。

## Report 视图

`GET /api/v1/tracks/{trackId}/views` 返回模板视图、HTML 资产、诊断和内核 Flow 投影。数据或调度状态变化后，前端会重新拉取这些视图。视图更新无需写入 Report 正文。

| `perga-block widget` 的 `use` | 数据来源与呈现 |
|---|---|
| `builtin:flow` | 内核 Flow 投影。使用 `view: "flow"`，模板无需声明 JavaScript 投影 |
| `builtin:list` | 视图返回 `items`，可提供标题、摘要、指标和分组顺序。条目以 `group` 标记所属分组 |
| `builtin:table` | 视图返回 `metrics`，可带目标、当前值、状态、更新信息和证据 |
| `./widgets/*.html` | 包内 HTML 接收对应视图的 JSON，用于估值轴等专用图形 |

列表和指标表各自校验数据。运行中的视图出错时，诊断显示在对应块内。Report 的创作契约是 Markdown 正文与 `perga-block widget`。历史 Report 块及其压缩修订仍可读取，模板快照直接存储源码或包，保留继承所需的冻结源码。

HTML 通过含 `sandbox="allow-scripts"` 的 iframe 承载。宿主发送 `perga:data` 和 `perga:theme`，组件可上报 `perga:resize`，并通过 `perga:intent` 请求打开工作区文件或会话。导航沿用 Track 的文件和会话入口。

## 内置模板

- [agents-delivery](../../templates/agents-delivery/template.perga.js) 声明审查路由和写任务门禁。[Report](../../templates/agents-delivery/report.md) 通过 `builtin:flow` 展示 Lead 根据当前交付目标安排的任务。
- [equity-research](../../templates/equity-research/template.perga.js) 声明每周价格复核。按所选标的初始化 [Report](../../templates/equity-research/report.md)，用 [估值区间轴](../../templates/equity-research/widgets/range-axis.html) 和 `builtin:list` 呈现估值与事件。
- [okr-loop](../../templates/okr-loop/template.perga.js) 声明每日指标采集待办。[Report](../../templates/okr-loop/report.md) 用 `builtin:table` 呈现关键结果，用 `builtin:flow` 展示检查安排。
