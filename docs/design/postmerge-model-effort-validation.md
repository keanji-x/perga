# PR #38 model regression verification

Attempt: `dd4c5b7ab6a549559cf9d64f3c3197c8:fix-postmerge-model-effort`.
Design: [Preserve complete model selections](postmerge-model-effort.md).
Review tier L2; formal independent reviews remain Planner-owned.

## Reproductions before production edits

All commands ran from the repository root using `/home/kenji/.dotnet/dotnet`.
Compilation mistakes in initial test drafts were corrected before recording these
behavioral failures.

- Host project filter:
  `FullyQualifiedName~DefaultModelKeepsIndependentEffort|FullyQualifiedName~AcceptedFirstInputKeepsModelAndEffort|FullyQualifiedName~AcceptedSelectionRecovers`.
  Five failures: Lead and ordinary subagent expected high but received null;
  workspace-gated A/low input received high after update to B/high;
  reopened accepted A/low and A/null inputs received the current high effort.
  Evidence: `/tmp/model-host-red.log`.
- AgentKit project filter: `FullyQualifiedName~AdvertisedGroupDefaultAndEffort`.
  Three failures, four passes. A strict mock rejected unadvertised IDs on open
  for high-only default, low/high without medium default, and uppercase raw suffix
  with explicit high. Evidence: `/tmp/model-acp-red.log`.

## Green checks

- Host focused selection suite: 10 passed (`/tmp/model-host-green.log`).
- AgentKit full project: 72 passed (`/tmp/model-agentkit-final.log`), including
  ordinary Codex `thread/start` and `turn/start` request assertions, strict ACP
  discovery/open/switch, existing Worker configuration and protocol error cases.
- Storage full project: 25 passed (`/tmp/model-storage-green.log`), including
  v8→v9 upgrade, injected migration receipt failure, rollback, retry, preserved
  v1–v8 checksums and historical null effort despite current session high.
- Frontend from `fe`: `npx vitest run --project platform-independent
  core/api/perga-v1.test.ts core/domain/conversation.test.ts core/domain/track.test.ts
  --project web-dom web/src/features/chat/thread/model-pill.test.tsx --maxWorkers=2`:
  287 passed (`/tmp/model-fe-unit.log`). This includes the existing default model
  plus explicit effort request semantics.
- Chromium from `fe`: `npx vitest run --project browser
  web/src/app/router/assistant-provider.browser.test.tsx --maxWorkers=1`:
  15 passed (`/tmp/model-fe-browser.log`).
- `cd fe && npm run build`: passed (`/tmp/model-fe-build.log`), with the existing
  bundle-size advisory.
- `git diff --check`: passed. No released migration is modified; migration 0009
  is the only new migration.

The final tree is also verified by the declared full .NET gate:
`dotnet test Perga.sln -m:1`. Its authoritative output is stored in this attempt's
gate log by the task runner; the worker result reports its actual verdict.

## Scope and evidence boundaries

The live race uses normal Host track creation with first input and a controlled
workspace command, then the model and permission endpoints. Recovery commits
acceptance through the production storage port before shutdown, then starts a
fresh Host on the same temporary database. An additional crash fixture omits the
turn effort to verify that missing historical values remain unspecified. These
are controlled providers and temporary databases, without live model turns.

The wire shape is unchanged. Permission remains current policy, while accepted
provider/model/effort is immutable. Worker updates still require Worker settings;
existing negotiated Worker and permission/reopen checks run in the full .NET gate.
Paused approvals remain outside this change. No historical development database
compatibility, manual commit, push or PR creation is included.

## Final checks and local ownership range

The first full .NET gate passed all 274 tests across eight projects. A subsequent
focused run passed both Lead/subagent cases after adding a second reopen that
confirms explicit effort clearing is durable (`/tmp/model-host-clear-restart.log`).
The final gate is rerun after that test-only addition and this evidence update.

The default `npm run lint` stopped at ownership history auditing because local
`origin/main` includes older commits c163d245 and 80a57304 without required
trailers. Those files are unchanged here. The supported explicit-baseline retry
is `OWNERSHIP_BASE_SHA=f06637ebcaac620fd2e204171f4686061afdd04f npm run lint`
from `fe`; its output is `/tmp/model-fe-lint-baseline.log`, and its actual result
is included in the worker report. This uses the verified baseline tree and does
not suppress ownership checks or change source files.

## PR #41 raw ACP ID collision follow-up

Attempt: `dd4c5b7ab6a549559cf9d64f3c3197c8:fix-pr41-raw-acp-id-collision`.
The starting tree is c8db9b7, identical to reviewed PR head 105981ec77bc66fee7369ed20eb8624f454271d6.
This focused behavior correction remains subject to Planner's independent review
of the PR delta; prior review and CI do not authorize the new commit.

The outcome is that an advertised raw ID keeps its meaning even when another
model group's derived name matches it. The shared ACP catalog builder reserves
advertised IDs before publishing group aliases. A colliding group publishes its
original variants with their own names/descriptions, no grouped effort controls,
and a default marker on the exact current variant. Without a current ID, the
existing first-group default selection rule still applies. Ordinary same-group
bare ID plus effort retains its previous semantics. Catalog discovery and session
selection continue to use the same builder; wire contracts and migrations do not
change. Default model plus explicit effort and paused approvals remain outside
this correction.

Before production edits, the independent review test was copied from
`/tmp/pr41-independent-review/tests/Perga.AgentKit.Tests/IndependentSelectionReviewTests.cs`.
Only its two collision cases were imported; the known unrelated default-effort
failure was excluded. The review fixture's two-line load response addition was
also reused so resumed sessions advertise the same controlled catalog as new
sessions.

- Red: `dotnet test tests/Perga.AgentKit.Tests/Perga.AgentKit.Tests.csproj
  --filter FullyQualifiedName~ExplicitAdvertisedRawIdMustSurviveGroupCollision -m:1`:
  2 failed, 0 passed. Both initial `session/new`/`session/load` bindings and their
  subsequent switches sent `vendor/model-high-low` instead of `vendor/model-high`.
  Evidence: `/tmp/pr41-collision-red.log`.
- Red catalog regression: the same project with filter
  `FullyQualifiedName~CollidingCatalogExposesSelectableRawVariants`: 4 failed,
  0 passed, expecting three selectable rows but receiving two. Evidence:
  `/tmp/pr41-catalog-red.log`.
- Green: the same project with filter
  `FullyQualifiedName~IndependentSelectionReviewTests|FullyQualifiedName~AdvertisedGroupDefaultAndEffort`:
  14 passed, 0 failed. Strict controlled ACP validates every selected wire ID.
  Coverage includes new/load, switching each catalog row with unspecified and
  explicit effort, both publication orders, exact current and absent-current
  defaults, original display metadata, high-only, missing medium, uppercase
  suffixes, and bare ID plus explicit high. Evidence:
  `/tmp/pr41-collision-green.log`.
- Frontend focused API/domain/model-pill suite (same command as above): 287 passed
  across four files, `/tmp/pr41-fe-unit.log`.
- Real Chromium provider selection suite (same command as above): 15 passed,
  `/tmp/pr41-fe-browser.log`.
- `cd fe && npm run build`: passed, with the existing bundle-size advisory;
  `/tmp/pr41-fe-build.log`.

Full .NET validation uses this attempt's declared `dotnet-full` gate:
`dotnet test Perga.sln -m:1`. The runner retains the authoritative gate log and the
worker result reports its verdict. No manual commit, push or PR edit is made.
Frontend lint is also executed with `cd fe && npm run lint`; its output is
`/tmp/pr41-fe-lint.log`. The worker result records the actual verdict and any
explicit ownership-baseline retry separately, rather than treating old PR CI as
validation of this change.
