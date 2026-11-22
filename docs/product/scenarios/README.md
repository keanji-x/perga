# 典型场景总览

基于 [产品设计与痛点优先级](../design.md) 中按优先级落地的最小解法，仓库内提供了三份内置运行协议（位于 [templates/](../../../templates)）。

它们刻意呈现一条**“按需递增的克制梯度”**——从 **28 行、零业务 Schema** 的纯流式交付，到 **41 行、带一张原生总账表**的 OKR 巡检，再到 **41 行、仅引入一个 124px 轻图形组件**的长期投研——用真实代码证明“只加最小必要制度”即可承接不同长周期任务。

现阶段，我们把**软件开发**作为首个重点打磨的领域，从 6 类真实工程场景自底向上拆解 User Flow、共性、差异与设计命题，详见 **[软件开发场景拆解与产品思考](software-development.md)**。

## 三级克制梯度对照

| 具体场景（内置协议） | 协议体量与新增构件 | 该场景最痛的具体问题 | 当前真实局限 |
| :--- | :--- | :--- | :--- |
| 1. 仓库持续交付（[`agents-delivery`](../../../templates/agents-delivery) · [详细设计](software-development.md)） | **28 行主文件 · 零业务 Schema**（仅 `builtin:flow` + 强制写门禁 + 审查模型强路由） | 多分支重编踩踏、单测绿但架构烂、口头 `AGENTS.md` 总走样 | 单人多轨并行交付已顺滑；多轨强耦合改同一组文件时仍需人工协调合并 |
| 2. 跨周 OKR 优化（[`okr-loop`](../../../templates/okr-loop)） | **41 行主文件 · +1 张原生账本表 + 1 条 `cron`**（`builtin:table` KR 总账 + `builtin:flow` + 归因模型强路由） | 优化几天就忘掉指标基准、缺乏每日真实采集与独立异常归因 | 已沉淀四列 KR 总账与每日巡检；跨周历史实验的自动归因复用仍待加强 |
| 3. 个股长期投研（[`equity-research`](../../../templates/equity-research)） | **41 行主文件 · +1 个 124px 无框图形 + 事件时间线 + 常青正文 + 1 条 `cron`**（`range-axis.html` + `builtin:list` + `builtin:flow`） | 研报聊完即散、反馈滞后数月、极易陷入确认偏误 | 验证了非代码场景的通用性；当前依赖外部行情与研究数据源配置，适合作为高深度个人样本 |

---

## 场景一（首个重点打磨）：软件开发（`agents-delivery`）

- **真实形态**：[template.perga.js](../../../templates/agents-delivery/template.perga.js) 仅 28 行，[report.md](../../../templates/agents-delivery/report.md) 仅 5 行（单一 `builtin:flow` 视图），**完全不维护自定义业务数据**——所有交付进度直接由内核 Flow 真实投影。
- **完整场景拆解与产品思考**：详见 **[软件开发场景拆解与产品思考](software-development.md)**（从修 Bug、功能开发、技术设计、模块维护、长期运维、跨周架构重构 6 类真实场景出发，归纳真共性、真差异与反推出的核心产品命题）。

## 场景二：跨周工程指标持续优化（`okr-loop`）

- **真实形态**：[template.perga.js](../../../templates/okr-loop/template.perga.js) 41 行，[report.md](../../../templates/okr-loop/report.md) 15 行——在 `builtin:flow` 之外，仅增加稳定的 `Objective` 与一张四列 `KR` 总账（`builtin:table`），检查安排全部交回内核 `flow`。
- **核心约束与闭环**：内置 `daily-ci-check` 每日 `cron` 唤醒 Lead，检查采集脚本可用性、刷新过去 24 小时真实指标并用 `data_patch` 更新 KR 总账（由内核自动打标更新时间）与核对异常；协议将 **`role: triage` 强制路由至指定只读归因模型（`enforce: true`）**，将 `role: optimize` 路由至读写模型推进具体优化。
- **当前边界**：把原本容易虎头蛇尾的长期指标攻坚（压低 CI 耗时、消 Flaky、降内存）锚定在每日更新的真实总账上；后续仍需补强跨周历史实验经验的自动沉淀与检索复用。

## 场景三：个股与主题长期深度投研（`equity-research`）

- **真实形态**：[template.perga.js](../../../templates/equity-research/template.perga.js) 41 行，[report.md](../../../templates/equity-research/report.md) 24 行——只有一维估值区间轴因为原生表格表达不够直观，才引入了一个 124px 无框 HTML Widget（`./widgets/range-axis.html`）；下方的事件时间线（`builtin:list`）、常青深度正文与复核安排（`builtin:flow`）全部保持原生渲染。
- **核心约束与闭环**：内置 `weekly-price-refresh` 每周 `cron` 唤醒 Lead，刷新现价与同行估值快照并用 `data_patch` 更新 `valuation`，检查价格异动与临近催化事件，必要时在 `flow.json` 中动态安排单次 `at` 核查节点；协议在事件时间线中对比事前 `Expect` 与事后核查（`beat / inline / miss`），并将 **`role: event_review` 强制路由至异构只读模型（`enforce: true`）**对抗确认偏误。
- **当前边界**：验证了“活报告 + 结构化账本 + 周期/单次唤醒 + 异构复核”在非代码严肃研究中的泛化力；目前依赖外部行情与研究数据源配置，现阶段更适合作为高深度个人研究样本。

## 延展方式：从零模板起步，按需停在合适的梯度

这三个内置包不是封闭清单，也不要求任何新任务一上来就套模板。面对每周安全依赖审计、开源 Issue 分诊、持续竞品追踪等新需求，始终遵循 **[design.md](../design.md) 中 P2-5 的克制原则**：先用一句自然语言开启普通 Track 跑起来；真的出现重复红线时，再按需停在最轻的梯度上（能只用 `flow` + 路由解决的，绝不硬造 Schema；能用原生 `builtin:*` 视图表达的，绝不写自定义 HTML）。

---

- 相关文档：[愿景与根本矛盾](../vision.md) · [产品设计与痛点优先级](../design.md) · [软件开发闭环产品设计](software-development.md)
