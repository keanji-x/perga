# Router composition boundaries (P2b)

The outcome is a small route-tree entry with unchanged route identity, keyed Track
mounts, conversation restoration, focus ownership and card creation delivery semantics.

- `public.tsx` owns route construction, boot dependencies and ShellRoute.
- `track-route.tsx` owns the aggregate read, workspace retry and loading boundary.
- `track-route-body.tsx` connects Track surfaces, report navigation and conversation ports.
- `conversation-panel.tsx` connects main, side and terminal surfaces;
  `conversation-pane.tsx` owns one conversation pane's existing lifecycle.
  Initial message redemption and archive/recovery notices have focused hooks in
  `conversation-initial-send.ts` and `conversation-action-area.tsx`. Their shared
  input contract is explicit in `conversation-panel-contract.ts`.
- `features/track/new-card/controller.tsx` owns menu policy, form, busy/error state,
  retries and suppression of stale attempt effects. App injects the shared keyed
  intent holder, terminal create port, directory lister, theme reader, desktop
  workbench action and successful-card navigation. No feature imports app.

The keyed request still retains its first body and theme across unknown-outcome
retries. New attempts abort the previous UI owner; unmount aborts the active owner.
Transport/cache invalidation remains in the existing app mutation adapter. Resource
mount keys, hook order within each extracted lifecycle, provider scope and query
keys are preserved. There is no persistence, API or backend change.

Existing router contract, Track, conversation, card-create, focus and real-browser
suites verify observable behavior. Full frontend lint, unit tests, build and browser
suites plus two independent L2 reviews are required before delivery.

The inventory already owns router and features/track by writable directory; no
frozen interface or inventory expansion is required.

## Independent review

Two independent L2 reviewers compared the final extraction with baseline
`818dcf4`, including the initial-send and action-area hooks, late responses,
retry identity, focus ownership, feature ports and dependency direction. Both
reported no blockers. Existing render-time ref/state patterns remain separate
P2c work; this extraction preserves their current semantics.

## Validation

- `npm run typecheck`: passed.
- `npx vitest run --project web-dom web/src/app/router/new-card-controller.test.tsx`:
  2 compact creation lifecycle tests passed; both also ran in the full suite.
- `npm run lint`: passed, including ownership, CSS and dependency checks.
- `npm test`: 347 files and 4,620 tests passed.
- `npm run build`: passed; Vite retains its large-chunk advisory.
- `npm run test:browser`: 212 files and 1,970 tests passed.
- Final whitespace-only EOF cleanup passed `git diff --check origin/main`.
