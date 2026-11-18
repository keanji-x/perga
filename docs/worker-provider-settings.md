# Worker Provider 设置与审批授权

设计稿 · 2026-11-18 · [Issue #21](https://github.com/keanji-x/perga/issues/21)

浏览器与 Lead MCP 读取同一份 Worker 能力和下一轮设置。Provider 适配器只公布实际协商或查询到的模型与思考深度。选择缺省时，Kernel 使用适配器报告的可恢复默认值。找不到可信默认值时继续使用已经明确选定的值，并在界面保留选择。Worker 暂停后保留同一 Attempt 已报告的能力，继续执行时由新打开的适配器校验可用值。

Codex 从同一 Worker app-server 读取 `model/list` 和当前目录的 `config/read`，确认模型、每个模型支持的思考深度和默认值。ACP 使用会话协商的 `model` 与 `thought_level` 选项，确认 `session/set_config_option` 回执之后才发送提示词。旧版 ACP 可以通过 `session/set_model` 确认派发时显式指定的模型。Claude 目前没有已接通的运行时设置确认接口，保留启动时的模型设置。 [ACP 会话配置协议](https://agentclientprotocol.com/protocol/v1/session-config-options)

审批选项仍为询问和拒绝。Host 的现有 whoami 是安装实例标识，放宽同一 Attempt 的审批需要额外的所有者证明。安装方可配置一个 32 字节随机授权值的 SHA-256 摘要。浏览器只在请求把拒绝改为询问时提交原值，Host 验证后将授权身份通过 Kernel 内部调用传递。原值保留在安装所有者手中，浏览器、Worker 和 Lead 的配置、历史或日志不保存原值。Lead 的会话凭证不能用于放宽审批。常规审批请求仍由人类按既有 Ask 流程决定。

同一 Worker 轮次继续使用开始时的设置。测试覆盖真实响应形状的本地 Provider 替身、设置失败时没有发送提示词、恢复默认、旧设置读取，以及无证明、错误证明和授权后的模式转换。HTTP、MCP、浏览器和 OpenAPI 共用这一语义。

## 安装实例配置

安装所有者生成一个 32 字节随机值，使用 64 位十六进制表示，并在 Host 配置项 `Perga__WorkerApprovalAuthorizationSha256` 中保存这段文本的 SHA-256 十六进制摘要。Worker 设置对话框在把审批从拒绝改为询问时接收原值。浏览器页面和 Host 都必须使用 HTTPS，携带证明的浏览器请求禁用重定向。摘要配置缺失或无效时，该 Attempt 的审批保持拒绝，直到下一次 Attempt。
