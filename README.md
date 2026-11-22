# Perga

以任务为中心驱动的多 AI 长周期协作工作台。

Perga 把跨越数天乃至数周的复杂任务（仓库持续交付、工程指标优化、长期深度研究）从滚屏聊天中解放出来：每一项目标是一条独立的 **Track**，对话退居边栏，主屏持续沉淀只读活报告与内核验真的真实进展；同一任务内混编你已有的 Agent CLI（Claude Code、Codex、ACP），通过轻量协议与统一编排流实现隔离执行、异构复核与零 Token 外部守望。

## 核心特点

- **活报告居中，对抗长期状态漂移**：聊天留在侧边，主屏以只读编辑型视图呈现目标、结构化业务账本与不可伪造的内核执行事实；上下文逼近上限时自动压缩或锚定报告无损换页。
- **跨厂商异构混编与人机共享控制**：由唯一 Lead 在同一工作区内调度不同厂商/模型的 Worker，强制审执分离与异构模型独立审查；人与 Lead 共享同一实时观测流，随时可 `Steer` 纠偏、中断或切换模型。
- **统一编排与零 Token 守望（Flow）**：用极轻的声明式 `flow.json` 统一并发依赖、`cron`/`at` 定时触发与零 Token 外部状态探针（如 PR Checks 或 shell 命令），无变化时不消耗模型 Token，异常与决策交还唤醒后的 Lead。
- **真实隔离现场与本地主权**：支持 Git worktree、Jujutsu（`jj`）、Linux OverlayFS（共享预热编译缓存）及远程 SSH 执行目标；独立 Rust 终端守护（`perga-term-bridge`）在主服务热升级时保持不断连，全部状态保存在本地 SQLite 中。
- **默认零模板起步，按需挂载轻量协议**：任何任务一句话即可开始，规律浮现后再按需挂载内置协议或由 Lead 现场塑形。

## 内置运行协议

- [`agents-delivery`](templates/agents-delivery) — 设计对齐、隔离实现、并行异构双 Review 与打回修复闭环、强制测试 Gate 及 PR 合并护栏。
- [`okr-loop`](templates/okr-loop) — 稳定 Objective、内核自动维护时间戳的四列 KR 总账、每日定时采集与带局部 Expect 核查的实验闭环。
- [`equity-research`](templates/equity-research) — 无框估值价格区间轴、事件 Expect 核查时间线、常青深度分析与定期异构复核。

## 快速开始

从 GitHub Releases 下载最新验证通过的发布包（运行需要 **ASP.NET Core 11** 运行时，以及至少一个已配置的本机 Agent CLI，如 `claude`、`codex` 或 ACP 提供方）：

```sh
sha256sum -c perga-linux-x64.tar.gz.sha256
tar -xzf perga-linux-x64.tar.gz
cd perga
PERGA_DATA_DIR="$PWD/data" PERGA_STATIC_WEB_DIR="$PWD/web" \
  dotnet Perga.Host.dll --urls http://127.0.0.1:4041
```

启动后在浏览器打开 [http://127.0.0.1:4041/next/](http://127.0.0.1:4041/next/)（桌面与移动端均支持安装为 PWA）。

### Linux 沙箱中的 Codex

沙箱任务把 Codex 状态存放在 Perga 数据目录的独立会话目录。通过 Host 的 `Perga__CodexSandboxApiKey` 配置这类任务使用的 OpenAI API 密钥；普通 Codex CLI 的 ChatGPT 登录由原 CLI 管理。

## 持续集成与发布

提交到 `main`、向 `main` 提交 PR，或在 GitHub Actions 手动运行 **CI and release**，都会执行 .NET 测试、Rust 终端桥检查，以及前端 lint、测试和构建。Chromium 测试单独运行；其余检查通过后，Actions 会验证打包后的服务启动、终端桥与静态资源访问，并上传 `perga-linux-x64` 构建产物。

推送 `vX.Y.Z` 格式的标签（例如 `v0.1.0`）会执行同一流程，并将 Linux x64 压缩包及 SHA-256 校验文件发布到 GitHub Releases。

## 文档索引

### 产品思考
- [愿景与根本矛盾](docs/product/vision.md) — 核心命题、四大根本矛盾、AI 组织的四个物理不对称与长期愿景
- [产品设计与痛点优先级](docs/product/design.md) — P0–P3 具体痛点、最小解法、刻意暂缓边界与同类范式差异
- [典型场景应用](docs/product/scenarios.md) — `agents-delivery`、`okr-loop`、`equity-research` 落地与延展方式

### 架构与部署
- [系统架构参考](DESIGN.md) — 18 个 C# 工程拓扑、依赖矩阵、核心模块与契约索引
- [Unified Track Flow 架构](docs/design/unified-track-flow-architecture.md) — 统一编排流与只读 `builtin:flow` 设计
- [模板 Taste 与信息设计原则](docs/design/template-taste.md) — 只读出版物视图与信息减法原则
- [Linux OverlayFS 工作区](docs/track-overlayfs.md) — 预热构建缓存共享与命名空间挂载配置
- [自托管安装与热升级](docs/design/install-and-upgrade.md) — 双 systemd 用户服务部署与无损升级

## 许可证

遵循 [LICENSE](LICENSE)（Apache License 2.0）。
