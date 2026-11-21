# Session cancellation and teardown corrections

Worker cancellation reserves settlement in the Track mailbox while retaining the
active attempt and workspace lease. It cancels startup and turn work, then closes
the SessionRuntime outside the mailbox. Only confirmed shutdown permits lease
discard, attempt removal, task cancellation and scheduling. Failed shutdown keeps
the exclusion and permits a later cancellation retry.

Track close, deletion and task contract supersession use the same cancellation
path. Track close reserves scheduling while it waits outside the mailbox. Deletion closes runtimes
without making the registry terminal; only actor disposal permanently disposes it.
Startup cancellation is synchronous outside the runtime snapshot lock, and both
activation checks and scheduler admission reject canceled startup.

Verification covers an unconfirmed canceled writer excluding a second writer,
stop after provider open before activation, and send after failed deletion. The
full .NET suite and OAR bridge gate validate affected consumers. Additional checks
cover unconfirmed compaction, failed idle reset, provider casing and a real
subprocess with blocked stdin. Review tier is L2; formal independent reviews are
coordinated by the Planner. No migration or durable schema change is required.
