# PR #38 reconciliation validation

This candidate combines main `fdc34189148e91f7ea428e07240e2085cb507652`
(provider-declared permission modes) with PR #38 model/reasoning selection,
implicit templates and existing queue/recovery and browser fixture behavior.
Review tier: L2; independent reviews and publication are arranged by Planner.

Published main migrations 0001–0007 are unchanged. The reasoning column is added
only by sequential 0008. The migrator uses main's integrity checks, per-script
transactions and online backups. No database or user data was removed.

The canceled checkpoint contained unresolved conflict markers; the initial
focused .NET invocation failed compilation with CS8300. Earlier integration/CI
results apply to historical trees and do not validate this candidate.

Focused checks before the full gate: Storage 23/23; Host model endpoint 5/5,
including model/effort plus permission persistence across restart; Kernel joint
active-turn permission/model update 1/1. Frontend build passed with the existing
bundle-size warning. Published main 0001–0007 were compared byte-for-byte against
`fdc3418`; the migrator is identical to main. `git diff --check` passed.

The earlier `/tmp/perga-dd4c-browser.NpCtne` baseline fails both image cases
before the attachment assertion (run request missing); the historical candidate
reaches the button and fails only `images=false` by expecting it enabled.
The current candidate reproduced that exact latter failure (1 failed, 1 passed).
Production combines run attachment support with the provider's declared image
capability. The browser assertion now expects disabled for `images=false` and
enabled for `images=true`, and retains the no-upload assertion. Production image
handling was not relaxed.

The authoritative final full-suite and Chromium outcomes are the current task's
gate logs and completion result; historical CI run 44337471497 is not evidence
for this tree. No unrelated broad browser repairs are included.

The standalone Assistant Chromium run then passed 14/15, including both image
cases. Its remaining assertion expected a disabled Approvals button despite no
permission options declared in the fixture. It now checks that the button is
absent per #35 and still verifies no permission mutation request is sent.

## Approval fixture follow-up (blocked on published Ask semantics)

Attempt `dd4c5b7ab6a549559cf9d64f3c3197c8:fix-pr38-approvals-browser-fixture`
starts from candidate `19ccd58644dfdee4dc9f18c5a355c5ba19f1cf96`.
Its prior full gate is
`runs/dd4c5b7ab6a549559cf9d64f3c3197c8:reconcile-pr38-no-dev-db-compat/gates/1.log`:
all eight .NET projects passed (257 tests); frontend build passed; frontend unit
checks passed (377 files, 6,107 tests, one skipped); Chromium had four files
passing and 19 failures in the approval file. Those results remain applicable
to production because this follow-up changes only the approval browser fixture
and this document. No production fallback, migration or database change is made.

The smallest original router case was reproduced with:

```sh
cd fe
npx vitest run --project browser web/src/app/router/planner-approvals.browser.test.tsx -t 'shows no approval setting'
```

It failed before opening the conversation on both the candidate and a fresh
`git archive fdc34189148e91f7ea428e07240e2085cb507652` baseline
(`/tmp/perga-pr38-main.yExIEO`). The baseline reused installed dependencies;
it did not change production sources. Logs: `/tmp/pr38-approvals-before.log`
and `/tmp/pr38-approvals-main-before.log`. An initial mistyped filter matched
zero tests and supplies no regression evidence.

The fixture now serves camelCase Bootstrap, TrackAggregateSnapshot,
AgentSession, SessionItemsPage and Ask DTOs on the actual `/api/v1` read/write
routes. The permission response is an AgentSession; the answer response is an
answered Ask. The exact answer assertion includes both option index and label,
as the production operation sends. Existing visual, mode, error and approval
assertions remain intact. The original transcript text remains in a SessionItem.

The four permission persistence cases passed in Chromium after fixture repair.
`npx tsc -b --pretty false` passed. The first complete approval run with DTOs
reported 14 passed and six failed (`/tmp/pr38-approvals-dto.log`): four paused
approval cases, the disappeared-request message, and hold priority. All now reach
the loaded Track; they fail on approval semantics rather than Track loading.
One intermediate run hit a dynamic module import error; a clean retry passed
all four selected mode cases. Final affected-file evidence is the gate for this
attempt, including the transcript-preserving final fixture.

The remaining problem predates this reconciliation: published `AskDto` in
`src/Perga.Contracts/Contracts.cs` has no hold/wake discriminator, and
`askDtoToActivityItem` in `fe/core/domain/track.ts` unconditionally sets
`delivery: 'wake'` on main as well as the candidate. That loses the paused
approval notice, options-only semantics and hold-specific 409 handling.
Worker activity has a separate scoped `openAsks` path mapped to hold; a Planner
Track snapshot does not expose that identity. Treating every Track ask as hold,
inferring approval from question text, injecting legacy overlays, or removing
these assertions would mask the missing contract. A new scoped task must decide
how to represent paused Planner approvals before this suite can honestly pass.
This attempt does not claim acceptance or merge readiness. Independent L2 review
and any publication remain Planner work.

Final gate run 1 (`affected-browser`) finished red: four files passed; the
approval file had 14 passed and six failed, for 34 passed / six failed overall.
Evidence:
`runs/dd4c5b7ab6a549559cf9d64f3c3197c8:fix-pr38-approvals-browser-fixture/gates/1.log`.
The final fixture preserved the transcript text, and the same six semantic
failures remained. No assertions were removed or relaxed. `git diff --check`
passed after the documentation update.

For an independent main reproduction beyond the original stale-route failure,
the repaired test file alone was copied into the archived main baseline, leaving
all main production files unchanged. Running the same browser command with
`-t 'shows a paused turn'` failed (one failed, 19 skipped) at the missing
`Turn paused, waiting for your approval` assertion. Evidence:
`/tmp/pr38-approvals-main-dto.log`. This confirms the hold-contract defect on
main independently of PR #38. No other old browser fixtures were repaired.
