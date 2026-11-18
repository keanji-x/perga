# Preserve complete model selections after PR #38

Baseline: main `ecd046373aab5e5d4e6fd76861c6fbd0631b9af2`. The local
`f06637e` tree is `13a33e13b0327f1874a48fa82de63b5466e101ad`, matching the
postmerge review evidence. The main commit object is absent locally.

Outcome: ordinary sessions preserve independent model/effort selections and
accepted inputs execute the complete selection bound at acceptance.

- Normalize model and reasoning effort independently on the existing PUT route.
  Null or whitespace clears that field; omitted effort still means null. This
  follows the existing browser request and creation semantics, without a wire change.
- Build an internal ACP selection table alongside the public catalog. Preserve
  exact advertised IDs (including suffix casing). Catalog defaults and outbound
  selection use the same table: bare variant first, otherwise medium, otherwise
  first supported effort in catalog order. Explicit effort selects its advertised
  variant; explicit raw IDs remain usable. Unknown IDs retain existing provider
  error behavior; do not invent a suffix for an advertised group.
- Add migration 0009 with nullable `session_turns.reasoning_effort`. Bind this
  column with provider/model in the existing acceptance transaction and read it
  into the internal durable input. Missing historical values stay null. Do not
  backfill from current sessions or modify migrations 0001–0008.
- Dispatch overrides provider/model/effort together, while permission continues
  to use current session policy. Desired session settings remain B/high after
  executing an earlier A/low input. Worker turns retain their separate negotiated
  settings and the ordinary session endpoint continues to reject Worker updates.

Verification: red/green Host endpoint persistence and controlled launch tests;
strict ACP discovery/open/switch tests for high-only and missing-medium groups;
workspace-gated accepted first input A/low then B/high; recovery from committed
acceptance using the storage port; historical null recovery. Run focused suites,
full .NET gate, frontend API/domain/model control tests and affected Chromium flow.
No real providers, approval changes, manual commits, push or PR creation.

Review tier: L2 (durable snapshots); independent reviews are arranged by Planner.
Operational impact: normal forward schema upgrade with existing migration backup.
