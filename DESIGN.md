# Neige.Next 详细架构设计与冻结接口规范（DESIGN.md）

> **文档性质**：本规范为 **Neige.Next（C# .NET 9 微内核 + 零耦合独立库 + 提纯 Rust 终端守护 + 原生 Jujutsu `jj`）** 的唯一权威设计基线与冻结契约。后续 Stage 0 脚手架搭建与 Stage 1 / Stage 2 Swarm 并行实现必须严格遵守本文档第 2–4 节定义的命名空间、接口签名、SQL DDL 与线协议，未经架构协调者批准不得擅自变更跨模块公开契约。

---

## 1. 工程拓扑与严格无环依赖规则

### 1.1 仓库与 Solution 目录布局

实现仓库（`perga`）位于 `/home/kenji/neige-workspaces/78d0fc64f6394212b88223f52273e9dd/0356c8755af142cab9f7919f5e82ce20`：

```text
Neige.Next.sln                            # （待 Stage 0 生成）
├── DESIGN.md                             # 本架构与冻结接口规范
├── README.md / LICENSE / rust-toolchain.toml
├── Directory.Build.props                 # 统一 net9.0 / Nullable=enable / ImplicitUsings=enable / TreatWarningsAsErrors=true
├── fe/                                   # 已迁移的前端（React + TypeScript + Astryx Design）
├── docs/                                 # 架构拓扑图与前期 Rust 审计蓝图（已迁移）
├── plugins/                              # 保留的独立领域插件（market / barra / invest / paper-trading / research-archive）
├── libs/                                 # 零业务耦合独立底座库（严禁引用任何 src/* 或彼此环路引用）
│   ├── Neige.ReportDoc/
│   │   └── Neige.ReportDoc.csproj        # 依赖: ZstdSharp.Port
│   ├── Neige.PolicyVm/
│   │   └── Neige.PolicyVm.csproj         # 依赖: Jint
│   ├── Neige.AgentKit/
│   │   └── Neige.AgentKit.csproj         # 无重型三方依赖（System.Text.Json + Process/Pipes + OS Sandbox Wrapper）
│   ├── Neige.VcsKit/
│   │   └── Neige.VcsKit.csproj           # 无重型三方依赖（原生调用 jj / git / gh CLI）
│   └── Neige.TermClient/
│       └── Neige.TermClient.csproj       # 轻量 UDS/TCP JSON-RPC + WebSocket 客户端对接 neige-term-bridge
│
├── tools/
│   ├── rust-terminal/                    # 已迁移的 Rust 终端种子（calm-terminal-view / calm-terminal-runtime / terminal_interaction_ref）
│   └── neige-term-bridge/                # 独立纯 Rust 跨平台终端守护（#![forbid(unsafe_code)]）
│       ├── Cargo.toml                    # 提纯组装自 tools/rust-terminal/（rmux-core / rmux-server / rmux-sdk / portable-pty）
│       └── src/
│           ├── main.rs                   # CLI 参数（--listen-uds / --listen-tcp）与双监听启动
│           ├── view.rs                   # 提纯自 calm-terminal-view/src/lib.rs
│           ├── runtime.rs                # 提纯自 calm-terminal-runtime（macOS 可选跳过 unshare + Windows ConPTY trait）
│           ├── screen_diff.rs            # 提纯自 terminal_interaction_ref/screen_diff.rs
│           ├── actions.rs                # 提纯自 terminal_interaction_ref/actions.rs
│           ├── wait_plan.rs              # 提纯自 terminal_interaction_ref/wait_plan.rs & text_wait.rs
│           ├── rpc.rs                    # JSON-RPC 2.0 分发（term.ensure / observe / action / resize / kill）
│           └── ws.rs                     # 二进制 WS 流（0x01 raw PTY bytes, 0x02 JSON control）+ 64KB 环形缓冲回放
│
├── src/                                  # Neige 应用层（仅组合上述独立库）
│   ├── Neige.Contracts/
│   │   └── Neige.Contracts.csproj        # 纯跨进程/跨层共享 DTO、Enum、REST/WS 协议模型（零业务或 IO 依赖）
│   ├── Neige.Kernel/
│   │   └── Neige.Kernel.csproj           # 引用 Contracts + 5 个 libs/*；定义 ITrackStore / ITrackReadStore（零 SQLite/Web 引用！）
│   ├── Neige.Storage.Sqlite/
│   │   └── Neige.Storage.Sqlite.csproj   # 依赖反转引用 Neige.Kernel；依赖 Microsoft.Data.Sqlite + Dapper
│   └── Neige.Host/
│       └── Neige.Host.csproj             # ASP.NET Core 组合根；引用 src/* + ModelContextProtocol.AspNetCore + Cronos
│
├── templates/                            # 内置动态可编程模板与 TS 类型检查声明（Jint 直接跑 .neige.js）
│   ├── tsconfig.json                     # { allowJs: true, checkJs: true, noEmit: true } (编辑期/CI 强类型校验)
│   ├── neige-template.d.ts
│   ├── dev.neige.js
│   ├── long-term-goal.neige.js
│   └── investment-research.neige.js
│
├── scripts/hooks/                        # 供模板可选调用的标准生命周期脚本示例
└── tests/
    ├── Neige.ReportDoc.Tests/
    ├── Neige.PolicyVm.Tests/
    ├── Neige.AgentKit.Tests/
    ├── Neige.VcsKit.Tests/
    ├── Neige.Storage.Tests/
    ├── Neige.Kernel.Tests/
    └── Neige.Host.IntegrationTests/
```

### 1.2 全局编译期约束（`Directory.Build.props`）

所有 C# 工程共享以下 `Directory.Build.props`，严禁各 `.csproj` 私自关闭可空检查或放行警告：

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>13</LangVersion>
  </PropertyGroup>
</Project>
```

### 1.3 严格单向依赖矩阵

| 模块 | 允许引用的工程 | 严禁引用的对象（违者架构验收拦截） |
| :--- | :--- | :--- |
| `Neige.Contracts` | 无（仅 BCL） | 任何项目、任何 NuGet IO/Web/DB 库 |
| `libs/Neige.ReportDoc` | 无（仅 `ZstdSharp.Port`） | `src/*`、其他 `libs/*`、`Microsoft.Data.Sqlite` |
| `libs/Neige.PolicyVm` | 无（仅 `Jint`） | `src/*`、其他 `libs/*`、任何文件/网络 IO |
| `libs/Neige.AgentKit` | 无（仅 BCL） | `src/*`、其他 `libs/*`、SQLite、ASP.NET Core |
| `libs/Neige.VcsKit` | 无（仅 BCL） | `src/*`、其他 `libs/*` |
| `libs/Neige.TermClient` | 无（仅 BCL） | `src/*`、其他 `libs/*` |
| `Neige.Kernel` | `Neige.Contracts` + 5 个 `libs/*` | `Microsoft.Data.Sqlite`、`Dapper`、`AspNetCore`、`Neige.Storage.Sqlite`、`Neige.Host` |
| `Neige.Storage.Sqlite` | `Neige.Kernel` + `Neige.Contracts` | `Neige.Host`、`AspNetCore` |
| `Neige.Host` | `Neige.Kernel`、`Neige.Storage.Sqlite`、`Neige.Contracts`、`libs/*` | 无（组合根） |

---

## 2. 冻结 C# 接口与核心数据契约

以下所有命名空间、类型名、属性签名与方法签名即为 Stage 0 落盘编译的冻结契约。

### 2.1 `namespace Neige.Contracts`（共享 Wire 模型）

```csharp
using System.Text.Json;

namespace Neige.Contracts;

public enum SessionKind { Interactive, TaskAttempt }
public enum AccessMode { ReadOnly, ReadWrite }
public enum TaskRunStatus { Draft, Pending, Running, Verifying, Done, Failed, Canceled }
public enum TaskTriggerSource { Manual, Scheduler, Wake }
public enum SemanticVerdict { None, Accepted, Rejected }
public enum AskStatus { Open, Answered, Withdrawn }
public enum TerminalStatus { Running, Exited }
public enum EditActorKind { Human, Planner, Assistant, System }

public sealed record AreaDto(
    string Id,
    string Name,
    string Color,
    string? DefaultCwd,
    IReadOnlyList<string> Folders,
    double SortOrder,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record TrackDto(
    string Id,
    string AreaId,
    string? ParentTrackId,
    string? ParentAttemptId,
    string Title,
    string Cwd,
    string WorkspaceDriver,
    JsonElement WorkspaceSpec,
    string? PrimarySessionId,
    string? TemplateId,
    JsonElement TemplateInputs,
    JsonElement TemplateState,
    IReadOnlyList<string> EnabledPlugins,
    long? ClosedAtMs,
    long? PinnedAtMs,
    double SortOrder,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record AgentSessionDto(
    string Id,
    string TrackId,
    SessionKind Kind,
    bool IsPrimary,
    string? TaskId,
    string? Title,
    string Provider,
    string? Model,
    string? NativeThreadId,
    int Epoch,
    string Status,
    double ContextUsageRatio,
    long? ArchivedAtMs,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record SessionTurnDto(
    string Id,
    string SessionId,
    int TurnIndex,
    int Epoch,
    string TriggerKind,
    JsonElement Input,
    string Status,
    JsonElement? Usage,
    string? ErrorText,
    long StartedAtMs,
    long? CompletedAtMs);

public sealed record SessionItemDto(
    long Id,
    string SessionId,
    string TurnId,
    string Kind,
    JsonElement Payload,
    long CreatedAtMs);

public sealed record GateStepDto(string Name, string Cmd);

public sealed record GateSpecDto(
    IReadOnlyList<GateStepDto> Steps,
    int TimeoutSecs = 1800);

public sealed record TaskDto(
    string Id,
    string TrackId,
    string BlockId,
    string TaskKey,
    int Generation,
    string Kind,
    string? Provider,
    AccessMode Access,
    bool Ready,
    IReadOnlyList<string> DependsOn,
    string GoalOrCmd,
    JsonElement? Context,
    string? Acceptance,
    GateSpecDto? Gate,
    TaskRunStatus Status,
    string? StatusDetail,
    SemanticVerdict SemanticVerdict,
    string? SemanticNote,
    string? LatestAttemptId,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record TaskAttemptDto(
    string AttemptId,
    string TaskId,
    string TrackId,
    string? SessionId,
    string Status,
    JsonElement? WorkerResult,
    JsonElement Meta,
    JsonElement? WorkspaceLease,
    JsonElement? GateVerdict,
    long StartedAtMs,
    long? FinishedAtMs);

public sealed record AskQuestionDto(
    string Question,
    IReadOnlyList<string> Options,
    bool IsMultiSelect = false);

public sealed record AskDto(
    string Id,
    string TrackId,
    AskStatus Status,
    IReadOnlyList<AskQuestionDto> Questions,
    JsonElement? Answer,
    long CreatedAtMs,
    long? AnsweredAtMs);

public sealed record MailDto(
    string Id,
    string FromTrackId,
    string ToTrackId,
    string Summary,
    string Body,
    long? ReadAtMs,
    long CreatedAtMs);

public sealed record TerminalDto(
    string Id,
    string TrackId,
    string? AttemptId,
    string? IdempotencyKey,
    string Title,
    string? Program,
    string Cwd,
    TerminalStatus Status,
    int? ExitCode,
    long CreatedAtMs,
    long? ExitedAtMs);

public sealed record EventEnvelopeDto(
    long? EventId,
    string? AreaId,
    string? TrackId,
    string Kind,
    JsonElement Payload,
    long CreatedAtMs);

public sealed record DbMaintenanceStatsDto(
    int PrunedEvents,
    int ThinnedRevisions,
    int CompactedItems,
    int ReclaimedPages);
```

---

### 2.2 `namespace Neige.ReportDoc`（纯内存块文档与 Zstd 快照库）

- **职责**：解析带 `<!-- neige:b_xxxx -->` 块锚点的 Markdown 报告与 `neige-block` 围栏（`task` / `html` / `table` / `view`）；执行基于 `ReadAnchor` 的并发乐观锁防盲写校验；提供 `ReplaceSection` / `UpsertBlock` / `DeleteBlock` / `MoveBlock` 原子编辑；提供 Zstd 历史快照压缩/解压。
- **不变量**：
  - Block 在 `# `（H1）与 ` ```neige-block <kind>` 边界切分，维护稳定 6 字符 ID（如 `b_a1b2`）。
  - 修改或删除已存在块/章节时，`callerAnchor` 必须满足 `WholeDocumentRead == true` 或在 `KnownBlockRevs` 中包含目标块且版本匹配当前块 `Rev`；否则抛出 `ReportConflictException` 附带冲突详情。

```csharp
using System.Text.Json;

namespace Neige.ReportDoc;

public enum ReportBlockKind
{
    Prose,
    Task,
    Html,
    Table,
    View
}

public sealed record ReportBlock(
    string Id,
    int Rev,
    ReportBlockKind Kind,
    string? SectionH1,
    string? Markdown,
    JsonElement? Payload);

public sealed record ReadAnchor(
    int DocRev,
    IReadOnlyDictionary<string, int> KnownBlockRevs,
    bool WholeDocumentRead);

public abstract record BlockOp
{
    public sealed record ReplaceSectionOp(string SectionH1, string Markdown) : BlockOp;
    public sealed record UpsertBlockOp(
        string? Id,
        ReportBlockKind Kind,
        string? Markdown,
        JsonElement? Payload,
        int? Position = null) : BlockOp;
    public sealed record DeleteBlockOp(string Id) : BlockOp;
    public sealed record MoveBlockOp(string Id, int ToIndex) : BlockOp;
}

public sealed record ReportDocument(
    int DocRev,
    string Summary,
    string LeadingComment,
    IReadOnlyList<ReportBlock> Blocks);

public sealed record BlockChangeRecord(
    string Id,
    ReportBlockKind Kind,
    int? PreviousRev,
    int? CurrentRev,
    int? Position,
    bool Moved);

public sealed record ReportCommitResult(
    ReportDocument NextDocument,
    IReadOnlyList<BlockChangeRecord> Changes);

public sealed class ReportConflictException : Exception
{
    public string Code { get; }
    public string NextStep { get; }

    public ReportConflictException(string code, string message, string nextStep)
        : base(message)
    {
        Code = code;
        NextStep = nextStep;
    }
}

public interface IReportDocEngine
{
    ReportDocument Parse(string rawMarkdown, int docRev = 0, string summary = "");
    string RenderMarkdown(ReportDocument doc, bool withMarkers = false);
    ReportCommitResult ApplyCommit(
        ReportDocument current,
        ReadAnchor callerAnchor,
        IReadOnlyList<BlockOp> ops,
        string? newSummary = null);
    byte[] CompressZstd(IReadOnlyList<ReportBlock> blocks);
    IReadOnlyList<ReportBlock> DecompressZstd(byte[] compressed);
}
```

---

### 2.3 `namespace Neige.PolicyVm` 与 `.neige.js` SDK（零手写解析 · 受限策略虚拟机）

- **职责（彻底消灭手写类型擦除解析器）**：`Neige.PolicyVm` **不做任何手写正则或字符串类型擦除**，直接接收标准 **ECMAScript Module (`.neige.js`)**，由 **Jint 4.x 内置 Acornima JS/ESM 引擎**原生编译执行 5 个生命周期钩子，堆栈行号 1:1 精确对齐。
- **编辑期/CI 强类型保障**：所有 `.neige.js` 头部声明 `// @ts-check` 与 `/// <reference path="./neige-template.d.ts" />`（配合 `templates/tsconfig.json` 的 `allowJs + checkJs + noEmit`），编辑器与 CI 通过 `tsc --noEmit` 直接享受与 `.ts` 同等的完整泛型推断与只读类型检查；若未来外部作者确需用 `.ts` 编写策略，应在打包发布前由离线构建链（如 Node 22 原生 `module.stripTypeScriptTypes` 或 `esbuild`）输出标准 `.neige.js` 再交由内核加载。
- **沙箱安全硬约束**：
  - `Options`: `.Strict()`, `.LimitMemory(16 * 1024 * 1024)`, `.MaxStatements(10_000)`, `.TimeoutInterval(TimeSpan.FromMilliseconds(50))`, `.LimitRecursion(64)`。
  - **禁止暴露**任何 CLR 类型反射、文件系统、`HttpClient`、进程或环境变量读取能力；`globalThis` 与模板全局变量仅注入纯 JSON 辅助函数 `defineTemplate` / `Allow` / `Deny` / `AskHuman`（模块无需依赖外部 `node_modules` 解析即可直接 `export default defineTemplate(...)`）。

```csharp
using System.Text.Json;

namespace Neige.PolicyVm;

public abstract record GuardDecision
{
    public sealed record Allow : GuardDecision;
    public sealed record Deny(string Reason) : GuardDecision;
    public sealed record AskHuman(string Title, IReadOnlyList<string> Options) : GuardDecision;
}

public sealed record SectionRule(
    string H1,
    bool OmitIfEmpty = false,
    IReadOnlyList<string>? EditableBy = null);

public sealed record ScheduleSpec(
    string Id,
    string Cron,
    string Timezone,
    string? StaticWakePrompt = null);

public sealed record CompiledTemplateManifest(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<string> DefaultPlugins,
    JsonElement InputsSchema,
    IReadOnlyList<SectionRule> Sections,
    int? MaxProseChars,
    bool RequireGateForReadWrite,
    string? WorkspaceDriver,
    IReadOnlyList<string> WorkspaceLowerCacheDirs,
    string? WorkspaceCreateCmd,
    string? WorkspaceCloseCmd,
    string? AfterWorkerDoneCmd,
    IReadOnlyList<ScheduleSpec> Schedules,
    string InitialReportMarkdown);

public sealed record CompiledPolicy(
    string TemplateId,
    string JsSource,
    CompiledTemplateManifest Manifest);

public sealed record PolicyEvalResult<T>(
    JsonElement NextState,
    T Output);

public sealed record PolicyTurnContext(
    string TrackId,
    string SessionId,
    bool IsPrimarySession,
    string SessionKind,
    double ContextUsageRatio,
    JsonElement Inputs);

public sealed record PolicyReportCommitContext(
    string TrackId,
    string EditorRole,     // "human" | "planner" | "assistant" | "system"
    string PrevMarkdown,
    string NextMarkdown,
    IReadOnlyList<string> TouchedSections,
    int NextProseChars,
    JsonElement Inputs);

public sealed record PolicyToolCallContext(
    string TrackId,
    string SessionId,
    bool IsPrimarySession,
    string McpServer,      // "neige" | "vcs" | "<pluginId>"
    string ToolName,
    JsonElement Arguments,
    JsonElement Inputs);

public sealed record PolicyTaskSettledContext(
    string TrackId,
    string TaskKey,
    string AccessMode,     // "read_only" | "read_write"
    string AttemptStatus,  // "done" | "failed" | "canceled"
    JsonElement? WorkerResult,
    JsonElement AttemptMeta,
    JsonElement? GateVerdict,
    JsonElement Inputs);

public interface IPolicyVm
{
    CompiledPolicy Compile(string jsModuleSource);
    PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs);
    PolicyEvalResult<string?> BuildTurnContext(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state);
    PolicyEvalResult<GuardDecision> GuardReportCommit(CompiledPolicy policy, PolicyReportCommitContext ctx, JsonElement state);
    PolicyEvalResult<GuardDecision> GuardToolCall(CompiledPolicy policy, PolicyToolCallContext ctx, JsonElement state);
    PolicyEvalResult<string?> OnTaskSettled(CompiledPolicy policy, PolicyTaskSettledContext ctx, JsonElement state);
    PolicyEvalResult<string?> BuildScheduleWakePrompt(CompiledPolicy policy, string scheduleId, JsonElement inputs, JsonElement state);
}
```

#### `templates/neige-template.d.ts`（冻结脚本契约 · `@ts-check` + Jint 原生直跑）

```ts
export type GuardDecision =
  | { kind: "allow" }
  | { kind: "deny"; reason: string }
  | { kind: "ask_human"; title: string; options?: string[] };

export interface SectionSpec {
  h1: string;
  omitIfEmpty?: boolean;
  editableBy?: Array<"human" | "planner" | "assistant" | "system">;
}

export interface TemplateContext<TState = Record<string, any>, TInputs = Record<string, any>> {
  trackId: string;
  inputs: TInputs;
  state: TState;
}

export interface NeigeTemplateDefinition<TState = Record<string, any>, TInputs = Record<string, any>> {
  id: string;
  title: string;
  description?: string;
  plugins?: string[];
  inputs?: Record<string, { type: "string" | "number" | "boolean" | "enum"; values?: string[]; default?: any; optional?: boolean; label?: string }>;
  initialState?: (inputs: TInputs) => TState;
  initialMarkdown?: string;
  workspace?: {
    driver?: string;
    lowerCacheDirs?: string[];
    onCreateCmd?: string;
    onCloseCmd?: string;
  };
  report?: {
    maxProseChars?: number;
    sections?: SectionSpec[];
    customValidate?: (ctx: TemplateContext<TState, TInputs>, info: {
      editorRole: string;
      prevMarkdown: string;
      nextMarkdown: string;
      touchedSections: string[];
      nextProseChars: number;
    }) => GuardDecision;
  };
  tasks?: {
    requireGateForReadWrite?: boolean;
    afterWorkerDoneCmd?: string;
    onSettled?: (ctx: TemplateContext<TState, TInputs>, event: {
      taskKey: string;
      accessMode: "read_only" | "read_write";
      attemptStatus: string;
      workerResult?: any;
      meta: Record<string, any>;
      gateVerdict?: any;
    }) => string | void;
  };
  buildTurnContext?: (ctx: TemplateContext<TState, TInputs>, session: {
    sessionId: string;
    isPrimary: boolean;
    sessionKind: string;
    contextUsageRatio: number;
  }) => string | void;
  guardToolCall?: (ctx: TemplateContext<TState, TInputs>, call: {
    sessionId: string;
    isPrimary: boolean;
    mcpServer: string;
    toolName: string;
    arguments: Record<string, any>;
  }) => GuardDecision;
  schedules?: Array<{
    id: string;
    cron: string;
    timezone?: string;
    wakePrompt: string | ((ctx: TemplateContext<TState, TInputs>) => string);
  }>;
}

declare global {
  function Allow(): GuardDecision;
  function Deny(reason: string): GuardDecision;
  function AskHuman(title: string, options?: string[]): GuardDecision;
  function defineTemplate<TState = Record<string, any>, TInputs = Record<string, any>>(
    def: NeigeTemplateDefinition<TState, TInputs>
  ): NeigeTemplateDefinition<TState, TInputs>;
}
```

---

### 2.4 `namespace Neige.AgentKit`（三条铁律解毒多 Harness 兼容库）

**三条工程铁律（写死在 `Neige.AgentKit` 实现中）**：
1. **严禁调用任何 CLI 原生 `/compact` 命令或私有 compaction RPC**：上下文压缩由 `Neige.Kernel` 通过 Epoch 换页完成。
2. **严禁拼接 CLI 私有路径/权限 DSL 字符串**（如 Claude `Edit(//path/**)` 或 Codex `shell_environment_policy`）：统一外置给 `OsSandboxWrapper`（Linux 使用 `/usr/bin/bwrap`，macOS 使用 `sandbox-exec`）隔离工作目录边界，保留 CLI 自身完整性。一期在当前宿主机 `bwrap 0.8.0` 下直接绑定传入的 `Cwd`（即解析后的 `AttemptWorkspaceLease.EffectiveCwd`），同时通过 `OverlayMounts` 字段预留升级到 `bwrap >= 0.9` 时按进程直注 `--overlay-src / --overlay` 层的能力。
3. **锁死最小 4 原语表面**：Antigravity / OpenCode 原生走 ACP JSON-RPC 2.0（`initialize` -> `session/new` / `session/load` -> `session/prompt` -> `session/cancel` -> `session/request_permission`）；Claude 走 `--print --verbose --input-format stream-json --output-format stream-json --mcp-config <tempJson>`（或 `claude-agent-acp`）；Codex 走 `codex app-server` JSON-RPC 2.0（`thread/start` / `thread/resume` / `turn/start` / `turn/steer` / `turn/interrupt`）。

```csharp
using System.Text.Json;

namespace Neige.AgentKit;

public enum OsSandboxMode
{
    None,
    WorkspaceWriteBwrap,
    ReadOnlyBwrap
}

public abstract record McpServerConfig
{
    public sealed record HttpMcpServer(
        string Url,
        IReadOnlyDictionary<string, string> Headers) : McpServerConfig;

    public sealed record StdioMcpServer(
        string Command,
        IReadOnlyList<string> Args,
        IReadOnlyDictionary<string, string> Env) : McpServerConfig;
}

public sealed record OverlayMountSpec(
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string TargetPath);

public sealed record HarnessLaunchSpec(
    string Provider,   // "codex" | "claude" | "acp:antigravity" | "acp:opencode"
    string? Model,
    string Cwd,
    string? ResumeNativeThreadId,
    string? SystemPrompt,
    IReadOnlyDictionary<string, McpServerConfig> McpServers,
    OsSandboxMode Sandbox = OsSandboxMode.None,
    IReadOnlyList<OverlayMountSpec>? OverlayMounts = null);

public sealed record TurnInput(
    string Prompt,
    string CorrelationId);

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long? ContextWindowTokens);

public abstract record UnifiedAgentEvent
{
    public sealed record ThreadBound(string NativeThreadId) : UnifiedAgentEvent;
    public sealed record TextDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ThinkingDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ToolCallStarted(string CallId, string Name, JsonElement Args) : UnifiedAgentEvent;
    public sealed record ToolCallCompleted(string CallId, string Output, bool IsError) : UnifiedAgentEvent;
    public sealed record ApprovalRequested(string RequestId, string ToolName, JsonElement Args) : UnifiedAgentEvent;
    public sealed record TurnCompleted(TokenUsage? Usage) : UnifiedAgentEvent;
    public sealed record TurnError(string Code, string Message, bool IsRetryable) : UnifiedAgentEvent;
}

public sealed record ProviderStatus(
    string Provider,
    bool Available,
    string? Version,
    string? Reason,
    IReadOnlyList<string> Models);

public interface IAgentSession : IAsyncDisposable
{
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }

    IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(TurnInput input, CancellationToken ct);
    Task CancelTurnAsync(CancellationToken ct);
    Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default);
    Task<bool> TrySteerAsync(string message, CancellationToken ct = default);
}

public interface IAgentRuntime : IAsyncDisposable
{
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct);
}
```

---

### 2.5 `namespace Neige.VcsKit`（原生 Jujutsu `jj` + `git`/`gh` + 可插拔 `IWorkspaceDriver` 挂载扩展缝）

- **存储隔离与版本语义彻底解耦**：
  - `IWorkspaceDriver` 负责**物理工作区制备、挂载恢复、Per-Attempt Copy-on-Write 租约与安全卸载**（决定进程看到什么目录树及能否并行写）；
  - `IVcsService` 负责**代码版本历史语义**（`jj` / `git` 的快照、ChangeId、Rebase 与 Push）。
  - **一期实现边界**：Stage 1 仅实现极薄的 `DirectWorkspaceDriver`（`"direct"`：原目录直通）与 `VcsWorkspaceDriver`（`"vcs-workspace"`：委托 `IVcsService.CreateWorkspaceAsync` 创建隔离 `jj workspace` 或 `git worktree`）。未来接入 `"fuse-overlayfs"`、`"kernel-overlayfs"` 或 `"bwrap-overlay"` 仅需向 `IWorkspaceDriverRegistry` 注册新驱动，**无需修改 `Kernel`、`SQLite` 表结构、`AgentKit` 或前端任何契约**：
    - `EnsureMountedAsync` 解决宿主机或 Daemon 重启后 Overlay 挂载点丢失的重挂问题（一期驱动为零开销透传）；
    - `TeardownTrackAsync` 先卸载 `MergedCwd` 再清理 `UpperDir` / `WorkDir`，确保挂载时作为只读 `lowerdir` 共享的全局依赖缓存（如 `target/`、`node_modules/`）绝不会被误删；
    - `AcquireAttemptLeaseAsync` / `SettleAttemptLeaseAsync` 预留了 Per-Attempt 临时 `upperdir`（`Promote` 晋升 / `Discard` 秒级丢弃并解锁 `SupportsParallelIsolatedWriteLeases` 并行写能力）。
- **VCS 探测逻辑 (`DetectAsync`)**：检查 `jj` CLI 是否存在及目录是否包含 `.jj`；若含 `.git` 但未初始化 `.jj` 且用户开启自动共存模式，支持调用 `jj git init --colocate`。若无 `jj` 则降级为 `GitWorktree`；非 VCS 目录返回 `PlainDirectory`。
- **`jj` 核心语义优势**：
  - `SnapshotWorkingCopyAsync` 在 `JjColocated` 下不执行 `git add/commit`，直接运行 `jj log -r @ --no-graph -T ...` 与 `jj op log -n 1` 获取稳定 `ChangeId`、`CommitId`、`OpId` 与冲突标志。
  - `RebaseOntoUpstreamAsync` 运行 `jj git fetch && jj rebase -s <root> -d main@origin`——`jj` 将冲突作为一等树状态提交并退出码为 0，绝不会像 `git rebase` 一样卡死工作区在半途 `REBASING` 状态。

```csharp
using System.Text.Json;

namespace Neige.VcsKit;

// -------- 1. 工作区存储/挂载与 CoW 租约抽象（预留 OverlayFS，一期仅实现 direct / vcs-workspace） --------

public enum AttemptLeaseDisposition
{
    Promote,
    Discard
}

public sealed record WorkspaceOverlayLayer(
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string TargetPath);

public sealed record WorkspaceProvisionRequest(
    string TrackId,
    string SourcePath,
    string TargetRootDir,
    IReadOnlyList<string> LowerCacheDirs,
    string? BaseRevision = null);

public sealed record WorkspaceSpec(
    string DriverId,
    string LogicalRoot,
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string MergedCwd,
    JsonElement Metadata);

public sealed record MountedWorkspace(
    string DriverId,
    string MergedCwd,
    IReadOnlyList<WorkspaceOverlayLayer>? ProcessOverlayLayers = null);

public sealed record AttemptWorkspaceLease(
    string DriverId,
    string AttemptId,
    string EffectiveCwd,
    bool IsIsolatedCopyOnWrite,
    JsonElement Metadata,
    IReadOnlyList<WorkspaceOverlayLayer>? ProcessOverlayLayers = null);

public interface IWorkspaceDriver
{
    string DriverId { get; }
    bool SupportsParallelIsolatedWriteLeases { get; }

    Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default);
    Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default);
    Task<AttemptWorkspaceLease> AcquireAttemptLeaseAsync(
        MountedWorkspace workspace,
        string attemptId,
        bool readWrite,
        CancellationToken ct = default);
    Task SettleAttemptLeaseAsync(
        AttemptWorkspaceLease lease,
        AttemptLeaseDisposition disposition,
        CancellationToken ct = default);
    Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default);
}

public interface IWorkspaceDriverRegistry
{
    IWorkspaceDriver GetRequired(string driverId);
    IReadOnlyList<string> RegisteredDrivers { get; }
}

// -------- 2. 版本控制与 Forge 抽象（JJ 一等支持 + Git 兜底 + gh CLI） --------

public enum VcsBackendKind
{
    JjColocated,
    GitWorktree,
    PlainDirectory
}

public sealed record IsolatedWorkspace(
    VcsBackendKind Kind,
    string RootRepoPath,
    string WorkspaceName,
    string WorkspacePath,
    string? BaseRevision);

public sealed record RevisionSnapshot(
    VcsBackendKind Kind,
    string ChangeIdOrBranch,
    string CommitId,
    string? OpId,
    bool HasConflicts,
    IReadOnlyList<string> ChangedFiles);

public sealed record DiffSummary(
    string DiffText,
    bool Truncated,
    IReadOnlyList<string> ChangedFiles);

public sealed record PushResult(
    string BookmarkOrBranch,
    string RemoteName,
    string HeadCommitId);

public interface IVcsService
{
    Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct);
    Task<IsolatedWorkspace> CreateWorkspaceAsync(string repoPath, string workspaceName, string targetDir, string? baseRev = null, CancellationToken ct = default);
    Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default);
    Task<RevisionSnapshot> SnapshotWorkingCopyAsync(string workspacePath, string? commitMessageIfGit = null, CancellationToken ct = default);
    Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default);
    Task<RevisionSnapshot> RebaseOntoUpstreamAsync(string workspacePath, string upstreamBranch = "main", CancellationToken ct = default);
    Task<DiffSummary> DiffAsync(string workspacePath, string? fromRev = null, string? toRev = null, CancellationToken ct = default);
    Task<PushResult> PushBookmarkAsync(string workspacePath, string bookmarkOrBranch, string? targetRev = null, CancellationToken ct = default);
}

public sealed record PrPublishRequest(
    string WorkspacePath,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string Body);

public sealed record PrInfo(int Number, string Url, string State, string HeadSha);
public sealed record CheckRunSummary(string Name, string Status, string? Conclusion, string? DetailsUrl);
public sealed record PrChecksStatus(int PrNumber, string HeadSha, bool AllCompleted, bool AllGreen, IReadOnlyList<CheckRunSummary> Checks);
public sealed record CiFailureEvidence(int PrNumber, string HeadSha, string SummaryText, string LogTail);
public sealed record IssueInfo(int Number, string Url, string State, string Title, string Body, IReadOnlyList<string> Labels);

public interface IGitHubForgeService
{
    Task<PrInfo> PublishPrAsync(PrPublishRequest req, CancellationToken ct);
    Task<PrChecksStatus> GetPrChecksAsync(string repoOrPath, int prNumber, CancellationToken ct);
    Task<CiFailureEvidence> GetCiFailureEvidenceAsync(string repoOrPath, int prNumber, string headSha, CancellationToken ct);
    Task MergePrAsync(string repoOrPath, int prNumber, string expectedHeadSha, CancellationToken ct);
    Task<IssueInfo> ViewIssueAsync(string repoOrPath, int issueNumber, CancellationToken ct);
}
```

---

### 2.6 `tools/neige-term-bridge` (Rust) 与 `namespace Neige.TermClient` (C#)

#### 提纯自已迁移 Rust 终端种子（`tools/rust-terminal/`）的精确模块来源
从本仓库已迁移的 `tools/rust-terminal/` 中抽取并剥离业务依赖：
1. **`tools/rust-terminal/calm-terminal-view/src/lib.rs`**：原样保留 `TerminalView`、`Frame`、`Cursor`、`Cell`、`InputSurface`（含 `bracketed_paste()` 感知 `MODE_BRACKETPASTE`）、`key_bytes()`（感知 `MODE_KCURSOR`）及 `take_replies()` 防重复应答机制，基于 `rmux-core = "=0.10.0"` 与 `rmux-proto = "=0.10.0"`。
2. **`tools/rust-terminal/calm-terminal-runtime/src/lib.rs`**：保留 `rmux-server` / `rmux-sdk` 会话生命周期管理（确保主进程重启不杀终端）；将 `isolated_command` 中写死报错的 Linux `unshare` 改造为“Linux 下按开关可选，macOS 走普通子进程”，并抽取 `PtyBackend` trait 以便 Windows 使用 `portable-pty` ConPTY 向同一个 `TerminalView` 喂字节。
3. **`tools/rust-terminal/terminal_interaction_ref/`**：提取 `screen_diff.rs`（`CursorSnapshot`、`ScreenDiff`、`row_hashes`）、`actions.rs`（`ACTION_BYTES_MAX = 16384`、`SEQUENCE_KEYS`、`Encoded::{Bytes, Submit}`）、`wait_plan.rs`（`WaitFor::{Elapsed, Change, Signal, Text}`、`WAIT_MS_MAX = 20_000`、`SETTLE_MS_DEFAULT = 150`），彻底剔除 `RouteRepo`、`CardRole` 与 `fixtures` 钩子。

#### Rust `neige-term-bridge` 对外协议（JSON-RPC 2.0 + 二进制 WS）
监听 `--listen-uds <path>` 或 `--listen-tcp 127.0.0.1:<port>`：
- **`term.ensure`**：
  - 请求：`{ "terminal_id": "t_1", "program": "/bin/bash", "args": [], "cwd": "/repo", "env": {}, "cols": 120, "rows": 40 }`
  - 响应：`{ "terminal_id": "t_1", "pid": 12345, "created": true }`
- **`term.observe`**：
  - 请求：`{ "terminal_id": "t_1", "scroll_offset": 0, "wait_for": "none"|"elapsed"|"change"|"text", "wait_ms": 2000, "settle_ms": 150, "wait_text": [], "wait_text_absent": [] }`
  - 响应：`{ "revision": 42, "frame": { "cols": 120, "rows": 40, "cursor": { "column": 0, "row": 5, "visible": true }, "alternate": false, "scroll_offset": 0, "history_rows": 12, "modes": 0, "text": ["..."] }, "exited": false, "exit_code": null }`
- **`term.action`**：
  - 请求：`{ "terminal_id": "t_1", "kind": "text"|"keys"|"paste"|"submit", "payload": { ... } }`
  - 响应：`{ "ack_revision": 43 }`（内部根据 `InputSurface` 的 `bracketed_paste` 与 `alternate` 自动编码）。
- **`term.resize`**：
  - 请求：`{ "terminal_id": "t_1", "cols": 140, "rows": 45 }` -> `{ "ok": true }`
- **`term.kill`**：
  - 请求：`{ "terminal_id": "t_1", "grace_ms": 2000 }` -> `{ "exited": true, "exit_code": 0 }`
- **WebSocket `/ws/{terminal_id}`**（供 `Neige.Host` 透传给前端 `xterm.js`）：
  - 连接建立时立刻推送最近 **64KB** 环形缓冲历史字节帧；
  - 双向二进制帧：`0x01 + <raw_pty_bytes>`（终端原始输入输出）；
  - 控制帧：`0x02 + <utf8_json>`（客户端发 `{"type":"resize","cols":120,"rows":40}`，服务端推 `{"type":"exit","exit_code":0}`）。

#### C# 契约 `namespace Neige.TermClient`

```csharp
using System.Text.Json;

namespace Neige.TermClient;

public enum TermWaitMode { None, Elapsed, Change, Text }
public enum TermActionKind { Text, Keys, Paste, Submit }

public sealed record TermEnsureRequest(
    string TerminalId,
    string Program,
    IReadOnlyList<string> Args,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    ushort Cols = 120,
    ushort Rows = 40);

public sealed record TermEnsureResult(string TerminalId, int Pid, bool Created);

public sealed record TerminalCursor(uint Column, uint Row, bool Visible);

public sealed record TerminalFrame(
    ushort Cols,
    ushort Rows,
    TerminalCursor Cursor,
    bool Alternate,
    int ScrollOffset,
    int HistoryRows,
    uint Modes,
    IReadOnlyList<string> Text);

public sealed record TermObserveRequest(
    string TerminalId,
    int ScrollOffset = 0,
    TermWaitMode WaitFor = TermWaitMode.None,
    int WaitMs = 0,
    int SettleMs = 150,
    IReadOnlyList<string>? WaitText = null,
    IReadOnlyList<string>? WaitTextAbsent = null);

public sealed record TermObserveResult(
    ulong Revision,
    TerminalFrame Frame,
    bool Exited,
    int? ExitCode);

public sealed record TermActionRequest(
    string TerminalId,
    TermActionKind Kind,
    JsonElement Payload);

public sealed record TermActionResult(ulong AckRevision);

public interface ITerminalBridgeClient : IAsyncDisposable
{
    Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct);
    Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct);
    Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct);
    Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct);
    Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default);
    Uri GetWebSocketRelayUri(string terminalId);
}
```

---

### 2.7 `namespace Neige.Kernel`（微内核编排、`TrackActor`、Epoch 换页与 `ITrackStore`）

#### 核心机制设计
1. **`TrackActor` 单写者模型与按需挂载激活**：
   - `TrackActorRegistry` 维护 `ConcurrentDictionary<string, TrackActor>`；每个活跃 Track 拥有独立无锁 `Channel<Func<TrackActor, Task>>`，确保该 Track 内的会话输入、Report 提交、Task 声明/状态推进、Worker 完成回调、Ask 回答、Mail 投递与 Track 删除严格顺序执行。
   - **激活重挂（Hydration Mount Hook）**：`TrackActor` 从 SQLite 唤醒水合时，在启动任何 `AgentSession`、`Terminal` 或 `TaskAttempt` 前必先调用 `IWorkspaceDriverRegistry.GetRequired(track.WorkspaceDriver).EnsureMountedAsync(workspaceSpec)` 并将返回的 `MountedWorkspace`（含 `MergedCwd` 与可选 `ProcessOverlayLayers`）注入会话与终端启动参数；一期 `Direct` / `VcsWorkspace` 为空操作，未来接 `OverlayFS` / `fuse-overlayfs` 时可零修改恢复宿主机重启丢失的挂载点。
   - 当某 Track 无运行中 Turn/Task 且空闲超过 `10 分钟` 时自动从字典休眠卸载，保留 SQLite 快照。
2. **`PrimarySessionId` 代表路由**：
   - `AgentSession` 在 `Interactive` 模式下代码与工具权限完全对等（均可通过 `ReportDoc` 块级 Anchor 乐观锁安全协作）。
   - `Track.PrimarySessionId` 仅作为默认代表指针：接收外部 `Mail` 到达唤醒、后台 `Task` 完结/失败唤醒、用户在 UI 上提交的 `Ask` 回答与外部 `Report` 修改唤醒；可通过 `SetPrimarySessionAsync` 随时切换代表。
3. **宿主自管 Epoch 换页压缩（Host-Owned Epoch Handover Compaction）**：
   - 当 `ContextUsageRatio >= 0.80` 或用户触发 `CompactSessionAsync`：
     1. 向当前底层 `IAgentSession` 发送一条普通对话 Turn，要求输出结构化交接摘要（当前进展、关键路径、已验证结论、下一步）；
     2. 关闭旧底层 `IAgentSession` 实例；
     3. 在数据库更新该 `AgentSession` 的 `Epoch = Epoch + 1`、`NativeThreadId = null`、`ContextUsageRatio = 0.0`；
     4. 用「最新 Report 全文 + 交接摘要」作为系统种子打开全新底层 `IAgentSession` 并绑定新 `NativeThreadId`（前端 `SessionId` 与历史消息流水保持连贯不断）。
4. **`TaskGraphScheduler` 租约感知调度与 `afterWorkerDoneCmd`**：
   - `ComputeSchedulable(tasks, bool allowParallelWriteLeases = false)`：筛选 `ready == true`、所有 `depends_on` 任务处于 `Done`、非 `Draft`/`Running`/`Verifying`/`Done`/`Failed`/`Canceled` 的任务；当 `driver.SupportsParallelIsolatedWriteLeases == false`（一期默认）时，存在在途 `ReadWrite` 任务则返回空，存在在途 `ReadOnly` 任务仅放行 `ReadOnly`，无在途则按优先级与创建时间放行 `ReadOnly` 集合或单个最高优 `ReadWrite`；未来若挂载了支持独立 per-attempt CoW `upperdir` 的 `OverlayFS` 驱动（`SupportsParallelIsolatedWriteLeases == true`），调度器无需改动即可并发放行多个相互隔离的投机写 Attempt。
   - **Attempt 租约与统一钩子槽 `afterWorkerDoneCmd`**：派发 Attempt 前先向 `IWorkspaceDriver.AcquireAttemptLeaseAsync` 申请 `AttemptWorkspaceLease`（并将快照持久化至 `task_attempts.workspace_lease_json`）；Worker 会话、`afterWorkerDoneCmd` 与 `GateSpec` 门禁均在 `lease.EffectiveCwd`（以及透传映射到 `HarnessLaunchSpec.OverlayMounts` 的层配置）内执行。`neige_task_done` 后若 `CompiledTemplateManifest.AfterWorkerDoneCmd` 非空，在 `lease.EffectiveCwd` 下运行该命令并解析 `stdout` **最后一行非空合法 JSON** 并入 `task_attempts.meta_json` 后传给 `PolicyVm.OnTaskSettled`；最终门禁通过调 `SettleAttemptLeaseAsync(lease, AttemptLeaseDisposition.Promote)`，门禁失败/取消/Fail 调 `SettleAttemptLeaseAsync(lease, AttemptLeaseDisposition.Discard)`（秒级 O(1) 抛弃脏层）。
5. **启动收口 `BootReconciler`**：
   - 启动监听前扫描数据库 `status IN ('Running', 'Verifying')` 的 `task_attempts` 与 `session_turns`：统一标记为 `interrupted`；对仍持有非空 `workspace_lease_json` 的中断 Attempt 调用 `SettleAttemptLeaseAsync(lease, Discard)` 清理残留临时挂载与 CoW 层；释放内存占坑后向受影响的开放 Track 投递 `SystemWake` 通知 `PrimarySessionId` 恢复决策。

#### 冻结仓储端口 `ITrackStore` 与 `ITrackReadStore`

```csharp
using System.Text.Json;
using Neige.Contracts;
using Neige.ReportDoc;

namespace Neige.Kernel.Ports;

public sealed record TrackAggregateSnapshot(
    TrackDto Track,
    ReportDocument Report,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<AgentSessionDto> Sessions,
    IReadOnlyList<AskDto> OpenAsks,
    string? TemplateSnapshotSource);

public interface ITrackReadStore
{
    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct);
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct);
    Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct);
    Task<IReadOnlyList<TaskAttemptDto>> ListAttemptsForTaskAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default);
    Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct);
}

public interface ITrackStore : ITrackReadStore
{
    Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct);
    Task DeleteAreaAsync(string areaId, CancellationToken ct);

    Task CreateTrackAsync(TrackDto track, ReportDocument initialReport, string? templateSnapshot, AgentSessionDto initialPrimarySession, CancellationToken ct);
    Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct);
    Task DeleteTrackAsync(string trackId, CancellationToken ct);

    Task SaveReportAndTasksAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        IReadOnlyList<TaskDto> syncedTasks,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct);

    Task UpsertTaskAndAttemptAsync(TaskDto task, TaskAttemptDto? attempt, JsonElement? nextTemplateState, EventEnvelopeDto? evt, CancellationToken ct);
    Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct);
    Task RecordTurnAndItemsAsync(SessionTurnDto turn, IReadOnlyList<SessionItemDto> newItems, AgentSessionDto? updatedSession, CancellationToken ct);
    Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct);
    Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct);
    Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct);
    Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct);
    Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct);
}
```

---

## 3. 完整冻结 SQLite 10-表群 WAL Schema、升级规范与防膨胀 GC

> 全库收敛为以下 10 组清晰正交的表（含 `schema_migrations` 元表，废除老版 `operations`、`track_vcs_*`、`cards`、`harness_items` 等全部 70 余张冗余表），从建库第一天即开启 `INCREMENTAL auto_vacuum`、全量外键级联子表索引、大对象冷剥离与确定性滑窗回收。

```sql
-- 必须在首次建表前设置，使数据库支持逐页增量回收给 OS 而无需全库阻塞 VACUUM
PRAGMA auto_vacuum = INCREMENTAL;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store = MEMORY;

-- 0. 迁移账本（字节冻结内嵌脚本 + SHA-256 校验）
CREATE TABLE IF NOT EXISTS schema_migrations (
    version             INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    checksum_sha256     TEXT NOT NULL,
    applied_at_ms       INTEGER NOT NULL
);

-- 1. 领域分组与声明目录
CREATE TABLE IF NOT EXISTS areas (
    id                  TEXT PRIMARY KEY,
    name                TEXT NOT NULL,
    color               TEXT NOT NULL DEFAULT '#64748b',
    default_cwd         TEXT,
    folders_json        TEXT NOT NULL DEFAULT '[]',
    sort_order          REAL NOT NULL DEFAULT 0,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL
);

-- 2. Track 核心表（含代表指针、工作区挂载描述与模板快照/状态槽；零 Git 强耦合字段）
CREATE TABLE IF NOT EXISTS tracks (
    id                      TEXT PRIMARY KEY,
    area_id                 TEXT NOT NULL REFERENCES areas(id) ON DELETE CASCADE,
    parent_track_id         TEXT REFERENCES tracks(id) ON DELETE SET NULL,
    parent_attempt_id       TEXT,
    title                   TEXT NOT NULL DEFAULT '',
    cwd                     TEXT NOT NULL,
    workspace_driver        TEXT NOT NULL DEFAULT 'direct',
    workspace_spec_json     TEXT NOT NULL DEFAULT '{}',
    cleanup_cmd             TEXT,
    primary_session_id      TEXT,
    template_id             TEXT,
    template_snapshot       TEXT,
    template_inputs_json    TEXT NOT NULL DEFAULT '{}',
    template_state_json     TEXT NOT NULL DEFAULT '{}',
    enabled_plugins_json    TEXT NOT NULL DEFAULT '[]',
    closed_at_ms            INTEGER,
    pinned_at_ms            INTEGER,
    sort_order              REAL NOT NULL DEFAULT 0,
    created_at_ms           INTEGER NOT NULL,
    updated_at_ms           INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_tracks_area ON tracks(area_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_tracks_parent ON tracks(parent_track_id) WHERE parent_track_id IS NOT NULL;

-- 3. Report 当前文档态
CREATE TABLE IF NOT EXISTS reports (
    track_id            TEXT PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE,
    doc_rev             INTEGER NOT NULL DEFAULT 0,
    summary             TEXT NOT NULL DEFAULT '',
    leading_comment     TEXT NOT NULL DEFAULT '',
    blocks_json         TEXT NOT NULL DEFAULT '[]',
    updated_at_ms       INTEGER NOT NULL
);

-- 4. Report 历史 Zstd 压缩快照（替代原 5,000 行自研 track_vcs）
CREATE TABLE IF NOT EXISTS report_revisions (
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    doc_rev             INTEGER NOT NULL,
    author_session_id   TEXT,
    message             TEXT NOT NULL DEFAULT '',
    blocks_zstd         BLOB NOT NULL,
    created_at_ms       INTEGER NOT NULL,
    PRIMARY KEY (track_id, doc_rev)
);

-- 5. 任务声明与调度唯一真相表
CREATE TABLE IF NOT EXISTS tasks (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    block_id            TEXT NOT NULL,
    task_key            TEXT NOT NULL,
    generation          INTEGER NOT NULL DEFAULT 1,
    kind                TEXT NOT NULL,
    provider            TEXT,
    access_mode         TEXT NOT NULL DEFAULT 'read_write',
    ready               INTEGER NOT NULL DEFAULT 0,
    depends_on_json     TEXT NOT NULL DEFAULT '[]',
    goal_or_cmd         TEXT NOT NULL,
    context_json        TEXT,
    acceptance          TEXT,
    gate_json           TEXT,
    status              TEXT NOT NULL DEFAULT 'pending',
    status_detail       TEXT,
    semantic_verdict    TEXT NOT NULL DEFAULT 'none',
    semantic_note       TEXT,
    latest_attempt_id   TEXT,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL,
    UNIQUE (track_id, task_key)
);
CREATE INDEX IF NOT EXISTS idx_tasks_track_status ON tasks(track_id, status);

-- 6. 任务执行 Attempt 轨迹（含 afterWorkerDoneCmd 采集的通用 meta_json 与 Per-Attempt 租约快照）
CREATE TABLE IF NOT EXISTS task_attempts (
    attempt_id           TEXT PRIMARY KEY,
    task_id              TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    track_id             TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    session_id           TEXT,
    status               TEXT NOT NULL,
    worker_result_json   TEXT,
    meta_json            TEXT NOT NULL DEFAULT '{}',
    workspace_lease_json TEXT,
    gate_verdict_json    TEXT,
    started_at_ms        INTEGER NOT NULL,
    finished_at_ms       INTEGER
);
CREATE INDEX IF NOT EXISTS idx_attempts_task ON task_attempts(task_id, started_at_ms);
CREATE INDEX IF NOT EXISTS idx_attempts_track ON task_attempts(track_id);

-- 7. 大一统会话表（Planner / Assistant / Task Worker 完全统一）
CREATE TABLE IF NOT EXISTS agent_sessions (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    kind                TEXT NOT NULL,        -- 'interactive' | 'task_attempt'
    task_id             TEXT,
    title               TEXT,
    provider            TEXT NOT NULL,
    model               TEXT,
    native_thread_id    TEXT,
    epoch               INTEGER NOT NULL DEFAULT 1,
    status              TEXT NOT NULL DEFAULT 'idle',
    context_usage_ratio REAL NOT NULL DEFAULT 0,
    archived_at_ms      INTEGER,
    created_at_ms       INTEGER NOT NULL,
    updated_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_track ON agent_sessions(track_id);

-- 8A. 会话轮次（含 Idempotency-Key 去重与 Epoch 归属索引）
CREATE TABLE IF NOT EXISTS session_turns (
    id                  TEXT PRIMARY KEY,
    session_id          TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_index          INTEGER NOT NULL,
    epoch               INTEGER NOT NULL DEFAULT 1,
    idempotency_key     TEXT,
    trigger_kind        TEXT NOT NULL,        -- 'user' | 'wake' | 'system'
    input_json          TEXT NOT NULL,
    status              TEXT NOT NULL,        -- 'running' | 'completed' | 'failed' | 'interrupted'
    usage_json          TEXT,
    error_text          TEXT,
    started_at_ms       INTEGER NOT NULL,
    completed_at_ms     INTEGER,
    UNIQUE (session_id, idempotency_key)
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_turns_session_idx ON session_turns(session_id, turn_index);
CREATE INDEX IF NOT EXISTS idx_turns_session_epoch ON session_turns(session_id, epoch);

-- 8B. 会话完整条目（流式 Delta 走内存 WS，仅完成块落盘；>64KB 大输出剥离外部冷存储）
CREATE TABLE IF NOT EXISTS session_items (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id          TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_id             TEXT NOT NULL REFERENCES session_turns(id) ON DELETE CASCADE,
    kind                TEXT NOT NULL,        -- 'user' | 'thinking' | 'assistant_text' | 'tool_call' | 'tool_result'
    payload_json        TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_items_session ON session_items(session_id, id);
CREATE INDEX IF NOT EXISTS idx_items_turn ON session_items(turn_id);

-- 9A. 用户审批问答
CREATE TABLE IF NOT EXISTS asks (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    status              TEXT NOT NULL DEFAULT 'open',
    questions_json      TEXT NOT NULL,
    answer_json         TEXT,
    created_at_ms       INTEGER NOT NULL,
    answered_at_ms      INTEGER
);
CREATE INDEX IF NOT EXISTS idx_asks_track ON asks(track_id, status);

-- 9B. 跨 Track 邮件（投递后自动唤醒目标 Track 的 primary_session_id）
CREATE TABLE IF NOT EXISTS mails (
    id                  TEXT PRIMARY KEY,
    from_track_id       TEXT NOT NULL,
    to_track_id         TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    summary             TEXT NOT NULL,
    body                TEXT NOT NULL,
    read_at_ms          INTEGER,
    created_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_mails_to_track ON mails(to_track_id, created_at_ms DESC);

-- 10A. 终端元数据
CREATE TABLE IF NOT EXISTS terminals (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
    attempt_id          TEXT,
    idempotency_key     TEXT,
    title               TEXT NOT NULL DEFAULT 'Terminal',
    program             TEXT,
    cwd                 TEXT NOT NULL,
    status              TEXT NOT NULL,
    exit_code           INTEGER,
    created_at_ms       INTEGER NOT NULL,
    exited_at_ms        INTEGER
);
CREATE INDEX IF NOT EXISTS idx_terminals_track ON terminals(track_id);

-- 10B. 增量事件流水（供 WS ?since= 断线重放，受滑窗上限约束）
CREATE TABLE IF NOT EXISTS events (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    area_id             TEXT,
    track_id            TEXT,
    kind                TEXT NOT NULL,
    payload_json        TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_events_track_id ON events(track_id, id);
CREATE INDEX IF NOT EXISTS idx_events_created ON events(created_at_ms);
```

### 3.1 `SqliteSingleWriter` 单写协程与平滑升级契约（`SqliteMigrator`）
- **`SqliteSingleWriter` 单写通道**：
  - 启动时打开**唯一**写连接 `_writeConnection`（`Mode=ReadWriteCreate;Pooling=False`）并配置 `PRAGMA journal_mode=WAL; synchronous=NORMAL; foreign_keys=ON;`。
  - 拥有有界通道 `Channel<WriteBatchItem>`（容量 `4096`），每批最多提取 `32` 个写入闭包在单次 `BEGIN IMMEDIATE ... COMMIT` 中提交，失败整体 `ROLLBACK`；所有 `ITrackReadStore` 查询走独立 `Mode=ReadOnly` 连接池并发无锁快照读。
- **平滑可逆升级规则（不再重蹈老版 221 个迁移失控覆辙）**：
  1. **字节冻结迁移 + 迁移前在线备份**：所有迁移脚本以内嵌资源 `Migrations/NNNN_*.sql` 存放；`SqliteMigrator` 启动时先核对已应用脚本的 `checksum_sha256`，发现待升级脚本前通过 SQLite Online Backup API 自动生成 `{dbPath}.bak-v{current}`，再在事务中顺序执行增量迁移。
  2. **热关系列最小化 + JSON `"v": 1` 读取期 Upcaster**：仅对用于外键、索引或调度的字段建模为物理列；易演进的扩展属性（`workspace_spec_json`、`template_state_json`、`meta_json`、`gate_verdict_json`、`payload_json`）在应用层统一携带 `"v": 1` 并通过 C# 读取期 Upcaster 兼容旧版缺省字段，**日常新增可选字段 0 `ALTER TABLE` 成本**。
  3. **默认只增不毁与外键级联索引强制**：普通版本升级优先采用纯增列（`ADD COLUMN ... DEFAULT`）或新增索引；若必须重建表，必须遵循 `PRAGMA foreign_keys = OFF` -> 事务重建 -> `PRAGMA foreign_key_check`；**每张引用父表带 `ON DELETE CASCADE` 的子表均已强制配备子表外键索引**，防止删除 Track/Turn 时触发全表扫描锁死写协程。

### 3.2 防无序膨胀与确定性垃圾回收（`SqliteMaintenance`）
`Neige.Host` 的后台定时维护服务（默认每 `6 小时` 或空闲时，亦可手动触发）调用 `ITrackStore.RunMaintenancePassAsync(nowMs, ct)`，按以下 5 条确定性规则控制数据库物理体积与 B-Tree 碎片：

1. **64 KB 大对象外部冷剥离（拒绝多 MB 日志写进 SQLite B-Tree）**：
   - `RecordTurnAndItemsAsync` 与 `UpsertTaskAndAttemptAsync` 在写入前检测：`session_items.payload_json`（如超大 `tool_result`）或 `task_attempts.gate_verdict_json` 超过 **64 KB** 时，自动将其 Zstd 压缩写入 `{dataDir}/blobs/{trackId}/{sha256}.zst`，库内只保留 `{ "v": 1, "blobRef": "blobs/...", "bytes": ..., "preview": "前 2KB..." }`；删除 Track 时 O(1) 同步删除目录 `{dataDir}/blobs/{trackId}`。
2. **`events` 滑窗环形裁剪**：
   - 保留窗口上限：**最近 `7 天` 且最多 `50,000` 条**；超出部分按每批 `1,000` 行渐进 `DELETE`（不长事务堵读写）；更早断线的 WS 客户端由 `/api/v1/events` 契约直接返回 `_snapshot_required` 刷新首屏即可。
3. **`report_revisions` 里程碑抽稀**：
   - 活跃 Track 默认完整保留**最近 `100` 个修订版**；更早历史（`doc_rev <= latest - 100`）或已关闭超过 `14 天` 的 Track，自动抽稀为仅保留 `doc_rev % 10 == 0` 的里程碑版本 + 初始版本与最新版本。
4. **已换页旧 Epoch 与终态 Worker 条目瘦身**：
   - 对已经历过宿主换页的会话（`session_turns.epoch < agent_sessions.epoch - 1`）以及已完结超过 `14 天` 的 `task_attempt` 会话：自动删除历史 `kind = 'thinking'` 过程块，并将超 `4 KB` 的 `tool_result` 替换为精简墓碑（保留完整 `user`、`assistant_text` 与工具调用索引），同时清理对应的冷 Blob 引用。
5. **物理文件真实缩容（`INCREMENTAL auto_vacuum`）**：
   - 每次 GC 批次结束后，仅当空闲页达阈值时执行 `PRAGMA incremental_vacuum(500);`（将释放页面真实截断归还给操作系统）并执行 `PRAGMA wal_checkpoint(TRUNCATE);`，确保 `.db` 与 `.db-wal` 长期运行也不会无限单调膨胀。

---

## 4. `/api/v1` REST + WebSocket + `/mcp/*` 线协议规范

### 4.1 22 个精简 REST 端点

| 分组 | 方法与路径 | 请求 / 响应契约概要 |
| :--- | :--- | :--- |
| 系统 | `GET /api/v1/bootstrap` | 一次返回 `{ version, providers: ProviderStatus[], templates: CompiledTemplateManifest[], areas: AreaDto[] }` |
| 系统 | `GET /api/v1/templates` | 返回所有可用 `.neige.js` 模板及其 `inputs` JSON Schema |
| Area | `GET /api/v1/areas` | 返回 `AreaDto[]` |
| Area | `POST /api/v1/areas` | `{ name, color?, defaultCwd?, folders? }` → `201 AreaDto` |
| Area | `PATCH /api/v1/areas/{areaId}` | 部分更新名称/颜色/目录绑定 → `200 AreaDto` |
| Area | `DELETE /api/v1/areas/{areaId}` | 级联删除 Area 及下属 Track → `204` |
| Track | `GET /api/v1/areas/{areaId}/tracks` | 查询 `?includeClosed=true\|false` → `TrackDto[]` |
| Track | `POST /api/v1/tracks` | `{ areaId, title?, cwd?, workspaceDriver?, templateId?, templateInputs?, provider?, model?, firstMessage? }` → `201 TrackAggregateSnapshot` |
| Track | `GET /api/v1/tracks/{trackId}` | 返回完整 `TrackAggregateSnapshot` |
| Track | `PATCH /api/v1/tracks/{trackId}` | `{ title?, closed?, pinned?, enabledPlugins?, primarySessionId? }` → `200 TrackDto` |
| Track | `DELETE /api/v1/tracks/{trackId}` | 停止 Actor 与会话、调用模板 `onCloseCmd`、`TeardownTrackAsync` 安全卸载工作区并删除行 → `204` |
| 文件 | `GET /api/v1/tracks/{trackId}/files` | `?path=` 目录列表或读取工作区安全文本文件 |
| Diff | `GET /api/v1/tracks/{trackId}/vcs-diff` | 调用 `IVcsService.DiffAsync` 返回变更文件列表与 Diff 文本 |
| 报告 | `GET /api/v1/tracks/{trackId}/report` | 返回 `{ docRev, summary, blocks, tasks, diagnostics }` |
| 报告 | `PUT /api/v1/tracks/{trackId}/report/blocks/{blockId}` | 人类直接编辑报告块 `{ docRev, knownBlockRev, markdown?, payload? }`；更新后唤醒代表会话 |
| 任务 | `GET /api/v1/tracks/{trackId}/tasks/{taskKey}/attempts` | 返回该 Task 的历史 `TaskAttemptDto[]`（含 `meta` 与 Gate 日志） |
| 会话 | `GET /api/v1/tracks/{trackId}/sessions` | 统一返回该 Track 下所有 `AgentSessionDto`（含 `Interactive` 与 `TaskAttempt`） |
| 会话 | `POST /api/v1/tracks/{trackId}/sessions` | 新开交互式会话 `{ title?, provider, model? }` → `201 AgentSessionDto` |
| 会话 | `GET /api/v1/sessions/{sessionId}/items` | `?afterId=0&limit=100` 分页读取 `SessionItemDto[]` 与当前在途 Turn 状态 |
| 会话 | `POST /api/v1/sessions/{sessionId}/messages` | 必填 Header `Idempotency-Key`；Body `{ content, steerIfRunning? }` → `200 SessionTurnDto` |
| 会话 | `POST /api/v1/sessions/{sessionId}/interrupt` | 中断当前在途 Turn → `200` |
| 会话 | `POST /api/v1/sessions/{sessionId}/compact` | 触发宿主自管 Epoch 换页压缩 → `200 AgentSessionDto` |
| 决策/终端 | `POST /api/v1/tracks/{trackId}/asks/{askId}/answer` | 提交 `{ answers: [...] }` 回答并唤醒 `PrimarySessionId` |
| 决策/终端 | `GET / POST / DELETE /api/v1/tracks/{trackId}/terminals/{termId?}` | 列出/打开/关闭终端实例 |

*(说明：共 22 个逻辑 REST 操作路径，`GET/POST/DELETE terminals` 计为 1 组终端资源端点，加上 `bootstrap`/`templates` 共 24 条路由。)*

### 4.2 WebSocket 协议

1. **`WS /api/v1/events?since={lastEventId}&trackId={optional}`**
   - 服务端先从 `EventHub` 订阅实时广播，再从 `events` 表重放 `id > since` 的持久化事件（超过 2,000 条发送 `{"kind":"_snapshot_required"}`，否则重放完毕发送 `{"kind":"_replay_complete","tipId":...}`）。
   - **持久事件**（`eventId != null`）：`track.updated`, `report.updated`, `task.updated`, `session.updated`, `turn.completed`, `ask.changed`, `mail.received`, `terminal.changed`。
   - **瞬态流式帧**（`eventId == null`，绝不写 SQLite）：`session.text_delta`, `session.thinking_delta`, `session.tool_progress`。
2. **`WS /api/v1/terminals/{termId}/ws`**
   - 透传对接 `neige-term-bridge` 的 `/ws/{termId}` 二进制帧（`0x01` 终端字节 / `0x02` 控制 JSON）。

### 4.3 多源 MCP 路由体系（内核 + 内置 JJ Forge + 静态外挂插件）

会话启动前由 `Neige.Host` 为每个 `AgentSession` 生成唯一短期内存 Token `ng_<guid>` 映射 `(TrackId, SessionId, SessionKind, AttemptId?)` 并通过 `HarnessLaunchSpec.McpServers` 注入多路 URL：

1. **`/mcp/kernel`（服务名 `"neige"`，官方 `ModelContextProtocol.AspNetCore`）**：
   - `Interactive` 会话（Planner 与 Assistant 完全平权）：`neige_report_read`, `neige_report_commit`, `neige_task_ls`, `neige_task_cancel`, `neige_task_accept`, `neige_task_reject`, `neige_user_ask`, `neige_mail_send`, `neige_mail_ls`, `neige_mail_cat`, `neige_track_status`, `neige_track_rename`, `neige_track_close`, `neige_terminal_open`, `neige_terminal_read`, `neige_terminal_input`。
   - `TaskAttempt` 会话：`neige_report_read`, `neige_task_ls`, `neige_task_done`, `neige_task_fail`。
2. **`/mcp/vcs`（服务名 `"vcs"`，基于 `libs/Neige.VcsKit`）**：
   - 暴露：`vcs_status`, `vcs_diff`, `vcs_restore_op` (JJ 时光机), `vcs_rebase_upstream`, `vcs_publish_pr`, `vcs_pr_checks`, `vcs_ci_evidence`, `vcs_pr_merge`, `vcs_issue_view`。
3. **`/mcp/p/{pluginId}`（静态配置文件 `mcp-plugins.json` 挂载外部标准 MCP）**：
   - 支持配置 `"transport": "http" | "stdio"`，例如直接挂载智堡 Wisburg MCP 或原仓库 `market` 二进制。
   - **全局安全拦截切面**：所有 `/mcp/kernel`、`/mcp/vcs` 与 `/mcp/p/{pluginId}` 的 `tools/call` 请求在执行前统一先走 `TrackActor` 的 `PolicyVm.GuardToolCall`：
     - `Allow` → 立即放行执行；
     - `Deny(reason)` → 立刻返回 MCP `isError: true` 给 Agent；
     - `AskHuman(title, options)` → 自动插入 `asks` 表、推 WS 通知用户并挂起当前 MCP HTTP 请求（或返还挂起等待用户点击后自动放行执行）！

---

## 5. 宿主机工具链状态与 Swarm 分阶段实施路线

### 5.1 已探测宿主机工具链现状
- **已就绪**：`rustc` / `cargo` (`/home/kenji/.cargo/bin`), `git` (`/usr/bin/git`), `gh` (`/usr/bin/gh`), `node` / `npm` (`v22.22.2`), `bwrap` (`/usr/bin/bwrap`)。
- **待 Stage 0 用户态安装（无需 root）**：
  - `.NET 9 SDK` → 安装至 `~/.dotnet`（使用官方 `dotnet-install.sh --channel 9.0`）；
  - `jj` (Jujutsu) → 下载官方预编译静态二进制或 `cargo install jj-cli` 至 `~/.local/bin/jj`。

### 5.2 三阶段 Swarm 并行实施编排（Compact 后下一回合直接启动）

1. **Stage 0（Scaffold · 单作者奠基）**：
   - 安装 `dotnet` 9 SDK 与 `jj` 到用户目录并验证版本。
   - 在 `/home/kenji/neige-workspaces/78d0fc64f6394212b88223f52273e9dd/0356c8755af142cab9f7919f5e82ce20` 落地 `Directory.Build.props`、`Neige.Next.sln`、全部 15 个 `.csproj`（及 `tools/neige-term-bridge/Cargo.toml`）、**以及第 2 节中全部冻结接口文件**，提交到 `main` 并确认 `dotnet build` 编译全绿。
2. **Stage 1（6 路 `workspace: "share"` 并行开发 + 2 名只读 Verifier）**：
   - `w-doc-vm`：实现 `libs/Neige.ReportDoc` + `libs/Neige.PolicyVm`（Jint 原生直跑 ESM，零手写解析）+ `templates/{tsconfig.json, *.neige.js}`（通过 `tsc --noEmit` 强类型检查）与单测。
   - `w-agentkit`：实现 `libs/Neige.AgentKit`（Codex / Claude stream-json / ACP + `bwrap` Wrapper）与 Mock CLI 协议测试。
   - `w-vcskit`：实现 `libs/Neige.VcsKit`（`jj --colocate` 一等支持 + `git` 兜底 + `gh` CLI + `IWorkspaceDriverRegistry` 与一期 `DirectWorkspaceDriver` / `VcsWorkspaceDriver`）与真实临时 `jj`/`git` 仓库及租约单测。
   - `w-term-rust`：基于本仓库 `tools/rust-terminal/` 提纯组装 `tools/neige-term-bridge` + 实现 `libs/Neige.TermClient` 与 PTY 读写集成测试。
   - `w-sqlite`：实现 `src/Neige.Storage.Sqlite`（10 表群 DDL + `SqliteMigrator` 迁移备份 + `SqliteSingleWriter` 微批写入 + `ITrackStore` + >64KB 冷 Blob 剥离与 `RunMaintenancePassAsync` 确定性 GC）与高并发无 `SQLITE_BUSY`、外键级联索引、升级及防膨胀压测。
   - `w-kernel`：实现 `src/Neige.Kernel`（`TrackActor`、代表路由、Epoch 换页压缩、`TaskGraphScheduler`、`TemplateHost`、`BootReconciler`）与纯内存模拟测试。
   - 协调者按序审查并合入 `main`（或派 `v-libs` / `v-core` 验证每个分支 `dotnet test` / `cargo test` 全绿后再合并）。
3. **Stage 2（组合根 `Neige.Host` + 前端 `fe/` 协同瘦身切换）**：
   - `w-host`：实现 `src/Neige.Host`（REST / WS / `/mcp/kernel` / `/mcp/vcs` / `/mcp/p/{id}` 拦截器 / `Cronos` 调度 + 定期 `RunMaintenancePassAsync` / E2E 集成测试）。
   - `w-fe`：在独立前端分支将 `fe/core/{api,events}` 对接到 `/api/v1`，裁剪冗余旧恢复/桌面/日历模块并通过 `npm run build` + Playwright 验证。
