# Perga

轻量级 Agent-Native 本地研发与长周期工作区（**.NET 11 微内核 + 零耦合独立底座库 + 提纯 Rust `rmux` 终端守护 + 原生 Jujutsu `jj`**），前端沿用 `fe/`（React + TypeScript + Astryx Design），前后端通过精简的 `/api/v1`、WebSocket 与多源 `/mcp/*` 协同工作。

## 持续集成与发布

提交到 `main`、向 `main` 提交 PR，或在 GitHub Actions 手动运行 **CI and release**，都会执行 .NET 测试、Rust 终端桥检查，以及前端 lint、测试和构建。Chromium 测试单独运行。由于主分支已有失败用例，该作业在首个失败或达到时限时结束并报告结果。其余检查通过后，Actions 会验证打包后的服务能启动、终端桥可用、Web 页面和静态资源可访问，并上传 `perga-linux-x64` 构建产物。

推送 `vX.Y.Z` 格式的标签（例如 `v0.1.0`）会执行同一流程，并将验证通过的 Linux x64 压缩包及 SHA-256 校验文件发布到 GitHub Releases。

解包后的服务需要 ASP.NET Core 11 运行时。进入 `perga` 目录启动服务，然后打开 [本地 Web 界面](http://127.0.0.1:4041/)。

```sh
sha256sum -c perga-linux-x64.tar.gz.sha256
tar -xzf perga-linux-x64.tar.gz
cd perga
PERGA_DATA_DIR="$PWD/data" PERGA_STATIC_WEB_DIR="$PWD/web" \
  dotnet Perga.Host.dll --urls http://127.0.0.1:4041
```

## 仓库目录结构

- `DESIGN.md`：当前架构、工程依赖、核心接口契约与存储设计。
- `fe/`：React 前端与架构校验工具链，通过 `/api/v1` 对接 Host。
- `tools/perga-term-bridge/`：Rust 终端桥与持久化 PTY 守护。
- `docs/`：当前架构设计记录与 Flow JSON Schema。
- `libs/`、`src/`、`templates/`、`tests/`：独立底座库、服务实现、模板包与测试。

## Linux 沙箱中的 Codex

沙箱任务把 Codex 状态存放在 Perga 数据目录的独立会话目录。通过 Host 的
`Perga__CodexSandboxApiKey` 配置这类任务使用的 OpenAI API 密钥。普通 Codex CLI 的
ChatGPT 登录由原 CLI 管理。

## 许可证

遵循 [LICENSE](LICENSE)（Apache License 2.0）。
