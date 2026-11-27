# TOML-Driven Application Settings (`data/settings.toml`) — Phase 2 Design (L2)

## Outcome and delivery boundary

Introduce a schema-driven application settings store backed by `<dataDir>/settings.toml` so future user-configurable runtime preferences can be added in one backend registry entry and automatically surface in the Settings UI with live hot-reload.

This is an L2 change (persistence, HTTP/OpenAPI contract, cross-component runtime configuration, and `0600` file boundary) requiring two independent reviews.

### Isolation boundary

- **Operator configuration (`config/perga.toml`) stays strictly isolated.** Managed instances continue reading `[instance]`, `[providers.*]`, and `[proxy]` via `tools/perga-deploy/perga_config.py` at launch; the web Settings API never reads or overwrites `config/perga.toml`.
- **Application preferences live in `<dataDir>/settings.toml`** (resolved via `PergaHostOptions.ResolveStoragePaths()`), with POSIX mode `0600`, preserved across release upgrades and rollbacks.
- **Browser-local preferences (`Appearance › Theme`) remain browser-local** in `localStorage`.

## Initial settings (zero fake switches)

Every field in the initial registry maps to an existing, live-consumed `KernelOptions` setting and takes effect immediately on save:

| TOML key | Section | Control | Type & validation | Default | Live consumer |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `planners.default_provider` | `planners` | `select` | Canonical registered provider ID | `KernelOptions.DefaultProvider` | `PergaKernelService` / `TrackActor.GetDefaultProviderCore()` |
| `sessions.auto_compaction_percent` | `planners` | `segmented` | Integer `50..95` (preset options: `70%`, `80%`, `90%`) | `round(KernelOptions.AutoCompactionThreshold * 100)` | `SessionTurnRunner` / `TrackActor.SessionResume` |
| `sessions.idle_timeout_minutes` | `planners` | `select` | Integer `5..240` (preset options: `5 min`, `15 min`, `30 min`, `1 hr`, `2 hr`) | `round(KernelOptions.SessionIdleTimeout.TotalMinutes)` | `TrackActor.SessionInterop` / `TrackActor` / `TrackActorRegistry` |

When a user hand-edits `data/settings.toml` with a valid non-preset integer (e.g. `85` or `45`), the backend preserves it and the frontend appends it to the control's option list so manual TOML edits round-trip cleanly without silent clobbering.

## Architecture and contracts

### 1. Contracts (`src/Perga.Contracts/SettingsContracts.cs`)

Flat, OpenAPI-friendly records serialized via the existing Host JSON options:

- `SettingsSectionDto(string Id, string Label, string? Description)`
- `SettingsFieldOptionDto(string Value, string Label)`
- `SettingsFieldDto(string Key, string Section, string Label, string? Description, string Control, string ValueType, JsonElement DefaultValue, int? Min, int? Max, string? Unit, IReadOnlyList<SettingsFieldOptionDto>? Options)`
- `SettingsSnapshotDto(long Rev, IReadOnlyList<SettingsSectionDto> Sections, IReadOnlyList<SettingsFieldDto> Fields, IReadOnlyDictionary<string, JsonElement> Values)`
- `PatchSettingsRequestDto(long ExpectedRev, IReadOnlyDictionary<string, JsonElement> Patch)`

### 2. Kernel domain & registry (`src/Perga.Kernel/Settings/`)

- `AppSettingsRegistry`: single authoritative catalog of supported TOML keys, section metadata, UI control metadata (`segmented` | `select`), validation rules, and effective value projection into an immutable `AppSettingsSnapshot`.
- `IAppSettingsStore` (Kernel port): `ReadAsync(CancellationToken)` and `WriteAsync(IReadOnlyDictionary<string, object> values, CancellationToken)`.
- `AppSettingsService`:
  - Loads `<dataDir>/settings.toml` on startup (missing file → in-memory defaults without creating a file; malformed TOML or invalid value → fail-closed with a clear diagnostic error, never silently resetting).
  - Serializes writes with `SemaphoreSlim(1, 1)`, validates every key/value in `Patch` (rejecting unknown keys, wrong types, out-of-range integers, or unregistered providers with HTTP `400` + `field`), checks `expectedRev` (returning HTTP `409` on stale revision), writes atomically via `IAppSettingsStore`, swaps the immutable `Current` snapshot, and bumps `Rev`.
  - Refreshes the revision from the file on every `GET /api/v1/settings` and before compare-and-set on `PATCH /api/v1/settings`. Sequential manual edits are detected even when the file's modification time stays the same. Concurrent manual writes during an in-flight `PATCH` are outside the supported concurrency protocol.
- `PergaKernelService` & `TrackActor`: read live `DefaultProvider`, `AutoCompactionThreshold`, and `SessionIdleTimeout` through `AppSettingsService.Current` (falling back to `KernelOptions` when no settings service is injected in isolated unit tests).

### 3. Host TOML store & HTTP (`src/Perga.Host/`)

- `SettingsTomlCodec` + `TomlAppSettingsStore` (`src/Perga.Host/Configuration/`):
  - Zero-dependency strict TOML subset codec supporting `[section]` tables, quoted strings (standard TOML escapes), signed integers, booleans, and `#` comments.
  - Rejects duplicate tables/keys, unexpected bare tokens, and malformed syntax fail-closed.
  - Deterministic TOML writer grouped by table header (`[planners]`, `[sessions]`).
  - Reads require a regular file with no group or other permissions on POSIX.
  - The store writes and flushes a sibling temp file with POSIX mode `0600`, renames it over `settings.toml`, then syncs the parent directory on Unix before reporting success.
  - After a post-rename directory sync failure, the store retries the sync before its next read or write. The service keeps the last confirmed snapshot until the replacement is durable.
- `ApiV1Endpoints.cs` + `OpenApiDocumentExporter.cs`:
  - `GET /api/v1/settings` → `200 OK` (`SettingsSnapshotDto`)
  - `PATCH /api/v1/settings` → `200 OK` (`SettingsSnapshotDto`), `400 Bad Request` (`field` + message on validation error), `409 Conflict` (stale `expectedRev`).
  - Regenerate `fe/core/api/generated/openapi.json` and `wire.ts` via `(cd fe && npm run gen:api)`.

### 4. Frontend schema-driven Settings (`fe/`)

- `fe/core/api/schemas.ts` & `fe/core/api/operations.ts`: Zod schemas matching generated `wire.ts`, `getSettings`, and `patchSettings`.
- `fe/web/src/app/providers/app-settings.ts`: saves edits immediately, refreshes the snapshot after a conflict or unconfirmed save, and surfaces errors inline.
- New Track prefers the saved default provider when ready on the selected target and otherwise uses the first ready provider. It submits the provider shown in the composer and replays saved requests with their original bodies and keys.
- `fe/web/src/features/settings/fields.tsx`: extend `SettingFieldsList` cleanly to render both `segmented` (`@astryxdesign/core/SegmentedControl`) and `select` (`@astryxdesign/core/Selector`, `size="sm"`), zero hand-rolled controls and zero speculative unused control kinds.
- `fe/web/src/features/settings/planners.tsx`: render a compact `DEFAULTS` section driven by the server schema (`section === 'planners'`) directly above `PROVIDERS ↻` on `--paper` with the same Editorial Parchment hairline rhythm, zero Save/Reset button clutter.

## Verification plan

- **Backend**:
  - `SettingsTomlCodec` unit tests (round-trip, escapes, comments, duplicate keys/tables, malformed input).
  - `TomlAppSettingsStore` & `AppSettingsService` tests (missing file defaults, `0600` permissions, atomic write, restart persistence in a fresh host on the same `dataDir`, stale `expectedRev` `409`, invalid value `400` with `field`, corrupted TOML fail-closed, live Kernel consumption of updated `DefaultProvider` / `AutoCompactionThreshold` / `SessionIdleTimeout`).
  - Host HTTP integration tests (`GET/PATCH /api/v1/settings`).
  - Full `dotnet test Perga.sln`.
- **Contract & Frontend**:
  - `(cd fe && npm run gen:api -- --check)`
  - `(cd fe && npm run lint && npm run build && npm test)`
  - Chromium browser tests (`web/src/features/settings/`, `responsive`, `system-upgrade`) + real visual screenshot review in Light, Dark, and Mobile viewports.
- **Reviews**: Two independent L2 code reviews covering data integrity, partial failure, concurrency, isolation, contract drift, accessibility, and code simplicity.
