# Native Checkpoint Stage Deliverable & Routing Enforcement

## Outcome

Give Perga Tracks a first-class, human-readable **Checkpoint Stage Deliverable** surface directly inside the Report document (`perga-block checkpoints`), backed by optional template-declared `checkpoints` and enforced `tasks.routing` rules in `Perga.PolicyVm` and `Perga.Kernel`.

## Design Principles (Aligned)

1. **Target-Driven Markdown First**: The Track's Target stays concise, plain Markdown at the top of the Report so humans can read and edit it directly with zero component ceremony.
2. **Native Circular Stage Stepper (No Iframe)**: Stages (`设计`, `实现一`, `实现二`, `L1 Review`, `L2 Review`, etc.) render natively inside the Report DOM using Perga's semantic design tokens (`--bg`, `--paper`, `--hairline`, `--success`, `--accent`, `--warn`, `--error`).
3. **Three-Layer Information Hierarchy**:
   - **Foreground (Rail)**: Circular stage nodes showing status only (`done`, `running`, `pending`, `blocked`) — zero rule or model clutter.
   - **Midground (Selected Stage Detail)**: Clicking any stage node seamlessly reveals its deliverable summary, checks, a real link to open the workspace document in Perga's native `ReportFileViewer`, and a provenance bar linking straight to the bound Worker conversation. Pre-registered `pending` stages show their upstream dependencies and scheduling constraints on demand.
   - **Background (Policy Drawer)**: Trivial global scheduling constraints (e.g. `role=review` requires a specific model) stay collapsed in a discreet top-right disclosure unless opened or violated.

## Contracts & Boundaries

- **`Perga.ReportDoc`**: Adds `ReportBlockKind.Checkpoints` (`perga-block checkpoints`), preserving deterministic block IDs, optimistic-lock `ReadAnchor`, and Zstd revision snapshots.
- **`Perga.PolicyVm`**: Adds optional `checkpoints` (`CheckpointSpec[]`) and `tasks.routing` (`TaskRoutingRule[]`) declarations to `CompiledTemplateManifest` with backwards-compatible defaults.
- **`Perga.Kernel`**:
  - Extends `DispatchWorkerRequest` with optional `Role` and `CheckpointId`.
  - Evaluates `tasks.routing` during `DispatchWorkerAsync`: fills omitted `provider`/`model` from the first matching rule (expanding `${inputs.<key>}`), and rejects conflicting explicit values with `PolicyDenied` when `enforce: true`.
  - Records `role` and `checkpointId` in `task.context` for explicit Checkpoint ↔ Worker attribution.
- **Frontend (`fe/core` & `fe/web/src/features/report/checkpoints/`)**:
  - Adds Zod schema `checkpointsBlockPayloadSchema` in `fe/core/domain/report-checkpoints.ts` and registers `checkpoints` in `report.ts` and `perga-v1.ts`.
  - Renders `ReportCheckpointsBlock` inside `ReportDocument`, wired to existing `onOpenFileLink` (native file viewer) and `onOpenWorker` (native conversation panel).

## Verification

- Backend: `dotnet test` across `Perga.ReportDoc.Tests`, `Perga.PolicyVm.Tests`, `Perga.Kernel.Tests`, and `Perga.Host.IntegrationTests`.
- Frontend: `npm run lint && npm run build && npm test` in `fe/`.
- Live Preview: Build and launch an isolated `Perga.Host` instance on `0.0.0.0:4299` with seeded Track, real workspace docs, and real bound Worker sessions for interactive browser review.
