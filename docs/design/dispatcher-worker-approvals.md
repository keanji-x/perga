# Dispatcher decisions for Worker approvals

A live provider approval for a dispatched Worker may wake the session that
dispatched its task. Resolve that session from the stored dispatch receipt.
The installation owner grants delegation through
`Perga:DispatcherWorkerApprovalOptions`, a map from exact provider-reported
tool names to the exact approval option IDs that the dispatcher may select.
The IDs are the choices already exposed by the common approval contract.
For providers with only a boolean decision, `approve` and `deny` are the
same single-request choices used by the existing human path.
Choose only the option IDs whose provider behavior you intend to delegate.
With an empty map existing human approval behavior applies. The dispatcher
must still be the Track Lead. Perga policy approvals that require a human
are outside this delegation.

Use a system notification in the existing wake queue, carrying only the live
approval ID. At delivery require a pending approval assigned to the current
Lead session, both while preparing the notification and when admitting the
turn in the actor. This also retires wakes assigned to a former Lead. Capture
tool arguments in memory and include them as request data in the notification. Approval state remains
live and expires with the originating execution. A wake replayed after
resolution, interruption or restart does not start a dispatcher turn.

The authenticated MCP decision and the existing HTTP decision use the same
decision and provider-response delivery path. The MCP path additionally checks
the dispatch author and the allowed option against live state before any audit
or reply. Existing HTTP access controls continue to apply to the human path.
Test successful dispatch, visibility to the right session, forbidden
decisions, no duplicate provider replies, and scope of the human decision path.
