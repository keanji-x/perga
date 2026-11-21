# Template Package v2.1 Follow-up Architecture & Taste Spec

> **Status**: Design approved for implementation on `feat/template-v2-followup` (base `origin/main` `b9a7d83`, post-PR #107).
> **Review Tier**: **L2** (adds forward SQLite migration `0022_track_data.sql`, changes Track data persistence authority & optimistic concurrency, updates `/api/v1` + MCP `data_patch`, and refactors frontend report widget/protocol components).

## Outcome

Fix three architectural ownership/concurrency/duplication issues discovered at PR #107 merge time together with four maintainability/taste cleanups:

1. **Track owns business data (`tracks.data_json` + `tracks.data_rev`)**, separated from template hook state (`tracks.template_state_json`), so switching or detaching templates never silently destroys accumulated research data.
2. **Single authoritative business data write path (`data_patch` / `PATCH /api/v1/tracks/{id}/data`)** enforced by host C# `TemplateDataValidator`, with **optimistic locking (`expectedDataRev`)** and **keyed record array deltas (`$upsert` / `$delete`)** to prevent multi-agent lost updates.
3. **Single authoritative scheduling/automation model**: retire the parallel display-only `triggers` declaration in favor of `#89` unified durable `schedules`.
4. **Frontend & PolicyVm taste cleanups**: consolidate shared editorial primitives & retire duplicate `checkpoints/range-axis.tsx`, generalize `builtin:detail` to domain-neutral `callouts` / `entries`, split `TemplateDataValidator.cs` under 800 lines, and visually de-noise `TemplateProtocolBar`.

---

## Detailed Design

### 1. Track Data Persistence Authority (`0022_track_data.sql`)

- **Forward migration** `src/Perga.Storage.Sqlite/Migrations/0022_track_data.sql` (all existing `0001`..`0021` files remain byte-frozen):
  ```sql
  ALTER TABLE tracks ADD COLUMN data_json TEXT NOT NULL DEFAULT '{}';
  ALTER TABLE tracks ADD COLUMN data_rev INTEGER NOT NULL DEFAULT 0;
  ```
- **`TrackDto` (`src/Perga.Contracts/Contracts.cs`)**:
  - Append optional fields with defaults: `JsonElement Data = default, long DataRev = 0` (normalized to `{}` when undefined).
- **`SqliteTrackStore`**:
  - Read and write `data_json` and `data_rev` in track creation, queries, and `UpdateTrackAndEmitAsync`.
  - Keep `template_state_json`-only updates (e.g., task settlement / hook state updates) strictly isolated from `data_json` and `data_rev`.
- **Track & Template Lifecycle (`TrackActor.cs`, `PergaKernelService.cs`)**:
  - `CreateTrackAsync`:
    - `TemplateState` ← `initialState(inputs)` (or `{}`).
    - `Data` ← `data.initial(inputs)` validated & `_meta`-stamped via `TemplateDataValidator` (or `{}` if template has no `data.initial`), `DataRev = 0`.
  - `SetTrackTemplateAsync(templateId/source, inputs, dryRun, resetData = false)`:
    - `TemplateState` ← new template's `initialState(inputs)` (or `{}`).
    - `Data`:
      - If `resetData == true` OR existing `_track.Data` has no user fields (empty `{}` or only `_meta`): initialize `Data` from new template's `data.initial(inputs)` (or `{}`). Increment `DataRev` if `Data` changed.
      - Else (existing `_track.Data` has user data):
        - If the new template declares `data.schema`: validate existing `_track.Data` against the new schema with current `knownTaskKeys`.
          - If valid: **preserve `_track.Data` and `_track.DataRev` unchanged**.
          - If invalid: **fail-closed with `KernelException.Conflict`** detailing the schema incompatibilities (caller must explicitly pass `resetData: true` or migrate data before switching).
        - If the new template has no `data.schema`: **preserve `_track.Data` and `_track.DataRev` unchanged**.
  - `ClearTrackTemplate` (`track_set(clear: true)`):
    - Clears `TemplateId`, `TemplateSnapshot`, `TemplateInputs`, `TemplateState = {}`.
    - **Preserves `_track.Data` and `_track.DataRev` untouched**.

### 2. Single Business Data Write Path, `expectedDataRev`, and Keyed Array Deltas

- **No dual write paths**:
  - Policy hooks (`before`, `after`, `turnContext`, `headerView`) continue to read/mutate `TemplateState` (`ctx.state`) only; they never write `Track.Data`.
  - `Track.Data` is ONLY mutated via `TrackActor.PatchTemplateDataAsync` (`PATCH /api/v1/tracks/{trackId}/data` and MCP `data_patch`) and initial creation / explicit `resetData: true`, all going through host C# `TemplateDataValidator`.
  - `TrackActor.GetTemplateViewsAsync` (`ProjectViews`) and `BuildActiveTemplateDataSectionCore` read `_track.Data` (not `_track.TemplateState`) and expose `dataRev`.
- **Optimistic concurrency (`expectedDataRev`)**:
  - `PatchTemplateDataAsync(..., long? expectedDataRev = null, ...)`:
    - If `expectedDataRev.HasValue && expectedDataRev.Value != _track.DataRev`, throw `KernelException.Conflict` with both expected and current revisions.
    - When `result.NextData` differs from `_track.Data`, increment `DataRev = _track.DataRev + 1` atomically inside `UpdateTrackAndEmitAsync`.
  - MCP `data_patch` input schema: `{ patch: object, expectedDataRev?: integer }` -> returns `{ ok: true, dataRev, data }`.
  - HTTP `PATCH /api/v1/tracks/{trackId}/data`: accepts `{ patch: object, expectedDataRev?: integer }` (and gracefully accepts a raw patch JSON object when neither `patch` nor `expectedDataRev` envelope keys are present) -> returns `{ ok: true, dataRev, data }`.
  - `TemplateViewsProjection` (`GET /api/v1/tracks/{trackId}/views`) includes `long DataRev = 0`.
- **Keyed array record deltas (`$upsert` / `$delete`) in `TemplateDataValidator.MergeAndStamp`**:
  - When a patch property is a JSON object containing only `$upsert` and/or `$delete` keys and targets an array property (or initializes one):
    - `$delete`: array of string keys (`id` or `code`); removes matching records from the existing array.
    - `$upsert`: array of objects each with a non-empty string `id` or `code`; shallow-merges onto an existing record with the same key (preserving untouched fields such as `expects`), or appends if not present, stamping `_meta` on changed/new records.
    - Both `$delete` and `$upsert` undergo the same `ResourceBudget`, `ForbiddenKeys`, `_meta` guard, and final Fail-Closed `ValidateRoot` schema check.
  - Plain full-array replacement remains valid when a JSON array is passed directly.

### 3. Unify Automation on `#89` `schedules` (Retire Parallel Display-Only `triggers`)

- Migrate recurring cron jobs in `templates/equity-research/template.perga.js` and `templates/okr-loop/template.perga.js` to real `#89` declarative `schedules` (`[{ id, cron, timezone, title, prompt }]`).
- Remove display-only `triggers.js` from `templates/agents-delivery/`, `templates/equity-research/`, `templates/okr-loop/` and remove `triggers` declarations from their `template.perga.js` / `views.js`.
- Remove unused `CompiledTriggerSpec`, `ParseTriggers`, `TemplateViewsProjection.Triggers`, `TriggerSpec` (`perga-template.d.ts`), and `triggers` plumbing from `PolicyVm`, `TrackActor.Views.cs`, `fe/core/domain/report-widget.ts`, `fe/core/api/generated/openapi.json`, and `fe/web/src/features/report/widget/public.tsx`.

### 4. Consolidate Shared Editorial Primitives & Retire `checkpoints/range-axis.tsx`

- Extract shared editorial subcomponents (`EditorialByline`, `EditorialGateStrip`, `EditorialMetricsStrip`, `EditorialTriggersTable`) into a shared module (`fe/web/src/features/report/editorial/` or cleanly factored shared export under `fe/web/src/features/report/`, updating `fe/module-file-inventory.yaml` ONLY if a new top-level owned path requires it — prefer keeping under `fe/web/src/features/report/` where ownership is already covered by `fe/web/src/features/report`!).
- Remove redundant `fe/web/src/features/report/checkpoints/range-axis.tsx` and its unused axis branch in `checkpoints/public.tsx` (custom price axis is served by `templates/equity-research/widgets/range-axis.html`).

### 5. Generalize `builtin:detail` (`callouts` / `entries`)

- Replace `blockers` / `rounds` in `NativeDetailWidget` (`fe/web/src/features/report/widget/public.tsx`) with domain-neutral:
  - `callouts?: { title?: string; items: string[] }`
  - `entries?: { title?: string; items: Array<{ tag: string; status?: 'pass' | 'warn' | 'fail' | 'running' | 'neutral'; statusLabel?: string; actor?: string; summary: string }> }`
- Update `templates/agents-delivery/views.js` to emit `callouts` and `entries`.

### 6. Split `TemplateDataValidator.cs`

- Split `libs/Perga.PolicyVm/TemplateDataValidator.cs` into:
  - `libs/Perga.PolicyVm/TemplateSchemaExtractor.cs`: `TemplateSchemaNode`, `ResourceBudget`, `ExtractSchemaNode`, `ExtractDataElement`.
  - `libs/Perga.PolicyVm/TemplateDataValidator.cs`: pure `JsonElement` `ScanPatchSafety`, `MergeAndStamp` (including `$upsert` / `$delete`), `ValidateRoot`.
- Keep both files well below 600 lines.

### 7. Visually De-noise `TemplateProtocolBar`

- Update `fe/web/src/features/report/template-protocol/protocol.module.css` so `.template` renders as quiet editorial masthead metadata (no chip background, muted `--text-3` ink, regular weight) instead of an accent-blue pill box, preserving all existing semantics, keyboard navigation, contrast, and tests.

---

## Verification Plan

1. **Migration & restart tests** (`Perga.Storage.Tests`, `Perga.Kernel.Tests`, `Perga.Host.IntegrationTests`):
   - Opening a DB upgraded from `0021` -> `0022` preserves existing rows with `data_json = '{}'`, `data_rev = 0`.
   - `CreateTrack` -> `data_patch` -> `track_set` (compatible template switch preserves `Data` & `DataRev`; incompatible switch fails closed with `Conflict`; explicit `resetData: true` resets; `clear: true` preserves `Data` & `DataRev`).
   - Hook `before`/`after` mutating `ctx.state` updates `TemplateState` without touching `Data` or `DataRev`.
   - `expectedDataRev` mismatch returns `Conflict` without mutating `Data`.
   - Concurrent/sequential keyed `$upsert` & `$delete` preserve untouched records and untouched fields (e.g., `expects`).
   - Fresh host reopen preserves `Data` and `DataRev`.
   - `equity-research` & `okr-loop` materialize real `#89` schedules on track creation.
2. **Full suites**:
   - Isolated `dotnet test Perga.sln` (all test projects green).
   - `./fe/node_modules/.bin/tsc -p templates/tsconfig.json --noEmit`.
   - `(cd fe && npm run lint && npm run build && npm test)` (including `check-readonly-change-requests` with `OWNERSHIP-CHANGE:` trailers for `fe/core/api/*`).
