# Session context usage

Outcome: preserve provider context occupancy from streaming events through SQLite and session reads to both frontend run projections. This is an L2 persistence and shared contract change.

`AgentSessionDto` is the source of truth for the latest reading: used tokens, nullable window, sample time and model invalidation. A forward migration adds nullable readings and a false invalidation default to `agent_sessions`; historical ratios remain available without inventing historical measurements. Existing session upserts and event transactions persist and broadcast each streamed sample. Final usage uses `EffectiveUsedTokens`. Model changes invalidate old readings, including events from a turn still running on the old model; the next turn on the desired model can validate a new sample. Compaction resets occupancy and its timestamp; replacing an unknown provider thread resets the same fields.

Both frontend readers share a projection. Real zero readings take precedence over the legacy ratio. Percent is unknown when the window is missing, nonpositive or exceeded. Absent used tokens produce no reading; there is no legacy 200k estimate.

Verification: upgrade from v12 with retained rows and rollback/retry injection; SQLite round trips; controlled streaming updates before completion, final effective usage, model changes and compaction; both frontend projections including zero, unknown window and overflow. Run the full .NET solution and frontend lint/build/tests via the declared gate. Formal independent L2 reviews are arranged by the Planner.

Replay onto upstream main at `8a9ecec3` retains all released migrations through `0012_daily_tracks.sql` byte-for-byte and adds context usage as `0013_session_context_usage.sql`. The upgrade regression injects failure at version 13 after seeding version 12, preserving both upstream migration receipts and legacy context ratios.

Upstream native compaction keeps its epoch and thread behavior. Its returned usage now flows through the same measured-occupancy projection, using effective tokens and model invalidation; compaction without usage resets the reading. ACP usage notifications retain upstream compact support while merging windows and broadcasting samples before completion. Focused regressions reproduced the stale compact readings and missing ACP broadcasts before these integration fixes.

This replay has a mechanical delta relative to `223d7de6`: only the forward migration number, schema-version assertions, upgrade-test baseline and this documentation change. Provider, Kernel, API and frontend behavior remain the previously reviewed implementation.
