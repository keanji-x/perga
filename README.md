# Perga

轻量级 Agent-Native 本地研发与长周期工作区（**.NET 11 微内核 + 零耦合独立底座库 + 提纯 Rust `rmux` 终端守护 + 原生 Jujutsu `jj`**），前端沿用 `fe/`（React + TypeScript + Astryx Design），前后端通过精简的 `/api/v1`、WebSocket 与多源 `/mcp/*` 协同工作。

## 仓库目录结构

- `DESIGN.md`：冻结架构规范、工程依赖矩阵、核心接口契约、`SQLite WAL` Schema 与三阶段并行实施路线。
- `fe/`：已迁移的下一代 React 前端与架构校验工具链（待 Stage 2 协同瘦身对接 `/api/v1`）。
- `tools/rust-terminal/`：迁移保留的 `calm-terminal-view`（`rmux-core` 屏幕与宽字符解析）、`calm-terminal-runtime`（`rmux-server` 持久化 PTY 底座）与 `terminal_interaction_ref/` 参考算法，用于提纯组装跨平台守护 `tools/perga-term-bridge`。
- `plugins/`：保留的有价值独立领域插件源码（`market`、`barra`、`invest`、`paper-trading`、`research-archive`），已剔除虚拟桌面 `desktop` 与旧 `git-forge`（由原生 `VcsKit` 接管）。
- `docs/`：架构拓扑图（`csharp-architecture.html`）、源码审计预研报告（`csharp-rewrite-prestudy.md`）与架构蓝图（`csharp-rewrite-blueprint.md`）。
- `libs/`、`src/`、`templates/`、`tests/`：将按 `DESIGN.md` 第 5 节的 Stage 0 / Stage 1 / Stage 2 路线落地 `.NET 11` 微内核与底座库。

## Linux 沙箱中的 Codex

沙箱任务把 Codex 状态存放在 Perga 数据目录的独立会话目录。通过 Host 的
`Perga__CodexSandboxApiKey` 配置这类任务使用的 OpenAI API 密钥。普通 Codex CLI 的
ChatGPT 登录由原 CLI 管理。

## 许可证

遵循 [LICENSE](LICENSE)（Apache License 2.0）。
