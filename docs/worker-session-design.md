# Worker 会话控制与任务派发

设计草稿 · 2026-11-18

人和 Lead 从同一个 Task 找到当前 Worker，查看同一份执行进展，并执行相同的会话操作。派发界面和 MCP 工具只要求填写目标。Kernel 生成任务标识，解析模板默认值，并在调度条件满足后启动 Worker。

## 会话操作

Worker 是绑定到 Task Attempt 的 `AgentSession`。Task 的状态、Attempt 的执行结果、Session 的消息和当前 Turn 分别沿用现有记录。Task 详情返回 `taskId`、当前 `attemptId` 和 `sessionId` 及执行状态。等待调度时可看到等待原因，Worker 启动后可直接打开会话，重试产生的历史 Attempt 仍可从同一个 Task 查看。

| 操作 | 人类界面与 Lead 的共同行为 |
| --- | --- |
| 查看进展 | 读取任务阶段、当前轮次、最近消息、工具调用及结果、待审批请求和失败原因。运行中的输出随事件更新，历史支持分页。 |
| Steer | 指定当前 `turnId` 发送一条纠偏消息。provider 确认接收后返回「已送达当前轮」。provider 缺少实时 Steer 能力时，只有调用者允许排队才登记为下一轮输入。目标轮次已变更时返回冲突并带回当前状态。 |
| 中断 | 停止指定的当前轮。确认停止后，同一 Attempt 进入可继续状态并保留工作区租约。后续输入可继续原会话。取消 Task 仍按任务生命周期结算并清理租约。 |
| 改模型、思考深度、审批模式 | 当前轮沿用启动时的配置，已保存的修改在下一轮生效。读接口同时返回当前轮生效值、下一轮设置和可选项。 |

会话进展复用现有消息记录和 `EventHub`。Kernel 为活跃轮维护有界的进展快照及轮内序号，包含最新输出和工具进度。UI 读取快照并按游标接续 WebSocket 增量。MCP 支持按相同游标读取或限时等待增量，等待在 `TrackActor` 写入队列外进行。两边都按同一轮次和序号合并，断线或游标过期时重新取快照。轮次结算后以持久化的会话记录为准，进程重启按实际可恢复的记录返回状态。

Kernel 在 `TrackActor` 内检查目标和写入顺序。中断在途时显示「正在中断」，确认停止后等待显式继续，已排队输入随之暂停。暂停中的 Attempt 继续占用调度器的租约名额。验证期间保留租约，取消、关闭和替换请求会收到冲突。若中断和交卷同时发生，由 Actor 首先受理的操作推进状态，迟到的提交或中断收到冲突。交卷校验发起轮次，恢复后仍拒绝旧轮的迟到提交。

设置修改携带配置版本，避免人和 Lead 互相覆盖。AgentKit 适配器报告可用模型、每个模型支持的思考深度、审批能力，以及能否延用原生线程。开始下一轮前重新校验设置和当前授权，并记录实际生效的配置。切换设置需要新线程时，沿用现有 Epoch 交接，保留 Perga 会话和历史。应用失败时返回原因并阻止该轮以旧配置开始。

## 授权

人类 UI 使用登录用户身份。Lead MCP 使用当前 Track 的会话身份。Kernel 在每次操作时核对调用者、目标 Track、当前 Lead 和绑定关系。写操作校验 `attemptId`，Steer 和中断再校验 `turnId`。读取历史 Attempt 仍按 Track 权限鉴权。Lead 切换或人类审批等待期间发生的状态变化，在执行前重新检查。所有入口共享一致的拒绝和冲突语义。

模板和 Track 授权共同决定 Lead 可以调用哪些操作、选择哪些审批模式。对需要批准的 provider 请求，默认询问人类，也可将会话设置为直接拒绝。放宽审批权限由具备权限的人确认，自动批准只适用于人类已授权的范围。工作区隔离、Task 的读写权限、模板门禁和要求人类回答的 Ask 继续由各自的规则执行。会话记录保留派发、Steer、中断及设置变更的发起者和结果。

## 只填目标的派发

人或 Lead 提交一条非空目标即可直接排队。UI 将其余字段放在可选设置中。MCP 请求的最小业务参数如下。

```json
{"goal":"修复导出 CSV 时的日期格式，并验证相关测试"}
```

| 字段 | 解析规则 |
| --- | --- |
| `goal` | 必填。去除首尾空白后保存为 Task 目标和 Worker 首轮指令的一部分。 |
| Task 标识和 Report 块位置 | Kernel 生成可引用的 `taskKey`、Task ID 和 Report 块 ID。块放进模板指定的任务区。模板未指定时放在 Report 末尾。Attempt 和 Worker 会话 ID 在实际启动时生成。 |
| `kind`、`ready` | 此入口固定创建 `agent` 任务，提交即进入待调度状态。 |
| `access` | 优先采用获准的显式覆盖，其次用模板默认值。无模板默认时取 `read_only`。建议 `dev` 模板声明 `read_write`，只读排查可显式覆盖。 |
| `acceptance`、`dependsOn` | 可选。模板可提供验收标准，依赖缺省为空。 |
| `gate` | 优先采用获准的显式覆盖，其次用模板提供的验证步骤。建议 `dev` 模板从已配置的 `testCmd` 生成默认 Gate。模板要求 Gate 而解析结果为空时，在保存前返回错误。 |
| `provider`、`model`、思考深度、审批模式 | 优先采用获准的显式覆盖，其次用模板默认值。provider 缺省跟随当前 Lead。同 provider 下可沿用 Lead 已设置的模型，其他情况用所选 provider 的默认模型。思考深度使用所选模型默认值，审批模式缺省为询问人类。Kernel 校验组合和授权后保存解析结果。 |

模板通过编译后的派发默认值声明任务区、访问模式、初始会话设置和验证步骤，允许验证步骤引用模板输入。所有默认值在受理时解析并保存，同一派发的重放读取原结果。手写 Report Task 块仍按现有契约解析，含 `ready` 和 `access` 的现有缺省行为。

## Kernel 与接口

Kernel 负责派发、目标校验、会话操作和审批规则。Host 把 HTTP、WebSocket 和 MCP 映射到这些操作。AgentKit 负责 provider 能力和协议，SQLite 通过现有仓储边界持久化。

| 能力 | HTTP / UI | Lead MCP |
| --- | --- | --- |
| 派发 | `POST /api/v1/tracks/{trackId}/tasks/dispatch` | `perga_task_dispatch` |
| 找到 Worker | Track 的 Task 状态及 Attempt 列表 | 扩展 `task_list` 返回当前 Attempt 与 Session 关联 |
| 读会话 | 扩展 `GET /api/v1/sessions/{sessionId}/items` 并复用事件 WebSocket | `perga_session_read` |
| 发消息或 Steer | 扩展会话 `messages` 接口 | `perga_session_send` |
| 中断 | 扩展会话 `interrupt` 接口 | `perga_session_interrupt` |
| 更新会话设置 | `PATCH /api/v1/sessions/{sessionId}/settings` | `perga_session_update_settings` |

派发由 `TrackActor` 用现有 Report 提交和任务同步逻辑创建 Task 块。Report、Task、解析后的初始配置和请求回执在同一事务落库。回执返回任务标识和最新状态，等待依赖或租约时即可返回。UI 生成请求键并在重试时复用。MCP 支持可选的 `requestKey`，省略时由服务端分配。调用方需要在结果未知时安全重试，就应在首个请求中提供稳定的键。无键的请求结果未知时，Lead 先查询任务。同一个调用者在同一 Track 内重放相同请求返回同一个 Task，复用请求键提交不同参数返回冲突。

事务提交后调度器才启动 Attempt。派发已受理、尚未启动时，重启可重新调度。先持久化 Attempt 和租约标识，再申请租约。租约和会话绑定落库后才启动 provider。若启动结果未知，恢复流程记录中断并确认旧执行结束后清理租约，等待显式重试。Steer 复用持久化输入回执，在发送前登记输入。仍可继续的会话可恢复已排队输入。已中断 Attempt 的输入保留在历史中，provider 是否接收不明时保留未知结果并避免自动重发。

设置、回执和暂停状态需要前向数据库迁移。已有 Task 和会话历史按原含义读取，旧轮缺失的设置显示为未知。HTTP、MCP、前端 schema 和发布的 OpenAPI 一起更新。

## 实施与验收

1. 先接通同一份会话进展和 Steer、中断控制。用受控 provider 和真实浏览器验证，人能看到 Lead 发出的 Steer，Lead 能看到人发出的消息和中断结果。覆盖重连、原生 Steer、排队、轮次过期和中断与交卷竞态。
2. 接入模型、思考深度和审批模式。覆盖下一轮生效、会话历史延续、人和 Lead 的并发修改、不支持的设置、跨 Track 请求和旧 Lead 权限失效。验证普通设置修改无法绕过要求人类审批的请求。
3. 接通只填目标的派发。验证模板默认值和显式覆盖、`dev` 的 Gate、并发重放、保存后启动前重启、启动结果未知，以及旧 Task 块的调度。最终按 L2 评审，运行完整 .NET 测试和受影响的浏览器用例。

现有实现入口见 [会话与任务编排](../src/Perga.Kernel/Actors/TrackActor.cs)、[持久化会话输入](../src/Perga.Kernel/Actors/TrackActor.SessionInputs.cs)、[HTTP 接口](../src/Perga.Host/Http/ApiV1Endpoints.cs)、[MCP 工具](../src/Perga.Kernel/Mcp/KernelMcpTools.cs)和 [交付模板](../templates/agents-delivery/)。

## 首版支持范围

- Codex Worker 支持实时 Steer 以及下轮模型和思考深度覆盖。缺少原生 Steer 的 provider 支持显式排队。审批模式提供询问与拒绝，适用于 provider 上报的审批请求。
- HTTP 接口沿用 Host 的现有访问边界。MCP 写入时以 Kernel 中记录的当前 Lead 身份为准。当前 Attempt 选定拒绝后，新的 Attempt 可以重新启用询问。
- 新 `dev` 模板快照将写任务的 Gate 绑定到 `testCmd`。已有 Track 按其存储的模板快照解析派发默认值。
