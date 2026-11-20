# Approval and permission cleanup (#91)

Outcome: provider-advertised ACP choices govern approval responses; an invalid explicit choice cannot consume a pending request. Missing denial choices cancel the ACP request. Session implementations must claim responses explicitly. Durable asks keep their own UI, while sharing only queue presentation with live approvals.

The permission-mode endpoint accepts `permissionMode` and returns the session DTO. No persistence or migration changes are needed. Validate provider responses, policy whitespace handling, API consumers, and the existing approval browser flows with the declared full gate. Review tier: L2 for response claiming and provider authority; the Planner arranges independent reviews.
