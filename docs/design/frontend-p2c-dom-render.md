# P2c: explicit layout and committed rendering

Outcome: keep responsive editing, settings navigation and per-track viewport state while removing DOM relocation, broad observation and render-time mutations.

- Drawer instances register open lifetimes through a component-scoped bubbling handshake accepted by the nearest main host; shell owns aggregate panel width and spring state. Drag previews remain imperative CSS writes at pointer rate.
- Settings retain one React-owned body across presentation changes. History is tracked from public history subscription actions, without private state indices. Modal focus and dismissal remain owned by the existing Dialog in both presentations; compact styling expands its published layout classes to a full-page surface. No frozen Dialog API extension is required.
- Track title editing uses a stable React owner and a fixed mobile editor position measured against the component title slot; the editor DOM never changes parent, while read controls portal directly to the shell. A slot ResizeObserver measures only that host, not the document. No detached nodes or appendChild relocation.
- The shell stage stays mounted across route navigation. Route identities continue to own domain lifetimes; scroll snapshots remain per Track.
- Scroll input listeners are consolidated and recovery uses one cancellable frame job. A reader gesture wins over restoration.
- Serial setting writers initialize in the user event, not during rendering; every queued closure carries the transport admitted at its own press. Composer metadata uses typed comparison rather than JSON serialization.

Scope excludes P2b router/public.tsx and P2d files. No backend or durable storage contract changes. Review tier L2; two independent reviews plus lint, unit tests, build and full Chromium suites on the final tree.

The board still needs three elapsed frames because BoardHost owns a two-frame selected-card reveal. One cancellable frame loop replaces nested callbacks without guessing a different ordering. Input is observed once per Track, including while report preparation is pending.

The shell stage had no stage-level route animation or scroll reset implementation; its pathname key only destroyed descendant ownership. Domain route identities and per-Track snapshots continue to define resets. The regression suite asserts that the stage parent survives sidebar Track switches.

Guarded self-state adjustments in unrelated frozen primitives are outside this change; no mutation of their public contracts or enforcement allowlists is introduced. Existing JSON for wire/storage/action identities remains; composer and exchange memoization no longer use serialized JSON dependencies.

Validation on main baseline `4151121`: full frontend lint, 4,619 unit tests, build, and 1,971 Chromium tests across 212 files passed. The rebase from `818dcf4` was conflict-free and range-diff identical before the later test-only stage selector correction.

Two independent L2 reviews found the initial-open registration ordering issue; both accepted the passive-registration fix and final deltas with no blockers. Browser verification also covers zero-distance exit completion, focused editor identity/draft/selection across responsive switches, and the persistent shell stage. The cold-open regression explicitly checks the first close still animates layout allocation.

No frozen ownership paths were modified, so there are no required `OWNERSHIP-CHANGE` trailers. No backend changes, gate exceptions, generated protocol changes, or P2b/P2d implementation edits are included.
