# PR 54 replay and recovery delta

Replay the reviewed template work onto main eb7b66d. Keep upstream migrations
byte-frozen and move the task-role addition to migration 0016.

Report approval creation must settle inside the mailbox before cancellation can
leave the caller: await phase one without a cancellation token, while passing
the request token to its work. Existing catch/finally withdrawal then owns every
created Ask, including cancellation during publication.

An unavailable snapshot suspends scheduling without changing pending tasks.
Human HTTP GET and DELETE on a track's template provide inspection and recovery
without an agent identity. DELETE rejects closed tracks and shares the same
atomic detach implementation as MCP clear, then schedules retained tasks under
the plain-track policy. HTTP inspection shares the MCP snapshot projection.

Verify with cancellation during Ask publication, a fresh-host restart with a
seeded pending task and invalid snapshot, HTTP inspection/clear and closed-track
refusal, the full .NET suite, and frontend lint/build. This is an L2 delta;
formal independent reviews are arranged by the Planner.

## Replay onto main 5337fcd (PR 71)

PR 71 scopes approvals and Stop to an execution, separates live approvals from
Wake questions, and retires open Hold asks in migration 0016. Preserve those
contracts and every upstream migration byte. The task-role migration is now
0017; fresh and upgrade assertions include both forward migrations.

This replay has a behavioral adaptation: template report/task admission,
pre-turn admission and scheduling approvals use the authoritative live approval
registry. Report admission captures the caller's turn and MCP origin, creates
all approvals only after validation, and expires siblings on rejection or
cancellation. Stop expires approvals bound to its turn. Pre-turn and background
scheduling admissions are suspended operations with their own execution IDs.
They create no durable Hold asks. Generation and task-contract revalidation
still precede the authorized effect. The template diagram remains byte-identical.

Verify cancellation, sibling denial, Stop with multiple template approvals,
late decisions, routing and turn context; retain upstream approval lifecycle
coverage. Run the full backend gate, frontend lint/build, unit tests and the
affected router/report browser suites. This adaptation requires L2 review;
the Planner arranges independent reviews.
