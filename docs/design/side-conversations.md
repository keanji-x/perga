# Side conversations

The desktop composer opens a side draft with a frozen excerpt of the source conversation. Sending the first question creates an independent session through the existing keyed session creation endpoint. The excerpt travels with the first user message. The question supplies the session title.

The v1 bootstrap advertises `conversationSide` when the Host supports `sourceSessionId` on session creation and reads. The Kernel resolves the source in the current Track. The source identifies the discussion shown in the side pane. Session ownership and permissions continue to come from the authenticated creator.

SQLite migration 0029 adds a nullable source reference. Existing sessions have no recorded source. The existing creation transaction commits the reference, first input and receipt together. The source participates in the request hash when present. Check replay after restart, a source from another Track, and reopening the side pane from its original conversation.
