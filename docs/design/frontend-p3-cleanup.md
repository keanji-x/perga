# Frontend P3 cleanup

Outcome: present Perga consistently, place destructive copy in its owning feature,
and remove unused frontend scaffolding. The expanded scope deliberately resets
frontend storage identity and report URI/mount contracts without compatibility.
Original baseline: 15ec7d73; rebased baseline: 5faac7c6 (PR #122 and #131). Review tier: L2 (two independent read-only reviews).

## Scope and ownership

Only fe/**, plugins/invest/**, plugins/paper-trading/** and this document change. Rename package metadata and visible names,
including SVG accessibility text and PWA assets. Keep the existing mark geometry.
Keep internal font-family and lint-plugin identifiers: renaming them brings no
visible benefit. The user's follow-up explicitly authorizes a clean break:
replace all neige:// source/plugin/wave links with perga://, input-form markers
with perga:input-form, calm storage keys with perga keys, and IndexedDB with
`perga`. Serve from `/` with root router basepath and PWA identity/scope/start_url.
No compatibility reads, legacy redirects or data migration. Existing saved
frontend preferences, caches and installed PWA identity are intentionally reset.
Synchronize both plugin directories and test Python consumers. Backend root
mount removal is owned by the parallel B1 worker.

Move track/card delete copy to features/track/delete-copy.ts and area copy to
features/area/delete-copy.ts. App composition imports each owner explicitly.
Move INV-DUP-010's canonical path and positive fixture with the track copy;
retain the uniqueness rule and frozen ownership protection on UI primitives.
The old confirm-dialog directory contained only copy.ts; remove its now-empty
inventory row. The generic dialog remains in ui/operation-feedback and ui/dialog.
The user explicitly authorized these inventory, package and styles changes;
record exact frozen paths in OWNERSHIP-CHANGE trailers.

## UI and instrumentation audit

Remove the disabled mobile search placeholder; no search implementation exists.
Give About fields readable labels. The original P3 verified the inline prose
editor styles. PR #122 subsequently removed inline Report Edit, including
onSaveProseBlock and its styles; the P3 editor browser case is cancelled on
rebase rather than restoring the removed product capability.
Remove the theme window driver (only tests of the driver reference it), replacing
its browser check with useTheme controls and persistence assertions. Remove the
unused terminal mount counter. Retain the query-gated terminal buffer dump:
terminal-reconnect.browser.test.tsx uses it to check replay and deduplication of
actual canvas-terminal output, which DOM text queries cannot inspect.
Retain desktop-painter hidden metadata for now: public, mobile-projection,
inventory-groups, header-status and task-row tests consume it, including browser
checks of nonvisual sizing and accessible status. Removing it requires a separate
projection/accessibility redesign; existing coverage must remain intact.

## Tokens and documentation

Audit exact custom-property names across tracked frontend code, tests, stylelint
and tool sources, plus theme dependency values and shipped Astryx CSS. Remove
only unreferenced configurable shared/palette entries from theme.config.json,
then run the real theme generator. Keep generated typography/navigation families
and test-referenced tokens. Record the final removed-name list below.
Replace stale Rust/Neige routing and generator claims with current /api/v1,
/api/today, transport injection and manual contract-maintenance instructions.
Link fe/AGENTS.md to the existing styles owner documentation.

## Verification

Run frontend lint, unit tests, build, full browser suite and PWA checks from this
worktree. Review the full diff, protected contract preservation and whitespace.
Record actual outcomes and both independent reviews after implementation.

No configurable token met the zero-reference rule (0 removed). The typed token
registries in styles/public.ts, tokens.contract.test.ts and public.contract.test.ts
still reference the apparent unused candidates. Preserve those contracts; no
generator or token registry policy was weakened to make deletion possible.

## Root navigation and gate details

Root mounting removes the old path-prefix boundary. Chat application links now
require a complete match in the router's public registered-route matcher, so
absolute workspace paths and malformed links remain inert. Recovery persists
only current root-mounted settings and Track routes. Application scroll ports
and report arrivals own all scrolling; the router's public restoration predicate
disables competing automatic scroll, including Back to the initial hash entry.

The key-literal rule follows the Perga storage namespace. Report URI schemes are
excluded. Existing widget postMessage types and MCP-name test literals are exact
file/value exceptions in ESLint configuration; another literal in those files
still fails. Literal and template fixtures independently check rejection,
permitted protocol values, wrong files and wrong values. Hostnames use no storage
colon prefix. The rule still cannot detect split/dynamic prefix construction.

Other host/plugin protocols (`dev.neige/*` metadata, `neige.overlay.set`,
external tool identifiers and plugin IDs), internal CSS classes, font families
and historical source URLs keep their existing spelling. They are separate
published contracts or internal names; the user authorized URI/browser identity
changes without expanding backend write ownership.

## Validation log

- Initial unit run: 4,621 passed / 1 failed. The key-rule fixture exposed the old
  namespace guard, corrected with current-namespace string/template fixtures.
- Initial full browser run: 1,965 passed / 9 failed. Root route admission,
  competing hash scroll, fake-search assertions and theme cleanup were repaired.
- Focused rule/key/recovery tests: 115 passed; focused changed browser tests:
  31 passed; focused Track scroll tests: 8 passed.
- Full browser rerun: 212 files / 1,973 tests passed.
- `(cd fe && npx playwright test --config tools/pwa/playwright.config.ts)`:
  2 passed, including actual Chrome installation and standalone launch at `/`.
  Its closed-port API proxy intentionally reports ECONNREFUSED for background
  install metadata fetches; both assertions pass without a real backend.
- `python3 plugins/paper-trading/examples/build_native_demo.py` and the same
  command with `--check`: passed. Regeneration refreshes content-derived snapshot
  hashes after source URI changes; hand-editing only the URI is insufficient.
- `python3 -m pytest plugins/invest/tests -q`: collection blocked by missing
  retired Rust schema/market-series fixtures. The existing tests remain intact.
- `python3 -m pytest plugins/invest/tests --ignore=plugins/invest/tests/test_projections.py --ignore=plugins/invest/tests/test_series.py --ignore=plugins/invest/tests/test_theses.py -k 'not recipe_views_are_native_views_over_invest_units' -q`:
  203 passed / 2 deselected. This is partial coverage, not a full plugin pass.
- `python3 -m pytest plugins/paper-trading/tests -q`: collection blocked by the
  same missing `crates/calm-types/src/report_blocks/native_view.schema.json`.
- `python3 -m pytest plugins/paper-trading/tests --ignore=plugins/paper-trading/tests/test_allocation_views.py --ignore=plugins/paper-trading/tests/test_native_demo.py -q`:
  155 passed / 1 failed on both attempts. The unchanged broker descendant-reap
  deadline test (`test_broker.py:184`, options2) exceeds its 1-second `/proc`
  observation deadline in this environment. Independent review confirmed these
  missing-schema and process-timing defects predate P3. No tests were weakened.
- No .NET tests: no backend file changed. Follow-up C1 should replace the retired
  plugin schema fixtures with an authoritative current contract.

- `(cd fe && npm ci)`: passed, 705 packages installed and the pinned Astryx
  patch applied successfully. The lockfile dependency graph is unchanged; only
  root package-name metadata changed.
- `node fe/tools/pwa/generate-icons.mjs`: passed; regenerated artifacts match the
  renamed checked-in assets exactly.
- `python3 -m pytest plugins/paper-trading/tests/test_allocation.py -q`: 91 passed.
- `python3 -m pytest plugins/invest/tests/test_research.py plugins/invest/tests/test_arguments.py -q`:
  117 passed. These focused protocol checks supplement, not replace, the blocked
  full plugin schema suite.

## Independent L2 review record

Two independent read-only reviewers examined the whole diff against 15ec7d73
and re-reviewed behavioral fixes: review sessions
`ses_6cd0e015302547088ba7a2d77b74a05a` and
`ses_f3dd537217434bdfa42798a4021e01bb`. Both approved with no blockers after
fixture regeneration, route admission/recovery/scroll fixes, ownership cleanup
and documentation corrections. Their final narrow gate follow-up verifies that
protocol literal exceptions cannot authorize an interpolated template head:
only complete static strings/templates qualify. The new dynamic-template
regression brings the rule suite to 110 tests.

The retained router restoration predicate uses a public API; TanStack still
registers its scroll/pagehide listeners and can persist its own sessionStorage
cache, although the predicate suppresses automatic restoration. This is accepted
for the single production router; no private history fields are read or written.

The configurable token audit covered 109 shared/light/dark names: 108 appear in
the pinned type/test registries and --leading-none is used by the generator and
generated public registry. The generated typography/navigation inventory also
remains a public contract, so no token deletion was justified by the requested
zero-reference standard.

The original delivery b7d5329b used baseline 15ec7d73. At the user's request,
P3 was then rebased onto 5faac7c6, including Unified Track Flow v1 (PR #122)
and the root-only Host mount (PR #131). Upstream capabilities are preserved.

## Original b7d5329b verification outcome

- `(cd fe && npm run lint && npm test && npm run build && npm run test:browser)`
  passed after `npm ci`: 348 unit files / 4,626 tests, then build and 212 browser
  files / 1,973 tests. The subsequent narrow template-guard regression was
  followed by another full `(cd fe && npm test)`: 348 files / 4,627 passed.
- Final rule suite: 110 passed. After removing the empty ownership row, tracked
  fixture, ownership/style manifest and top-level gates passed; architecture and
  ownership suites passed 196 tests across 6 files.
- An isolated in-memory copy of the previous template guard admitted the new
  dynamic-head fixture (0 diagnostics); the final production rule rejects it
  (1 diagnostic). This confirms the regression fails against the previous guard
  without mutating a production file during parallel test execution.
- Both independent reviewers approved the final narrow gate delta with no
  blockers, as well as the earlier complete L2 review and behavioral fixes.
- Remaining output is pre-existing: Vite's large-chunk advisory, jsdom scrollTo
  notices and React act notices in the browser tests. Lint introduces no warning
  or disable. Full plugin pytest is not claimed green; limits are listed above.
- Final `(cd fe && npm run lint)` passed after the template-guard and inventory
  changes. `git diff --check origin/main` and the fixed-baseline scope audit pass.
  The final commit carries all 11 exact frozen-path ownership trailers.

## Rebase adaptation and verification

`git fetch origin main && git rebase origin/main` completed without conflicts.
The initial range-diff `b7d5329b^..b7d5329b` versus `origin/main..HEAD` showed
an identical patch. The only subsequent adaptation removes the P3-added inline
Report Edit browser case in task-execution.browser.test.tsx, because PR #122
removed onSaveProseBlock. That file now matches upstream exactly. No production
behavior changes are introduced by the rebase adaptation; existing independent
L2 approvals remain applicable under the mechanical-delta rebase rule.

Audited all frontend/plugin paths and specifically new Flow components,
report-reference.tsx and core/domain/report-flow.ts for /next/, neige:// and
calm: literals. None require additional changes. PR #131's backend root mount
is inherited unchanged; this P3 commit still changes only its authorized paths.
The package manifest/lock dependency graph matches the already installed P3
tree; the rebase adds no dependency update.

Re-run full lint, unit tests, build and browser suites on the rebased tree;
verify exact frozen-path trailers, both-baseline patch comparison, whitespace
against origin/main and a clean committed worktree before delivery.

Both-baseline production patch comparison excludes this document and the
cancelled browser case. `git patch-id --stable` returns the identical ID
`4c1f210e75d3dd070701f303793859492daebd42` for the original b7d5329b patch
and the rebased patch against origin/main. All 11 frozen paths in the latest
main inventory have their exact existing OWNERSHIP-CHANGE trailers.

### Rebased-tree results (5faac7c6)

- `(cd fe && npm run lint && npm test && npm run build && npm run test:browser)`:
  passed, with 351 unit files / 4,641 tests and 210 browser files / 1,968 tests.
  This run includes the current Flow consumers and preserves the upstream removal
  of both inline Report Edit browser suites and the obsolete P3 editor case.
- `git diff --check origin/main`: passed. Frozen-path coverage remains all 11
  exact trailers. No backend file is authored by this commit.
- Final range-diff against b7d5329b contains only the cancelled editor test and
  documentation/commit-message updates; the remaining patch has the identical
  stable patch-id recorded above. This is a mechanical/test adaptation, so the
  original two independent L2 approvals remain applicable.
