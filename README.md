# Perga

轻量级 Agent-Native 本地研发与长周期工作区（**.NET 11 微内核 + 零耦合独立底座库 + 提纯 Rust `rmux` 终端守护 + 原生 Jujutsu `jj`**），前端沿用 `fe/`（React + TypeScript + Astryx Design），前后端通过精简的 `/api/v1`、WebSocket 与多源 `/mcp/*` 协同工作。

## 持续集成与发布

提交到 `main`、向 `main` 提交 PR，或在 GitHub Actions 手动运行 **CI and release**，都会执行 .NET 测试、Rust 终端桥检查，以及前端 lint、测试和构建。Chromium 测试单独运行。由于主分支已有失败用例，该作业在首个失败或达到时限时结束并报告结果。其余检查通过后，Actions 会验证打包后的服务能启动、终端桥可用、Web 页面和静态资源可访问，并上传 `perga-linux-x64` 构建产物。

推送 `vX.Y.Z` 格式的标签（例如 `v0.1.0`）会执行同一流程，并将验证通过的 Linux x64 压缩包及 SHA-256 校验文件发布到 GitHub Releases。

解包后的服务需要 ASP.NET Core 11 运行时。进入 `perga` 目录启动服务，然后打开 [本地 Web 界面](http://127.0.0.1:4041/next/)。

```sh
sha256sum -c perga-linux-x64.tar.gz.sha256
tar -xzf perga-linux-x64.tar.gz
cd perga
PERGA_DATA_DIR="$PWD/data" PERGA_STATIC_WEB_DIR="$PWD/web" \
  dotnet Perga.Host.dll --urls http://127.0.0.1:4041
```

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
