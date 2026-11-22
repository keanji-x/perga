# Template Primitives, Zero-Hook Evolution, and Flow v1.1 Design

> **Design Principle**: Subtraction first (see [`template-taste.md`](template-taste.md)). Converge on small, orthogonal primitives rather than accumulating demo-specific widgets, lifecycle hooks, or low-value knobs.
>
> **Follow-up Tracking**: [GitHub Issue #151](https://github.com/keanji-x/perga/issues/151).

---

## 1. Template Identity & Single-Source-of-Truth Boundaries (Stages 1–2)

1. **`before()` as a Write-Side Guardrail**:
   - Read-only introspection tools (`template_list`, `template_read`, `track_status`, `report_read`, `flow_read`, `provider_list`, `task_list`, `session_list`, `session_read`, `mail_list`) and the Lead's `track_set(clear: true)` escape hatch are hard-exempt from `before()`, so a broken template can never brick diagnostics or detach.
   - Guarded `tool.*` and `report.commit` hooks receive `ctx.session` (`sessionId`, `isLead`, `contextRatio`, `boundTaskId`).
2. **Three Single-Source-of-Truth Pipelines**:
   - **Template binding & atomic report seeding**: Track creation and `track_set` share template binding, report validation, and single-transaction SQLite persistence (template snapshot, data, flow, seeded report, and events commit atomically).
   - **Flow task routing**: `FlowTaskRouting` + `IPolicyVm.RouteTaskDispatch` resolve omitted agent access consistently across validation, materialization, projection, and admission; host shell `cmd` nodes stay on the exclusive `read_write` path unless explicitly sandboxed.
   - **Template widget validation**: `WidgetPayloadValidator` is shared across PolicyVm compile-time checks, report seeding, and runtime `report_commit` / `report_read` (using effective `setup(inputs)` view names via `IPolicyVm.ResolveViewNames`). Newly added/edited widgets fail closed; untouched historical blocks remain removable/diagnosable.
3. **Clean Authoring I/O**:
   - Internal `@perga-bundle-v1` stays hidden behind `templateId`, local workspace `path` (anchored via POSIX `openat(O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)` with regular-file checks, bounded reads, and off-mailbox I/O), and single-file `report.initialMarkdown`.
   - `template_read` defaults to `format: "summary"`, supports `format: "files"` (preserving parent layers as `base/...` in inherited snapshots) and focused single-file reads (`file: "..."`), and `format: "full"` for round-tripping.
   - MCP JSON serialization (`JsonHelpers.SerializerOptions` and `PergaJsonDefaults.McpOptions`) uses `UnsafeRelaxedJsonEscaping` to eliminate 6-byte `\uXXXX` expansion of CJK and punctuation.

---

## 2. Minimalist Evergreen 0-Token `watch` & Flow `reasoningEffort` (Stage 3)

### 2.1 Evergreen `watch` (`#12`)

Extend `do.kind = "watch"` with only **two orthogonal fields** (no `probeTimeout` or `onAttention` knobs):

- `do.repeat?: boolean` (default `false`): valid only for `cmd` probes (fail-closed on `prChecks`). When `repeat: true` and `timeout` is omitted, defaults to `"never"`.
- `do.timeout?: string`: adds `"never"` (`TimeoutSeconds = null`) and day units (`"7d"`).

Runtime invariants:
- **Internal single-probe cap**: bounded safely (60s or remaining overall timeout) so `"never"` can never wedge a subprocess forever.
- Repeating command watches acquire exclusive workspace access for each probe and release it before waiting for the next interval.
- **Fail-safe error policy**: non-`0`/non-`75` exit settles once as `Done(Attention)` and pauses (never error-spams).
- **Downstream Convergence Barrier**: while any transitive downstream node is active, the evergreen `watch` stays armed-waiting and never re-enters; once the downstream subtree reaches terminal states (and `watch` is not canceled/attention), it re-arms automatically.
  Outcome branches that cannot run in the current wave stay available for later waves. If a repeating watch depends on another repeating watch, the upstream wave controls when the downstream watch re-arms.
- **GC, anti-deletion, and restart recovery**: `recurringSources = cron ∪ (watch where repeat && !canceled)` exempts the watch and its downstream closure from GC; active repeating watches require explicit `canceled: true`; restarting the Host on the same SQLite DB resumes patrolling or re-arms converged watches seamlessly.
- **Policy fix**: `requireGateForReadWrite` applies to writable `run` nodes, not `watch(cmd)` probes.

### 2.2 First-Class `reasoningEffort` on Flow `do.kind = "run"`

- Add optional `reasoningEffort?: string` to `FlowDoSpecDto`, `docs/schemas/flow.v1.schema.json`, parser, validator, reconciler, projector, and `TrackActor.TaskAdmission` (validated fail-closed via `SessionLaunchValidator.ValidateModelAndEffort`), eliminating silent fallback to provider defaults like Codex `low`.

---

## 3. Minimalist Consolidation: 3 Orthogonal Report Primitives & Zero-Hook Evolution (Stage 4 / Issue #151)

### 3.1 Three Orthogonal Built-in Report Primitives (No `builtin:metrics`)

`EditorialMetricsStrip` is structurally a hardcoded 4-column HTML `<table>`. Built-in Report widgets converge on **3 irreducible data structures**:

| Primitive | Data Structure | Role |
|---|---|---|
| `builtin:flow` | **Graph / DAG** | Execution topology, edges, gates, and evergreen watch state |
| `builtin:list` | **Stream / Accordion** | Generic narrative entries (`id`, `title`, optional `date`/`subtitle`, `badge`/`tone`, `meta`, expandable `details` — zero domain fields like `verdict`/`expectations`/`assignKind`) |
| `builtin:table` | **2D Grid (`{ columns, rows }`)** | General-purpose tabular data with optional cell semantics (`tone`/`tag`/`code`/`check`); `okr-loop` outputs its 4 KR columns via `builtin:table` and retires hardcoded `EditorialMetricsStrip` |

Bespoke graphics remain in sandboxed `./widgets/*.html` with documented `postMessage` bridge and CSS theme tokens (`bg`, `paper`, `surface`, `text`, `text2`, `text3`, `hairline`, `accent`, `fontSans`, `fontSerif`, `fontMono`).

### 3.2 Zero-Hook Long-Lived Track Evolution (Expand-and-Contract)

Avoid `data.migrate()` and `track_set(data)` while keeping `ValidateData` fail-closed:
1. Pass `inputs` as a backward-compatible 3rd read-only argument: `report.views(data, kernel, inputs)`.
2. On `track_set` with preserved data (`!resetData`), run the new schema's `MaterializeDefaults` before `ValidateData` so additive `v1 -> v2` upgrades with `.default()` succeed seamlessly.
3. Follow **Expand-and-Contract** for deprecated fields (e.g., `equity-research` retains `ticker: t.string().optional()` while reading `inputs.ticker` in views so existing v1 tracks upgrade cleanly); incompatible schema breaks stay fail-closed (`resetData: true`), and `track_set(clear: true, resetData: true)` allows clearing residual `data`.
