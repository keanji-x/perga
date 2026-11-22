# Perga 架构参考

> 实现基线：`216e2f3c`（含 PR #135 认证、#144 契约生成、#145 句柄回收、#143 浏览器入口清理与 #142 模板收敛）。本文描述该版本已实现的架构；接口、SQL 与运行参数的精确定义以所链接源码为准。

Perga 以 Track 为长期工作单元，将会话、任务编排、工作区、报告和外部工具放在同一生命周期内。Host 接入浏览器与 Agent，Kernel 决定领域行为，SQLite 保存可恢复状态，适配器执行外部协议。Report 是出版物，Flow 是调度规格，Session 是可复用的会话工位。

## 1. 仓库拓扑与依赖

### 1.1 Solution 与工具

[Perga.sln](Perga.sln) 包含 **18 个 C# 项目**：6 个库、4 个应用层项目、8 个测试项目。

| 位置 | 项目 | 职责 |
| --- | --- | --- |
| [libs/Perga.Processes](libs/Perga.Processes) | Perga.Processes | 子进程环境整理与 Host 配置隔离 |
| [libs/Perga.ReportDoc](libs/Perga.ReportDoc) | Perga.ReportDoc | Markdown 块文档、修订与压缩快照 |
| [libs/Perga.PolicyVm](libs/Perga.PolicyVm) | Perga.PolicyVm | Jint 模板编译、输入与数据校验、策略及视图 |
| [libs/Perga.AgentKit](libs/Perga.AgentKit) | Perga.AgentKit | OAR Agent 运行时、会话与子进程传输 |
| [libs/Perga.VcsKit](libs/Perga.VcsKit) | Perga.VcsKit | VCS、Forge、工作区驱动与文件访问 |
| [libs/Perga.TermClient](libs/Perga.TermClient) | Perga.TermClient | 终端桥的启动、RPC 与客户端契约 |
| [src/Perga.Contracts](src/Perga.Contracts) | Perga.Contracts | 跨层 DTO、枚举和公开数据契约 |
| [src/Perga.Kernel](src/Perga.Kernel) | Perga.Kernel | Track、Session、Flow、任务、模板与恢复编排 |
| [src/Perga.Storage.Sqlite](src/Perga.Storage.Sqlite) | Perga.Storage.Sqlite | Kernel 存储端口的 SQLite 实现 |
| [src/Perga.Host](src/Perga.Host) | Perga.Host | ASP.NET Core 组合根，HTTP、WS、MCP 与 SSH 适配 |
| [tests/Perga.ReportDoc.Tests](tests/Perga.ReportDoc.Tests) | Perga.ReportDoc.Tests | 文档引擎测试 |
| [tests/Perga.PolicyVm.Tests](tests/Perga.PolicyVm.Tests) | Perga.PolicyVm.Tests | 模板与策略测试 |
| [tests/Perga.AgentKit.Tests](tests/Perga.AgentKit.Tests) | Perga.AgentKit.Tests | Agent 运行时协议测试 |
| [tests/Perga.VcsKit.Tests](tests/Perga.VcsKit.Tests) | Perga.VcsKit.Tests | VCS、Forge 与工作区测试 |
| [tests/Perga.TermClient.Tests](tests/Perga.TermClient.Tests) | Perga.TermClient.Tests | 终端客户端测试 |
| [tests/Perga.Storage.Tests](tests/Perga.Storage.Tests) | Perga.Storage.Tests | 真实 SQLite、迁移、事务、恢复与维护测试 |
| [tests/Perga.Kernel.Tests](tests/Perga.Kernel.Tests) | Perga.Kernel.Tests | 受控运行时下的领域与并发测试 |
| [tests/Perga.Host.IntegrationTests](tests/Perga.Host.IntegrationTests) | Perga.Host.IntegrationTests | HTTP、WS、MCP 与 Host 集成测试 |

非 Solution 工具与内容：

- [tools/perga-term-bridge](tools/perga-term-bridge)：独立 Rust PTY 终端服务，有自己的 Cargo 清单与锁文件。
- [tools/perga-oar-bridge](tools/perga-oar-bridge)：TypeScript/Node OAR 桥；依赖 `@botiverse/oar`，Node 要求 `>=24.0.0`，构建为可供 C# 启动的 ESM bundle。
- [tools/perga-overlayfs](tools/perga-overlayfs)：OverlayFS 工作区辅助程序。
- [tools/perga-deploy](tools/perga-deploy)：安装、发布、systemd 与升级控制工具。
- [templates](templates)：内置包 `agents-delivery`、`equity-research`、`okr-loop`；每包有 `template.perga.js` 与报告骨架，部分包含 schema、views 和 widget。共享类型为 [perga-template.d.ts](templates/perga-template.d.ts)，检查配置为 [tsconfig.json](templates/tsconfig.json)。
- [fe](fe)：React/TypeScript 浏览器客户端及其构建、测试、架构门禁。

### 1.2 编译与直接依赖矩阵

[Directory.Build.props](Directory.Build.props) 的统一属性是：`TargetFramework=net11.0`、`Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors=true`、`LangVersion=15`。下表列出全部生产项目的直接 `ProjectReference` 和 `PackageReference`；传递依赖不重复列入。

| 项目（省略 Perga. 前缀） | 直接项目依赖 | NuGet 包 |
| --- | --- | --- |
| Processes | 无 | 无 |
| Contracts | 无 | 无 |
| ReportDoc | 无 | ZstdSharp.Port 0.8.4 |
| PolicyVm | Contracts | Jint 4.2.2 |
| AgentKit | Processes | 无 |
| VcsKit | Processes | 无 |
| TermClient | Processes | 无 |
| Kernel | Processes、Contracts、ReportDoc、PolicyVm、AgentKit、VcsKit、TermClient | 无 |
| Storage.Sqlite | Kernel、Contracts | Microsoft.Data.Sqlite 11.0.0、Dapper 2.1.66、ZstdSharp.Port 0.8.4 |
| Host | Processes、Kernel、Storage.Sqlite、Contracts、ReportDoc、PolicyVm、AgentKit、VcsKit、TermClient | Cronos 0.9.0、Microsoft.Extensions.Identity.Core 11.0.0、System.CommandLine 3.0.0 |

8 个测试项目均直接使用 `Microsoft.NET.Test.Sdk 17.12.0`、`xunit 2.9.3`、`xunit.runner.visualstudio 2.8.2`。Host 集成测试另用 `Microsoft.AspNetCore.Mvc.Testing 11.0.0`。

| 测试项目 | 直接项目依赖（省略 Perga. 前缀） |
| --- | --- |
| ReportDoc.Tests | ReportDoc |
| PolicyVm.Tests | PolicyVm |
| AgentKit.Tests | AgentKit |
| VcsKit.Tests | VcsKit |
| TermClient.Tests | TermClient |
| Storage.Tests | Storage.Sqlite、Kernel、Contracts、ReportDoc |
| Kernel.Tests | Kernel、Contracts |
| Host.IntegrationTests | Host |

该图无环：Kernel 定义存储端口，不引用 SQLite；SQLite 引用 Kernel 实现端口；Host 装配两者。Processes 与 Contracts 是依赖叶子。不要把某个适配器的协议或模板的业务规则放进通用契约层。

## 2. 领域与运行架构

### 2.1 Track 与单邮箱 Actor

[PergaKernelService](src/Perga.Kernel/PergaKernelService.cs) 是领域入口，[TrackActorRegistry](src/Perga.Kernel/Actors/TrackActorRegistry.cs) 管理按 Track 加载、复用与回收的 Actor。数据库是持久事实源，Actor 持有已水合状态与运行中的资源。

[TrackActor.cs](src/Perga.Kernel/Actors/TrackActor.cs) 主文件为 **519 行**，聚焦组合、邮箱与生命周期。相关 partial 文件承接会话、Flow、模板等跨职责协调；四个私有嵌套协作者真正封装各自的可变状态：

| 协作者 | 拥有的状态与职责 |
| --- | --- |
| [TerminalLifecycle](src/Perga.Kernel/Actors/TerminalLifecycle.cs) | 终端集合、水合、幂等打开、观察、输入、尺寸、关闭及删除清理 |
| [TrackInteractions](src/Perga.Kernel/Actors/TrackInteractions.cs) | Ask、等待者、回答与取消，以及跨 Track 邮件交付和唤醒 |
| [TrackReport](src/Perga.Kernel/Actors/TrackReport.cs) | 当前报告、预览、授权复核与提交 |
| [TrackTasks](src/Perga.Kernel/Actors/TrackTasks.cs) | 任务集合、活跃执行、准入、调度、取消、验证与结算 |

每个 Track 只有一个 mailbox reader；这是异步串行化，不是固定 OS 线程。领域状态转换在获准的邮箱操作内完成。会阻塞生命周期的 provider 停止、gate、终端 I/O 和后台命令等已有外部工作阶段在邮箱外执行，完成后重新进入邮箱并复核身份、版本或生命周期。不能把“邮箱外 I/O”理解为数据库访问也全部移出邮箱。

Actor 继续拥有 Track/Session 索引、模板及工作区协调、活动计数、后台工作和释放顺序。协作者通过显式方法与快照交互，不向其他领域交出可写集合。拆解边界见 [TrackActor 设计记录](docs/design/backend-trackactor-decomposition.md)；其中历史验证段落须结合最终 retirement/rebase 段落阅读。

### 2.2 统一会话生命周期

[UnifiedSessionModels](src/Perga.Kernel/UnifiedSessionModels.cs) 与 [SessionInterop](src/Perga.Kernel/Actors/TrackActor.SessionInterop.cs) 把 Lead、普通子会话与正式 Task Worker 放在同一会话模型中。普通协作者共享 Track 工作区；正式任务还受任务访问模式、工作区隔离或写租约、gate、完成边界及权限约束。普通子会话不自动产生一个任务。

- Session 保存父子关系、provider/model、permission mode、reasoning effort、配置版本、通知父会话策略与 Epoch。
- [SessionTurnScheduler](src/Perga.Kernel/Actors/SessionTurnScheduler.cs) 与 [SessionTurnRunner](src/Perga.Kernel/Actors/SessionTurnRunner.cs) 协调输入、执行、流式输出及结算。
- 持久输入回执与消息队列负责请求键幂等、排队、steering、撤回和派发状态。重试回到原身份，不能用重复派发掩盖不确定结果。
- Unknown、重连、reset/compact、审批等待与响应是显式生命周期路径。结果不明与已失败是不同状态；恢复逻辑不能假定外部执行没有发生。
- Task 通过 `session_id` 指向会话；终结任务的关联用于追溯。当前活跃任务租约决定会话限制，历史任务不会永久锁住可复用会话。

[SessionRuntime](src/Perga.Kernel/Actors/SessionRuntime.cs) 独立管理 provider 句柄及最后使用时间；[Registry](src/Perga.Kernel/Actors/TrackActorRegistry.cs) 默认每分钟巡检，先回收空闲句柄再考虑 Actor 驱逐。[KernelOptions](src/Perga.Kernel/KernelOptions.cs) 的默认 Session 空闲阈值为 15 分钟，Actor 空闲阈值为 10 分钟；Host 的 `SessionIdleTimeout` 配置传入 Kernel。普通空闲回收要求 provider 支持恢复、已有 native thread ID、调度器无占用且无 running/pending runtime task。关闭只释放外部句柄，保留持久 Session 及其恢复身份。

[SessionInterop](src/Perga.Kernel/Actors/TrackActor.SessionInterop.cs) 还清理归档 Session、已关闭 Track 或 MCP token 已撤销的退役句柄；活跃 turn 先标记结算后关闭。候选在 mailbox 中选择，外部关闭在 mailbox 外进行，并在 runtime gate 内复核 generation、调度状态与空闲时间。关闭未确认时保留句柄，并将会话标为 `unknown`、原因为 `shutdown_unconfirmed`，不把不确定的停止当成可安全重新派发；启用 Session 空闲回收时，仍有开放句柄的 Actor 不会提前被空闲驱逐。

接口与运行模型见 [统一会话设计](docs/design/unified-conversation-architecture.md)、[会话配置状态](docs/design/unified-session-config-state.md) 与 [任务/会话归属](docs/design/unify-task-session-ownership.md)。这些设计记录提供背景，当前工具和路由以源码为准。

### 2.3 Track Flow v1

实现位于 [Flow](src/Perga.Kernel/Flow) 与 [TrackActor.Flow](src/Perga.Kernel/Actors/TrackActor.Flow.cs)。Flow 将调度与报告正文分离：

1. `flow.json` 是 Host 管理的可编辑调度文件；提交时经 `flow_commit` 或 HTTP 进入同一个校验/调谐路径。SQLite 中的 `tracks.flow_spec_json`、`flow_rev` 与文件 hash 保存已提交规格及投影依据。
2. 节点用 `when`（依赖、cron、指定时间）与 `do`（`run`、`wake`、`watch`）组合。Run 可调用 Agent 或确定性命令；Wake 向会话投递轻量工作；Watch 用 PR checks 或命令探针等待外部条件。
3. 全图校验覆盖依赖、活跃节点删除、会话复用与路由等不变量。取消活跃节点必须显式声明；提交支持 dry run 与版本检查。
4. Task、时钟与 wake 保存运行状态；`ok`、`attention`、`blocked` 表达业务结果，`none` 表示尚无结论。终结节点的归档保留历史身份。
5. 文件投影使用 hash 检查，避免后台归档或重启恢复覆盖尚未提交的草稿。执行、结算、重启与时钟触发通过持久状态和幂等标识衔接。
6. `builtin:flow` 是内置视图；模板可把 Flow 与业务 Data 投影成专属视图。旧的独立 worker dispatch、旧 schedule CRUD、task accept/reject 与人类逐块编辑报告入口已退役。

[统一调度图设计](docs/design/unified-track-flow-architecture.md) 包含演进背景；不要把其中的历史诊断或旧工具示例当作仍然开放的 API。

### 2.4 Report、模板与业务 Data

[ReportDocEngine](libs/Perga.ReportDoc/ReportDocEngine.cs) 负责 Markdown 块身份、提交与修订快照；[PolicyVm](libs/Perga.PolicyVm) 使用 Jint 执行模板，并提供输入校验、业务 Data 校验、策略和视图能力。模板包按 [模板架构](docs/design/template-architecture.md) 组织，源类型定义位于模板目录。

模板的业务状态统一保存在 Track Data；`data.schema(t)` 声明约束，`data.initial(inputs)` 初始化，`setup(inputs)` 可生成有效配置。`data_patch` 与 HTTP PATCH 使用相同的合并后校验，`expectedDataRev` 保护并发更新。任务、会话、门禁与 Flow 状态仍由 Kernel 持有，模板通过视图消费这些事实。策略钩子取得当前 inputs/data 的独立快照，钩子内修改不会写回；报告提交与任务派发复核审批所依据的数据快照。模板源码或包及继承所需冻结源码保存在模板快照中，见 [TemplateBundle](libs/Perga.PolicyVm/TemplateBundle.cs)。

新建 Track 从 `report.template` 指定的 Markdown 初始化正文并替换输入占位符；模板视图更新不覆盖已持久化正文。[ReportAuthoring](src/Perga.Kernel/ReportAuthoring.cs) 将当前创作面限定为 Markdown prose 与 `perga-block widget`。`report_commit` 受块版本、ReportAuthoring 创作边界与模板策略检查；任务在 Flow 中声明。既有退役块可原样保留，修改或新增这些块会被拒绝；历史块枚举与压缩修订仍由文档引擎保留可读性，不能据此认为旧块仍可创作。

`GET /api/v1/tracks/{trackId}/views` 提供模板视图、HTML 资产、诊断与 Kernel Flow 投影。前端 [ReportDocument](fe/web/src/features/report/document/public.tsx) 只呈现 prose/widget，其他块显示 unsupported 提示；[ReportWidgetBlock](fe/web/src/features/report/widget/public.tsx) 负责 `builtin:flow`、`builtin:list`、`builtin:table` 及包内 HTML widget。列表和指标表分别校验投影；HTML 通过 sandbox iframe 接收数据与主题。没有显式 Flow widget 时，存在 Flow 节点或归档记录的报告可自动补充 Flow 投影。旧 native/table/series/checkpoint/html 等独立 Report 渲染通路已删除；工作区 HTML 文件预览由文件查看器负责，模板特定业务规则留在 Data、视图与策略中。

### 2.5 Provider 与 OAR 运行活动

[AgentRuntime](libs/Perga.AgentKit/AgentRuntime.cs) 经 [OarAgentSession](libs/Perga.AgentKit/OarAgentSession.cs) 和 stdio JSON-RPC 驱动 Node OAR 桥。provider/model/effort 结合目标运行时目录发现；permission mode 按 provider 使用静态声明或动态目录（Codex/Claude 为静态目录，`acp:*` 为动态目录），并在配置及启动路径校验。不能按外部工具名称的私有前缀推断能力。

桥接除 turn 文本与工具事件，还转发 OAR runtime status/task activity；[TrackActor.RuntimeActivity](src/Perga.Kernel/Actors/TrackActor.RuntimeActivity.cs) 以 Session Epoch 和 provider 实例身份拒绝过期观察，将当前投影通过 WS 和会话 items 响应提供给前端。运行活动不等同于新的持久任务或一轮 turn；turn 结束后仍可能有活动。见 [OAR 桥设计](docs/design/oar-agent-bridge.md)。

[ChildProcessEnvironment](libs/Perga.Processes/ChildProcessEnvironment.cs) 移除 Host 保留环境名/前缀，保留正常工具链环境；SSH 边界另有显式环境传递策略。不要将本地环境过滤误写为所有子进程均采用同一份严格白名单。

### 2.6 Execution Targets、工作区与 SSH

[IExecutionEnvironment](src/Perga.Kernel/Ports/IExecutionEnvironment.cs) 汇集某一目标的 Agent runtime、workspace drivers、文件系统、shell 和 terminal，Registry 根据 Track 的 `executionTargetId` 选择环境。Area 保存默认目标，Track 保存自身目标；目录查询、provider 发现及创建流程都携带目标身份。

本地环境与 [Host SSH 适配](src/Perga.Host/Execution) 支持本地、多用户及远程 SSH 执行。SSH 的命令、文件、终端和 provider 使用目标适配器；Host 的数据库与编排权威不迁往远端。`SshMcpTunnel` 在远端 loopback 分配反向转发端口，将 Agent 的 MCP 调用送回 Host；自动隧道要求 HTTP Host MCP 地址，HTTPS 需配置可达的 `McpBaseUriOverride` 以保留 TLS 主机名验证。

[VcsKit](libs/Perga.VcsKit) 提供 direct、VCS 与 overlay 工作区驱动，以及 Git/Jujutsu 和 GitHub Forge 操作。工作区准备、失败重试、隔离/租约与销毁由 Kernel 生命周期协调，外部协议由适配器实现。只读任务的隔离不能依靠提示词替代。

[终端桥](tools/perga-term-bridge) 管理 PTY、屏幕状态、RPC 和终端 WS；[TermClient](libs/Perga.TermClient) 是 C# 侧契约与进程/连接管理。浏览器经 Host relay 接入，不直接承担桥进程的生命周期。

## 3. SQLite 持久化与恢复

### 3.1 门面、共享寿命与领域仓储

[SqliteTrackStore](src/Perga.Storage.Sqlite/SqliteTrackStore.cs) 是 **528 行**的公开端口门面；保留 `ITrackStore` 等接口和 `SessionInputs`、`SessionMessages`、`TrackSchedules`、`TrackWakes` capability 的原对象身份。

[SqliteStoreContext](src/Perga.Storage.Sqlite/SqliteStoreContext.cs) 独占共享初始化、migrator、单 writer、冷 Blob、upcaster、时钟、读连接工厂、数据库 identity sidecar 和 disposal。先迁移再启动 writer；释放时先结束 writer，再清理对应 reader pool。

**25 个 internal sealed repository** 按领域封装 SQL 与映射，依赖通过构造器显式注入，context 不持有仓储反向引用：

| 领域 | 仓储类型 |
| --- | --- |
| Area、创建回执、准备与认证 | AreaRepository、CreationRepository、AuthRepository |
| Track 创建、读取、更新、删除 | TrackCreationRepository、TrackReadRepository、TrackUpdateRepository、TrackDeletionRepository |
| 报告、任务 | ReportRepository、TaskRepository |
| Session、输入、消息、恢复 | SessionRepository、SessionInputsRepository、SessionMessagesRepository、SessionResumeRepository |
| Ask/邮件、终端、事件、overlay | CorrespondenceRepository、TerminalRepository、EventRepository、OverlayRepository |
| 时钟、wake、维护、启动恢复 | TrackSchedulesRepository、TrackWakesRepository、MaintenanceRepository、BootRecoveryRepository |
| Today、目标、已读、升级 | TodayRepository、ExecutionTargetsRepository、ReadReceiptsRepository、SystemUpgradeRepository |

[SessionMessageSql](src/Perga.Storage.Sqlite/SessionMessageSql.cs) 只提供消息插入、状态 CAS 与 ordinal 等事务内原语。跨仓储助手使用调用者的 connection/transaction，不嵌套 enqueue 或开启另一事务；仓储与静态助手依赖图保持无环。参见 [存储拆解记录](docs/design/backend-storage-decomposition.md)。

### 3.2 写入、快照与失败边界

[SqliteSingleWriter](src/Perga.Storage.Sqlite/SqliteSingleWriter.cs) 使用单写连接和有界通道。默认队列容量 4,096，微批最多 32 项；WAL、`synchronous=NORMAL`、外键开启。查询使用独立只读/query-only 连接，aggregate 读取保留自己的快照事务。

一个业务回调中的相关行与事件在同一事务提交。例如 Track 创建包括初始聚合、输入、准备意图、回执和事件；报告提交原子保存修订、当前正文和事件，`authorSessionId` 为空的写入还在同事务入队报告 wake；Flow 更新将规格、任务和时钟效果保持一致。

微批中任一项失败会回滚整批，再逐项在独立事务重放，使有效的其他调用能够成功。回调因此必须保持事务内可重放，不能夹带不可撤销的外部副作用。成功结果在 commit 后发布；取消与失败不能把半批结果当成成功。

输入准入、消息 steering、恢复和 Epoch 变更使用持久状态与 CAS。Boot recovery 与 Actor 水合配合处理进程重启后的中断工作。`synchronous=NORMAL` 下的进程恢复保证不应扩写成断电后零丢失承诺。

### 3.3 26 个冻结迁移与真实 schema

唯一 DDL 事实源为 [Migrations](src/Perga.Storage.Sqlite/Migrations)：**0001–0026，共 26 个 SQL 文件**，发布后字节冻结，修正必须新增前向迁移。本文不复制一份容易漂移的建表脚本。

| Schema 分组 | 当前承载 |
| --- | --- |
| 基础与工作区 | `schema_migrations`、`areas`、`tracks`、`track_workspace_preparations`、`creation_receipts` |
| 报告与业务数据 | `reports`、`report_revisions`；业务 Data/版本存于 Track 相关列 |
| Flow 与运行 | `tracks.flow_spec_json/flow_rev/flow_file_sha256`、`tasks`、`track_schedules`、`track_wakes` |
| 会话与输入消息 | `agent_sessions`、`session_turns`、`session_inputs`、`session_items`、`session_messages`、`session_message_steers` |
| 交互与终端 | `asks`、`mails`、`terminals` |
| 事件与产品投影 | `events`、`daily_tracks`、`read_receipt_meta`、`read_receipts` |
| 执行与运维 | `execution_targets`、`system_upgrade_receipts` |
| Web 认证 | `auth_users`、`auth_sessions` |

迁移顺序也描述了结构演进：0002 工作区准备；0003 Worker 消息；0004 provider resume；0005 Blob 引用索引；0006 消息队列；0007–0009 权限与推理配置；0010 Ask 交付；0011 升级回执；0012 Today；0013–0015 上下文及 wake；0016 退役 hold Ask；0017 task role；0018 时钟；0019 unknown 状态；0020 已读回执；0021 统一会话；0022 Data；0023 Task/Session 归属统一；0024 执行目标；0025 Flow；0026 Web 认证。

历史迁移出现过的 `task_attempts`、`worker_messages`、`worker_settings`、`provider_session_resume` 已在后续迁移删除。**当前没有独立 `track_flows` 表**，Flow 规格在 `tracks` 列中；`track_schedules` 的历史列仍存在，运行语义已收口为 Flow 时钟，不能仅凭建表历史推断仍有旧 CRUD。

[SqliteMigrator](src/Perga.Storage.Sqlite/SqliteMigrator.cs) 从内嵌 SQL 资源加载连续版本，验证已应用迁移的 SHA-256，升级前通过 SQLite backup 生成版本备份，再执行迁移。校验实现规范化 BOM/换行，不等于允许修改已发布脚本。读取期 JSON upcaster 由 [SqliteJsonUpcaster](src/Perga.Storage.Sqlite/SqliteJsonUpcaster.cs) 集中处理，不能假定所有任意 JSON 都有同一版本封装。

### 3.4 冷数据与维护

[ColdBlobStore](src/Perga.Storage.Sqlite/ColdBlobStore.cs) 处理大 payload 的压缩、预览和水合；默认剥离阈值为 65,536 bytes，preview 为 2,048 bytes，参数在 [SqliteStorageOptions](src/Perga.Storage.Sqlite/SqliteStorageOptions.cs)。关系数据中的引用、写入与文件回收顺序是完整性边界。

[SqliteMaintenanceHostedService](src/Perga.Host/Lifecycle/SqliteMaintenanceHostedService.cs) 周期调用维护；[MaintenanceRepository](src/Perga.Storage.Sqlite/MaintenanceRepository.cs) 分批裁剪事件、报告历史与可缩减输出，复查冷 Blob 引用，并执行增量 vacuum/checkpoint。用户输入、报告当前版本和任务/消息身份有各自保留规则，不能把维护概括为清空旧会话。配置及具体预算以实现为准。

## 4. Host 接入与安全边界

### 4.1 组合根与静态发布

[Program.cs](src/Perga.Host/Program.cs) 捕获原始传输信息，按配置处理可信转发头，安装异常转换、传输安全、RequestOriginGuard、WebSocket，再经过路由、认证、授权和写入来源检查，映射 API/MCP，并通过 [ReleaseSnapshotManager](src/Perga.Host/SystemUpgrade/ReleaseSnapshotManager.cs) 在 **根路径 `/`** 交付静态前端。不存在单独的旧前缀静态挂载。非 API/MCP 的 GET/HEAD 可回退到 SPA；未知 API/MCP 路由返回错误，不返回 HTML。

[服务注册](src/Perga.Host/PergaHostServiceCollectionExtensions.cs) 组合存储、Kernel、目标环境、桥与 hosted services。启动恢复、Cronos 时钟调度和 SQLite 维护分别位于 [Lifecycle](src/Perga.Host/Lifecycle)。

### 4.2 HTTP 资源面

路由权威为 [ApiV1Endpoints](src/Perga.Host/Http/ApiV1Endpoints.cs)、[TodayEndpoints](src/Perga.Host/Http/TodayEndpoints.cs)、[SystemEndpoints](src/Perga.Host/Http/SystemEndpoints.cs) 与 [AuthEndpoints](src/Perga.Host/Auth/AuthEndpoints.cs)。下表列出实际资源组，避免固定“若干个逻辑端点”的虚假计数。

| 路径（资源组） | 当前操作 |
| --- | --- |
| `/healthz` | GET 健康检查 |
| `/api/v1/bootstrap`、`/templates`、`/overlays` | GET 引导、模板目录与活动/通知投影（后两项同属 `/api/v1`） |
| `/api/v1/fs/listdir` | GET 目标环境目录 |
| `/api/v1/targets` | GET/POST，DELETE `/{targetId}`，GET `/{targetId}/providers` |
| `/api/v1/areas` | GET/POST，PATCH/DELETE `/{areaId}`；GET `/{areaId}/workspace-options`、`/{areaId}/tracks` |
| `/api/v1/tracks` | POST；GET/PATCH/DELETE `/{trackId}`；POST `/{trackId}/workspace/retry` |
| `/api/v1/tracks/{trackId}/template` | GET/DELETE；PATCH `/inputs` |
| `/api/v1/tracks/{trackId}/files`、`/files/raw`、`/vcs-diff` | GET 文件/原始内容与差异（均相对同一 Track） |
| `/api/v1/tracks/{trackId}/report`、`/views` | GET 报告与投影视图 |
| `/api/v1/tracks/{trackId}/flow` | GET；POST `/flow/commit`（相对 Track） |
| `/api/v1/tracks/{trackId}/data` | PATCH 业务数据 |
| `/api/v1/tracks/{trackId}/tasks/{taskKey}/cancel` | POST 取消 |
| `/api/v1/tracks/{trackId}/sessions` | GET/POST |
| `/api/v1/sessions/{sessionId}` | PATCH 配置；GET `/items`；POST `/messages`、`/control`、`/interactions/{interactionId}/respond`；DELETE `/messages/{messageId}` |
| `/api/v1/tracks/{trackId}/terminals` | GET/POST；DELETE `/{termId}` |
| `/api/v1/read-receipts` | GET；POST `/{kind}/{targetId}/read` 或 `/unread` |
| `/api/v1/system/info`、`/system/drain`、`/system/reload` | 分别 GET、POST、POST（均属 `/api/v1`） |
| `/api/v1/auth/login`、`/logout`、`/session` | POST 登录/退出、GET 当前会话 |
| `/api/v1/system/auth-status` | GET 认证配置状态（管理权限） |
| `/api/v1/today/daily`、`/calendar`、`/report-changes`、`/report-edits` | GET 日 Track、日历、报告变化及编辑明细（均属 `/api/v1/today`） |

`/api/v1` 默认属性为 camelCase，枚举写出 snake_case 字符串，见 [PergaJsonDefaults](src/Perga.Host/Serialization/PergaJsonDefaults.cs)。Today 同样使用 camelCase 属性和查询参数（`throughEventId`、`trackId`），统一位于 `/api/v1/today`，受 `UserSession` 和来源校验保护；旧 `/api/today` 路由已移除。

### 4.3 WebSocket 与事件重放

- **`/api/v1/events`**：事件 WS，支持 `since` 与可选 Track 过滤；先订阅实时广播再读持久日志，去除重放重叠。超出保留窗口或最大 2,000 条重放范围时发送 `_snapshot_required`，正常完成发送 `_replay_complete`；客户端刷新快照后从 tip 续接。当前没有 `/api/v1/ws` 路由。
- **`/api/v1/terminals/{termId}/ws`**：Host 将终端 WS 中继到对应执行环境的终端桥。

[EventsWebSocketHandler](src/Perga.Host/WebSockets/EventsWebSocketHandler.cs) 区分带持久 event ID 的领域事件与无持久 ID 的瞬态流式帧；完整状态可由查询恢复，不能依赖所有 delta 都曾写入 SQLite。[EventHub](src/Perga.Kernel/Events/EventHub.cs) 提供内存广播。

### 4.4 MCP 与授权

[McpStreamableHttpEndpoints](src/Perga.Host/Mcp/McpStreamableHttpEndpoints.cs) 在 `/mcp` 前缀下映射三类 Streamable HTTP 端点，分别支持 POST/GET/DELETE：

| 端点 | 后端 |
| --- | --- |
| `/mcp/kernel` | Kernel 工具：Report/Data、Flow/Task、Session、Ask/Mail、provider、terminal、Track 与升级 |
| `/mcp/vcs` | VCS/Forge 操作 |
| `/mcp/p/{pluginId}` | 配置的外部 HTTP/stdio 插件路由，并检查 Track 访问 |

`/mcp` 是路由前缀，不是单独工具调用端点。[HostMcpSessionBridge](src/Perga.Host/Mcp/HostMcpSessionBridge.cs) 管理会话 token 与 Principal；传输会话也绑定 owner token。工具公开名以 [KernelMcpTools](src/Perga.Kernel/Mcp/KernelMcpTools.cs) 的运行目录为准，例如 `session_create`、`flow_commit`、`task_done`，不把旧前缀命名当成新契约。

工具按当前是否绑定任务、Lead 身份及会话关系限制可见性与操作权限。任务完成与治理不是同一权限；模板策略和批准等待也由对应 Kernel 路径执行。不要将旧设计中“所有工具统一等待人类放行”的设想当作所有调用必经的实现。

### 4.5 浏览器来源与系统管理

[Web 认证](src/Perga.Host/Auth/PergaAuthentication.cs) 默认拒绝未认证访问：`UserSession` 要求有效 owner Web cookie 会话；API/WS 使用该身份，未知 API 路由与合成 405 也受保护。`UserOrManagement` 用于 system/info，接受用户或管理身份；`SystemManagementOnly` 用于 drain/reload/auth-status，要求管理 Bearer token。MCP 使用独立 `McpSessionOnly`，Web cookie 不替代 MCP 会话凭证。

[AuthEndpoints](src/Perga.Host/Auth/AuthEndpoints.cs) 暴露匿名 JSON 登录、受 `UserSession` 保护的当前会话及允许匿名的幂等退出。登录校验数据库 owner 和密码、限流后签发 cookie；退出按可选会话 ID 防止旧请求撤销新会话，并撤销持久会话、断开相关 WS。没有 owner 配置时登录拒绝。部署模式、原始 socket 与可信代理校验由 [TransportSecurityMiddleware](src/Perga.Host/Http/TransportSecurityMiddleware.cs) 与 [配置](src/Perga.Host/Configuration/PergaHostOptions.cs) 决定，不能靠伪造转发头绕过。

[RequestOriginGuard](src/Perga.Host/Http/RequestOriginGuard.cs) 覆盖整个 `/api`（含 `/api/v1/today`）及 `/mcp`，与身份认证互补：

- 本地连接与代理入口执行 Host/DNS rebinding 校验，结合 `AllowedHosts`、`AuthPublicOrigin` 和严格限定的直接本地管理/MCP 例外；不凭转发头推断原始本机连接。
- `Origin` 必须是单一有效 HTTP(S) origin，匹配请求本源、配置公开源或 `AllowedOrigins`；`Sec-Fetch-Site` 同样校验，未显式允许的 cross-site 请求拒绝。API WebSocket 握手必须带 Origin。
- 标有 `BrowserWriteApi` 的 API 写请求要求 JSON 或允许的 Origin/Referer；非浏览器调用省略浏览器头仍须满足传输、身份及写入来源约束。来源检查不能授予身份。

升级的 drain/reload 另受 [SystemManagementAuthorization](src/Perga.Host/Http/SystemManagementAuthorization.cs) 控制。Drain 管理派发准入，reload 切换发布快照；升级回执与通知由 Kernel 和 SQLite 配合恢复，细节见 [升级设计](docs/design/system-upgrade-phase2.md) 与 [安装升级](docs/design/install-and-upgrade.md)。

## 5. 前端架构

### 5.1 Perga 原生客户端与分层

[前端规则](fe/AGENTS.md) 定义运行时依赖方向：`app → features → systems → ui → core`，允许向下跨层，禁止向上引用和 feature 域互相引用；styles 是有 owner 的非运行时叶子层。禁止新建 shared/barrel，具体静态规则与测试例外以该文件和门禁为准。

| 层 | 位置与责任 |
| --- | --- |
| core | [fe/core](fe/core)：跨平台领域模型、契约解析、transport、事件与 query key 语义 |
| styles | [styles](fe/web/src/styles)：token、CSS layers、主题与设计系统约束 |
| ui | [ui](fe/web/src/ui)：通用原语与 Astryx 封装 |
| systems | [systems](fe/web/src/systems)：终端、卡片、文件查看等浏览器系统能力 |
| features | [features](fe/web/src/features)：Track、对话、Report、Area 等业务界面 |
| app | [app](fe/web/src/app)：路由、provider、Query、会话注册表和组装 |

生产入口按 Perga 单一产品运行，已移除 `pergaMode` 双轨。浏览器挂载根路径 `/`；产品持久键使用 `perga:*`、IndexedDB 名为 `perga`、内部链接为 `perga://`。P3 不引入旧命名迁移兼容层，见 [前端 P3 记录](docs/design/frontend-p3-cleanup.md)。

[ProductionApp](fe/web/src/app/auth/production-app.tsx) 统一通过 [SessionGate](fe/web/src/app/auth/session-gate.tsx) 验证 Web 会话，再进入兼容性检查和路由；[LogoutGate](fe/web/src/app/auth/logout-gate.tsx) 处理退出。旧 Tauri bundled 认证、配对/恢复入口、`systems/recovery` 与 `RecoveryGate` 已删除，其全局 DOM 观察与每秒滚动采样也随之移除；浏览器生产入口只保留 Web session 路径。

### 5.2 查询、会话与编辑器

TanStack Router 组织导航，TanStack Query 组织查询、mutation 与失效；[app/providers](fe/web/src/app/providers) 按领域拆分 Query hooks。核心 API 通过 [client](fe/core/api/client.ts) 与 [Perga v1 schema](fe/core/api/perga-v1.ts) 接入 `/api/v1`，Today 使用统一的 `/api/v1/today`。Area/Track 的响应、事件与缓存直接消费 `AreaDto`/`TrackDto`，不再往返翻译 `AreaWire`/`TrackWire`。Provider ID 保留运行时目录报告的原值，展示标签单独处理，不再拼接 `acp:` 前缀。Query key 包含必要的目标/资源身份，避免不同 execution target 的 provider/model 目录串缓存。

[app/conversations](fe/web/src/app/conversations) 管理会话注册表、outbox、历史、活跃流与审批控制；WS 驱动实时投影及查询失效，刷新仍可从持久快照恢复。创建确认的 Track ID 与首次发送恢复是客户端生命周期的一部分。

编辑器使用原生 `@codemirror/view` 和 `@codemirror/state`；代码查看与报告源码编辑共用 CodeMirror 基础能力，已移除 React CodeMirror wrapper。Astryx Design 与本仓库语义 token/UI 原语提供通用视觉层，模板/provider 业务规则留在所属领域。拆解与修复背景见 [P2a](docs/design/frontend-p2a-providers-queries.md)、[P2b](docs/design/frontend-p2b-router-split.md)、[P2c](docs/design/frontend-p2c-dom-render.md)、[P2d](docs/design/frontend-p2d-codemirror-perf.md)。

### 5.3 契约现状与门禁

[PergaHttpEndpoints](src/Perga.Host/Http/PergaHttpEndpoints.cs) 让服务与离线导出共享真实注册路由及 JSON/认证配置。[OpenApiDocumentExporter](src/Perga.Host/Http/OpenApiDocumentExporter.cs) 读取 RouteEndpoint 表，[OpenApiSchemaRegistry](src/Perga.Host/Http/OpenApiSchemaRegistry.cs) 使用序列化元数据生成 schema，[OpenApiAuthorization](src/Perga.Host/Http/OpenApiAuthorization.cs) 从真实授权策略生成安全要求。离线导出不启动监听或后台服务；未知契约/认证元数据使生成失败。

[gen-api.mjs](fe/tools/api/gen-api.mjs) 通过 `npm run gen:api` 运行 Host 导出 OpenAPI 3.1，再生成 TypeScript：[openapi.json](fe/core/api/generated/openapi.json) 在此基线包含 **49 路径、57 操作、114 schema**，[wire.ts](fe/core/api/generated/wire.ts) 从这些 schema 生成类型。`npm run gen:api -- --check` 对比真实导出，拒绝产物漂移；[OpenApiExportTests](tests/Perga.Host.IntegrationTests/OpenApiExportTests.cs) 验证路由覆盖、schema、认证与生成确定性。运行时解码器仍需随契约同步，不能用类型生成代替运行时校验。

前端门禁包含 ESLint、Stylelint、dependency-cruiser、目录/清单、样式/token 与 fixture 检查，入口为 npm lint scripts 和 [tools](fe/tools)。运行时不得导入测试工具；修改门禁须有正反 fixture 并检查同源互比的局限。契约检查还应对照真实 Host，不能用两份共同陈旧的产物相互证明。

## 6. 验证与维护约定

[AGENTS.md](AGENTS.md) 定义 review tier、单 checkout 单 writer、发布迁移冻结与最终树检查。维护本文时先核对源码；演进设计文档保留历史推演，引用时区分历史提案和当前实现。

以下为仓库检查入口，应按改动影响选择；文档修改只需路径、事实和 diff 检查，不据此声称运行测试已通过：

```sh
# 后端跨层、持久化、契约或隔离变更
dotnet test Perga.sln

# 前端；缺依赖或锁文件变更时先运行 npm ci
(cd fe && npm run lint && npm run build)
(cd fe && npm test)
(cd fe && npm run test:browser)

# Rust 终端桥
(cd tools/perga-term-bridge && cargo fmt --check && cargo test --locked)

# OAR 桥；依赖安装后运行，要求 Node >=24
(cd tools/perga-oar-bridge && npm run typecheck && npm test && npm run build)

# HTTP 契约变更后生成，再校验漂移
(cd fe && npm run gen:api && npm run gen:api -- --check)

# 所有改动的空白检查
git diff --check origin/main
```

验证失败要区分业务断言、依赖缺失和运行环境限制；只报告实际执行的结果。持久化测试应包含事务中途失败、同库重开与重试身份，外部执行使用受控 provider；界面改动在真实浏览器覆盖受影响流程。跨层或安全/持久化变更遵循 L2 双独立评审。
