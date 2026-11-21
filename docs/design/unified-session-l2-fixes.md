# Unified session L2 corrections

Outcome: session configuration remains authoritative from creation through activation;
unsupported explicit steering never becomes future input; recovery cannot replace an
unconfirmed provider; restart requeue preserves conversation identity; configuration
updates leave the Track mailbox responsive. Review tier: L2.

Resolve task routing defaults before constructing the preallocated session, with
explicit caller selections taking precedence. Activation changes attempt identity
and lifecycle fields only. Configuration updates persist the versioned selection
and reserve idle sessions;
configuration/disposal/reopen runs outside the mailbox, then releases the reservation.
Provider changes invalidate identity immediately and dispose the old handle outside
the mailbox. The next turn opens the selected provider.

Interrupted Workers retain their owned handle and shutdown fence until disposal
confirms shutdown. Recovery already joins tracked disposal before changing Epoch
or clearing the interruption flag. Requeue may release a stale resume hold only
for an orphaned attempt with no live turn, reservation, steering, compaction or
recovery operation. Existing ownership fences still prevent replacement. The new
attempt transaction clears archival, updates identity and writes epoch_boundary.
No schema or migration changes are needed.

Verification: reproduce each defect with focused Kernel tests and a fresh-Host
SQLite restart test, then run affected consumers and the official oar-bridge and
full solution gates. Inspect the full diff and verify migrations 0001–0020 are
unchanged. Formal independent L2 reviews are arranged by the Planner.
