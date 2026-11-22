# P2d: native code panes and focused domain modules

Based on origin/main 818dcf4. Review tier: L2.

Use native CodeMirror with explicit view ownership, read-only state, compartment
reconfiguration and destruction on unmount. Retain the shared static GitHub native
theme extensions and their unchanged versions. They have no React dependency. A
local dynamic HighlightStyle/theme factory would accumulate style-mod rules per
document; avoid both that leak and exceptions to the module-state gate. Native
height/overflow settings affect only the owned editor DOM. Preserve search adapter semantics,
keyboard focus, selection and scrolling across language/theme reconfiguration.

Reuse normalized Markdown lines for limits, diagnostics and the normalized AST.
Optimize currency disambiguation only where differential checks establish equal
AST values and positions; GFM references and math delimiters remain authoritative.

Keep report decoding, outline and task projection together; extract payload schemas,
backlinks, verdict parsing and transport operations into four owning modules. Extract
conversation catalogs/model writes and session commands from session/transcript models.
Consumers import owners directly; remove the old report re-exports. Extract model
selection policy and the effort slider from the picker. Preserve the acyclic graph,
transport contracts and error classification. Main modules are each below 600 lines.

Pending boundary decision: all consumers of `ui/confirm-dialog/copy.ts` are in
explicitly forbidden P2b/P2c files (`router/public.tsx`, `shell/sidebar.tsx`, and
`features/track/page/public.tsx`). The file remains unchanged until import-only
migration is authorized. Proposed owners are `features/track/delete-copy.ts`
(track/card copy) and `features/area/delete-copy.ts` (area copy). No cross-worktree edits.
No backend, persistence or API contract changes. Frozen ownership declarations
remain enforced and changes carry OWNERSHIP-CHANGE trailers.

Validation: focused behavior checks while iterating, then frontend lint, unit
suite, build and full browser suite, plus two independent read-only L2 reviews.

Two independent L2 reviews compared moved declarations against the baseline and
ran independent in-memory Markdown differential checks (12,000 and 2,500 cases,
plus the implementation's 8,000-case check). Reviewed fixes retain searchKeymap,
preserve original CSS entry/import order, and avoid dynamic theme registration.

Final implemented-tree checks passed: npm ci (including the Astryx patch), lint,
4,620 unit tests, production build and 1,972 browser tests across both projects
(maxWorkers=2). No backend or forbidden parallel-owner paths changed. The copy
relocation above is not included in this delivery and remains pending.
