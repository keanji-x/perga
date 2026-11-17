# Neige C# + Rust 终端 + 原生 JJ 终版工程蓝图（V5 定稿版）

> **定稿架构总览**：
> - **C# 业务层 (`src/*` ~4,500 行)**：`Neige.Kernel`（无锁 `TrackActor` + 宿主自管换页压缩 + `.neige.ts` 5 切面驱动）+ `Neige.Storage.Sqlite`（10 张表 WAL 单写通道）+ `Neige.Host`（精简 REST/WS + 内置常用工具 + 静态外部 MCP 直挂）。
> - **C# 独立零耦合底座库 (`libs/*` ~3,700 行)**：`AgentKit`（Harness 兼容解毒库）、`PolicyVm`（Jint 受限 TS 规则沙箱）、`ReportDoc`（块文档与 Zstd 快照库）、`VcsKit`（**原生一等支持 Jujutsu `jj` + Git 兜底**）。
> - **Rust 跨平台终端 Sidecar (`tools/neige-term-daemon` ~600 行)**：直接站在 Rust 最强跨平台生态 **`portable-pty`（Linux/macOS PTY + Windows ConPTY）+ `vt100`** 肩膀上，通过本地 IPC 桥接 C#，兼得跨三端能力与主服务热重启不断连！

---

## 一、三大实战痛点的彻底工程解法

### 1. 多 Harness 兼容彻底解毒（告别 Compaction / Sandbox / 子命令泥潭）

老版本在 Rust 里最痛苦的根源，是试图去适配和控制每家 CLI **极其脆弱且频繁变动的内部私有机制**（如 `acp_planner/profile` 里三种不同的 `CompactionWire`、`claude_planner/spawn.rs` 里手拼 `Edit(//path/**)` 遇到带括号路径就炸、Codex `shell_environment_policy.set` 冲掉内置 `rg`/`apply_patch`）。

**在 `libs/AgentKit` + `Neige.Kernel` 中用三条铁律彻底解毒**：

1. **Compaction 收归宿主自己做（Epoch 换页压缩 —— 0 行 CLI 私有压缩适配！）**
   - **绝不调用**任何 CLI 的 `/compact` 斜杠命令或私有压缩 RPC！
   - 当会话 Token 达到阈值（或用户/DSL 触发压缩）时，`TrackActor` 直接向当前会话发一条**最普通的对话消息**：*“请输出结构化交接摘要（目标进展、已确认事实、未决事项）”*。
   - 拿到回复后，**直接关闭旧底层 `native_thread_id`，用「最新 Report + 这份交接摘要」在底层开一个全新的 `native_thread_id`**（对前端而言 `SessionId` 不变，只是 `epoch + 1`）。100% 兼容天下所有 CLI，永远不会因某家改了 `/compact` 行为而卡死！
2. **Sandbox 移出 CLI 参数，统一外置给 OS 进程包装器 + MCP DSL 门禁**
   - **绝不手拼** Claude `--allowedTools "Edit(...)"` 或 Codex 内部复杂沙箱配置（底层 CLI 统一以无阻塞纯协议模式启动，保留其原生工具链完整性）。
   - 如果需要文件系统/网络边界隔离，在拉起子进程的最外层套统一的 **OS 级沙箱 Wrapper**（Linux 用 `bwrap`、macOS 用 `sandbox-exec`、Windows 用 Restricted JobObject）——所有 Agent 共用同一套 50 行规则，CLI 怎么升级都不受影响！
   - 业务动作权限（如发 PR、合并代码、改配置）统一收口在 Neige 的 MCP 通道上，由 `.neige.ts` 的 `guardToolCall` 确定性拦截。
3. **协议面锁死「ACP 最小 4 原语」，禁碰花哨子命令**
   - Antigravity、OpenCode 原生讲标准 **ACP (Agent Client Protocol)**；Claude 与 Codex 优先接入成熟官方/社区 ACP 桥接器（`claude-agent-acp` / `codex-acp`），或仅用极薄的 `stream-json` / `app-server` 封装映射成完全相同的 **4 个最小原语**：
     `OpenSession(cwd, env, mcpServers)` · `StreamPrompt(text)` · `CancelTurn()` · `RespondPermission()`。
   - 新增一个 Agent 只需配 20 行启动描述，零状态机分叉。

---

### 2. Terminal 桥接：直接去芜存菁复用你现有的 `rmux` 解析与交互栈（`neige-term-bridge`）

核查现有仓库源码后发现，老代码里其实并存着**新旧两代**终端实现：
- **旧代包袱（直接砍掉）**：`calm-proc-supervisor`（8.9k 行）+ `calm-session/terminal_model.rs`（基于 `vte` 手写的网格）+ `terminal_renderer/legacy_*`。
- **新代精华（写得非常扎实地道，100% 保留提纯复用！）**：
  1. **`crates/calm-terminal-view`（仅 234 行，`#![forbid(unsafe_code)]`）**：基于 `rmux-core::TerminalScreen`（Rust 版 tmux 内核），宽字符 padding 对齐、SGR 属性补齐、Alternate Screen 全屏菜单识别、`MODE_BRACKETPASTE` 感知、2000 行历史滚动偏移，以及抽空 `take_replies()` 防重复终端应答都已经打磨得极其干净——而且它是**纯内存字节状态机，天然 100% 跨 Linux / macOS / Windows 全平台通用**！
  2. **`terminal_interaction/{screen_diff, text_wait, repaint, actions}`**：基于每行哈希 `row_hashes` + 光标快照的屏幕静默稳定检测（`settle_ms`）、`wait_text` / `wait_text_absent`、以及自适应 Bracketed Paste 按键编码。只需剥掉里面混入的 `RouteRepo` 和 `CardRole` 业务引用即可直接独立！
  3. **跨系统 PTY 宿主底座**：
     - **Linux / macOS**：直接继续跑现有的 **`crates/calm-terminal-runtime` (`rmux-server`)**（自带持久会话与 keyframe 恢复，C# 主服务重启时终端完全不断连；仅需把 `isolated_command` 里的 Linux 专属 `unshare` 改为 Linux 可选启用、macOS 走普通进程组即可）。
     - **Windows**：若需支持原生 Windows，只需用 `portable-pty`（ConPTY）提供底层字节读写，吐出的字节流照样喂给同一个 `calm-terminal-view` (`TerminalView`) 与 `screen_diff` 等待引擎！
- **与 C# 的桥接方式**：将提纯后的 `calm-terminal-view` + `calm-terminal-runtime` + `terminal_interaction` 封装为一个零业务依赖的本地 Sidecar `neige-term-bridge`（暴露本地 WebSocket / JSON-RPC），C# `Neige.Host` 纯托管异步调用，0 行 `DllImport`。

---

### 3. 插件务实化 + 原生一等支持 `jj`（Jujutsu VCS）

#### (A) `libs/VcsKit`：把 `jj` 作为一等公民原生支持（同时保留 Git 兜底）
为什么 `jj`（尤其是 `jj git init --colocate` 共存模式）能把老版本 5,300 行的 Git 痛点一扫而空：
1. **工作区自动即快照（Working-copy-is-a-commit `@`）**：没有暂存区（Index），Agent 改完文件退出那一刻，调一行 `jj log -r @ --no-graph -T 'change_id ++ ":" ++ commit_id'` 就能拿到稳定的 **Change ID**（后续怎么 rebase 都不会变！），彻底省掉老版复杂的自动 `git add -A && git commit` 状态机！
2. **`jj workspace add` 轻量多轨并行**：每个 Track 创建时执行 `jj workspace add <dir> --name track-<id> -r main`，无需像 Git worktree 那样抢分支名；关闭 Track 时 `jj workspace forget track-<id>` 干净利落。
3. **永不卡死的 Rebase + `jj op restore` 时光机**：
   - 上游更新时跑 `jj git fetch && jj rebase -s <root> -d main@origin`，**即使有代码冲突也 100% 正常退出不卡半路**（冲突作为一等公民存在树里，直接派个 Task 解决即可）！
   - 每次派发读写 Worker 前记录当前 `op_id`（`jj op log -n 1`），万一 Worker 把环境搞坏，一行 `jj op restore <op_id>` 毫秒级原子回滚！
4. **无缝发 GitHub PR**：因为开启 `--colocate` 与 `.git` 共存，Cargo/IDE/`gh` 照常工作；发布 PR 时只需 `jj bookmark set neige/<id> -r @- && jj git push --bookmark neige/<id>` 再调 `gh pr create` 即可！
*(注：遇到未装 `jj` 或纯 Git 环境时，`IVcsWorkspace` 自动降级走 `GitBackend` 兜底，上层完全透明。)*

#### (B) 首期插件体系：不做大而全宿主，直击常用高频能力
1. **编译内置的核心工具集（零配置开箱即用）**：
   - **VCS & GitHub Forge 工具集**（基于 `VcsKit`：JJ 快照/Diff/回滚/Rebase + `gh` Issue/PR/CI 检查/合并）；
   - **Terminal & Preview 工具集**（对接 `neige-term-daemon` + 本地开发端口预览网关）；
   - **Source 存证工具集**（`source_capture` / `source_ls`，满足日常调研与投研引用）。
2. **外部常用 MCP 静态配置直挂（`mcp-plugins.json`，~150 行代码）**：
   - 不搞复杂的插件安装器、数据库表和版本协商；写一个简单的 `mcp-plugins.json` 配置文件，把你平时真正在用的 **智堡 Wisburg MCP**、**老仓库现成编译好的 `market` / `barra` 二进制**、**Playwright MCP** 配进去，按 Track 勾选注入 Agent 并走 `.neige.ts` 的 `guardToolCall` 门禁即可！

---

## 二、定稿工程目录结构一览

```text
neige-next/
├── libs/                           # 零业务耦合 C# 独立库（可单独单测与跨项目复用）
│   ├── AgentKit/                   # (~1,800 行) ACP 最小 4 原语适配 + OS 沙箱包装器
│   ├── PolicyVm/                   # (~650 行)   Jint 受限 TS 规则沙箱 (.neige.ts)
│   ├── ReportDoc/                  # (~650 行)   纯内存块文档解析、Anchor 乐观锁与 Zstd 快照
│   └── VcsKit/                     # (~600 行)   原生 Jujutsu (jj) 一等后端 + Git 兜底 + gh CLI
│
├── tools/
│   └── neige-term-daemon/          # (~600 行 Rust) portable-pty (Linux/macOS/Win ConPTY) + vt100 守护
│
├── src/                            # Neige 业务组装层
│   ├── Neige.Kernel/               # (~2,200 行) TrackActor、宿主换页 Compaction、Task DAG、TemplateHost
│   ├── Neige.Storage.Sqlite/       # (~800 行)   10 张表 WAL 单写通道微批提交 + 只读池
│   └── Neige.Host/                 # (~1,800 行) /api/v1 REST & WS、/mcp/kernel、内置 JJ/Source 工具、静态插件桥
│
├── templates/                      # 动态可编程 .neige.ts 模板包 (dev / long-term-goal / research)
└── fe/                             # 协同瘦身后的前端应用
```
