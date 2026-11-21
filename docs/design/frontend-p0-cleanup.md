# Frontend P0 cleanup

Remove unused Neige runtime and tooling while preserving Perga production entry
points and active architecture checks. P1 product/protocol removal is separate.
This scope is authorized by contract worker-f06f9a3dd48b6b7ba80f09c1.

## Cascade and contract boundaries

CSS aggregates declare their own top-level layers. Importing them under another
layer nests vendor/Astryx rules and changes cascade priority. The repository
audit therefore inspects the two explicit local imports, vendor.css and
astryx-theme.css, at their actual position. Other unlayered imports still fail.
Positive and single-violation fixtures cover both aggregate paths; a production
regression checks the effective library layers. MCP activity names follow
KernelMcpTools, including classification of track_set as a write.

## Development tooling and ownership

The preview server generates its HTML dynamically. Its demo pages and card
registration belong in tools/preview/pages, with a separate browser TypeScript
project included in the normal build. Production app/shell and cards/public do
not expose previews. Keep the preview worker's ownership/isolation tests and
both routes, and let the card-motion browser test consume the tool fixture.

Move ownership history self-tests into Vitest without changing the production
ownership audit. Remove only duplication entries whose implementations are gone;
retain generic declaration/import syntax coverage against the live TrackRow rule.
Inventory/dependency updates describe removed paths rather than new exemptions.

Review tier: L2. Validate full frontend lint, unit tests and build, plus affected
terminal/style/editor browser flows and both development preview pages. No
backend, persistence or public wire migration is part of this cleanup.

## Rebase onto current main

Retain the upstream overview route and its CSS: conversation-liveness browser
coverage actively renders it. Preserve the new conversation state machine,
projection and read-receipt regression tests. Only the unused legacy card-ID
hash helper and its synthetic golden are removed; router fixtures mint their
own IDs. Apply MCP classification in the new conversation-tool owner while
preserving normalized short names and task_ls normalization from #111.
The removed report tools have no Kernel implementation. Mutation tooling has
no new CI consumers. This rebase requires two fresh independent L2 reviews.
