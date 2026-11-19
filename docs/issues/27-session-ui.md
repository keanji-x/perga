# Issue 27: sessions own conversation identity

The Track aggregate response is decoded as `TrackAggregateSnapshotDto` without a card or wire-detail projection. Its sessions own list membership, provider, state and Lead role; Lead is metadata (`isLead`), not a conversation kind. Report preparation reads the aggregate report. Workbench reads tool resources separately and never receives sessions.

All sessions share composer controls and provider capabilities. Lead metadata is used only for role actions and navigation. Query updates invalidate aggregate data rather than inserting cards into its cache. No legacy response shape is admitted by the aggregate or session-list readers.

Validation: direct aggregate decoding, Lead and non-Lead conversation rendering, role switching, report preparation, terminal lifecycle, provider controls and mobile history; then the task gate (typecheck, lint, unit tests, browser tests). Review tier L1; no persistence migration or operational change.

Sources: https://github.com/keanji-x/perga/issues/27 and its owner comment (read with `gh issue view 27 --comments`).
