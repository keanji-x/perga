# Perga development guidelines

These rules apply throughout the repository. Follow the nearest `AGENTS.md` as
well. Frontend changes follow [the frontend rules](fe/AGENTS.md) and the
rules in their layer directory. [DESIGN.md](DESIGN.md) describes the system.
Check its claims against the current implementation when they differ.

## Workflow

1. Inspect the working tree, production call path, existing contracts, and
   tests. Preserve unrelated edits. Search for the behavior in its owning
   layer before adding a new implementation.
2. Define one outcome and how to verify it. Write a short design or issue
   first for migrations, changes to authority, persistence or isolation,
   and large changes spanning multiple components.
3. For a bug, reproduce it through the smallest production entry point that
   exposes it. Confirm the regression check fails for the expected reason
   before fixing the cause. Report an unconfirmed reproduction accurately.
4. Keep changes focused. Review the full diff and fix blockers with evidence.
   Re-run checks affected by subsequent changes, including a rebase or
   regenerated artifact. Report the checks that actually ran.

Use one writer per checkout. Use separate checkouts or worktrees for parallel
implementation. Preserve other people's edits and avoid destructive cleanup.
Write English PR titles and descriptions. Include the outcome, review tier,
validation, and any migration or operational impact.

## Review depth

| Tier | Scope | Review |
|---|---|---|
| L0 | Mechanical edits such as copy and pure renames | Inspect the diff and run the applicable checks. |
| L1 | Normal implementation changes | One independent review of the behavior, owning layer, callers, and tests. Recheck any fix and its delta. |
| L2 | Changes to authority, persistence, isolation, security, schema, or large changes spanning components | Two independent reviews. Check data integrity, partial failure, retry and recovery, along with callers and tests. Re-review behavior-changing fixes and re-run affected checks. |

For an already reviewed change that was rebased, compare its patch against both
bases. A mechanical delta needs fresh affected checks. A behavioral delta also
needs review at the original tier. Block on regressions introduced by the change
or defects in its approach. Track pre-existing defects separately when useful.

## Architecture and contracts

- Keep policy in its owning layer. The Host adapts HTTP, WebSocket and MCP.
  The Kernel owns orchestration, lifecycle and its domain rules. SQLite
  implements storage through Kernel ports. Provider, terminal and VCS adapters
  own their external protocols. Keep dependencies acyclic. Frontend layers
  consume published contracts.
- Reuse one authoritative implementation of a rule. Keep provider, template
  and plugin specifics out of generic components. Use declared contracts or
  runtime-reported identifiers for external tools. Avoid depending on private
  layouts and naming rules.
- Represent required values with required types. Use typed configuration for new
  settings and follow existing binding conventions. At credential or isolation
  boundaries, build subprocess environments from explicit allowlists.
- Treat released migrations as byte-frozen. Make corrections with a forward
  migration. Preserve data written by supported Perga releases. A Neige import
  or an old wire compatibility layer requires explicit scope. Do not infer
  missing historical facts or silently introduce fallbacks.
- Review write order, transaction scope, retries and restart behavior when
  touching durable data or provider dispatch. Make the source of truth explicit
  and test failures between local writes and external effects.
- When changing a contract, check producers, consumers, fixtures and
  published schemas. Run real generators where available.
  The frontend's `gen:api` currently only prints a notice. For a change to
  `/api/v1`, update the relevant frontend schema and
  `fe/core/api/generated/openapi.json` directly and verify against the Host.
- Keep files focused. Treat 800 handwritten lines as a prompt to split by
  responsibility when practical. Avoid unrelated file splits to satisfy a
  number. Add durable checks only for invariants that future changes must
  preserve, and prefer the simplest rule that enforces each invariant.

## Tests and verification

Test observable results such as stable error codes, persisted state, replayed
identity, or user-visible outcomes. Assert call counts and order when they are
part of the contract, such as preventing duplicate dispatch. Use a temporary
database and controlled providers for routine integration tests. For restart
behavior, reopen the same database in a fresh host. For visible frontend
changes, exercise the affected flow in a real browser.

Run the applicable checks on the final tree from the repository root. Select
focused cases while iterating and include affected consumers and integration
tests before delivery.

```sh
# .NET 10. For a focused run, use a test project and --filter.
dotnet test Perga.sln

# Install when dependencies are missing or the lockfile has changed.
(cd fe && npm ci)
(cd fe && npm run lint && npm run build)
(cd fe && npm test)
(cd fe && npm run test:browser)    # affected browser behavior

# Rust terminal bridge changes. Run from its directory to use the repo pin.
(cd tools/perga-term-bridge && cargo fmt --check && cargo test --locked)
```

Run the full .NET suite for cross-cutting backend changes, migrations or
shared contracts. For narrower changes use affected test projects, including
Host integration tests for API or persistence behavior. For a Rust plugin
change, use that plugin's own manifest and checks. For a docs-only change,
check paths, commands and the diff for whitespace errors.

Use targeted mutation verification when a small set of tests is all that
protects an important invariant. In an isolated checkout make one deliberate
production change, predict which tests will fail, run the affected suite and
check its actual failures. Restore the original code and confirm it passes.
Inspect the final diff for residue. Keep real provider runs off shared hosts
and use a dedicated environment if they are necessary.
