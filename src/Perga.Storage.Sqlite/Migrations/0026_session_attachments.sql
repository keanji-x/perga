CREATE TABLE session_attachments (
    id                  TEXT PRIMARY KEY,
    track_id            TEXT REFERENCES tracks(id) ON DELETE CASCADE,
    session_id          TEXT REFERENCES agent_sessions(id) ON DELETE CASCADE,
    status              TEXT NOT NULL CHECK (status IN ('staged', 'bound')),
    content_type        TEXT NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')),
    size_bytes          INTEGER NOT NULL CHECK (size_bytes > 0),
    sha256              TEXT NOT NULL,
    original_filename   TEXT,
    storage_rel_path    TEXT NOT NULL,
    created_at_ms       INTEGER NOT NULL,
    bound_at_ms         INTEGER,
    CHECK (
        (status = 'staged' AND bound_at_ms IS NULL)
        OR (status = 'bound' AND track_id IS NOT NULL AND session_id IS NOT NULL AND bound_at_ms IS NOT NULL)
    ),
    CHECK (session_id IS NULL OR track_id IS NOT NULL)
);

CREATE INDEX idx_session_attachments_session_status
    ON session_attachments(session_id, status, created_at_ms)
    WHERE session_id IS NOT NULL;

CREATE INDEX idx_session_attachments_staged_gc
    ON session_attachments(status, created_at_ms)
    WHERE status = 'staged';

CREATE INDEX idx_session_attachments_track
    ON session_attachments(track_id)
    WHERE track_id IS NOT NULL;

CREATE TABLE session_message_attachments (
    message_ordinal     INTEGER NOT NULL REFERENCES session_messages(ordinal) ON DELETE CASCADE,
    position            INTEGER NOT NULL CHECK (position >= 0 AND position < 8),
    attachment_id       TEXT NOT NULL REFERENCES session_attachments(id) ON DELETE CASCADE,
    PRIMARY KEY (message_ordinal, position),
    UNIQUE (message_ordinal, attachment_id)
);

CREATE INDEX idx_session_message_attachments_attachment
    ON session_message_attachments(attachment_id);

CREATE TRIGGER session_message_attachments_same_session
BEFORE INSERT ON session_message_attachments
WHEN NOT EXISTS (
    SELECT 1
    FROM session_messages m
    JOIN session_attachments a ON a.id = NEW.attachment_id
    WHERE m.ordinal = NEW.message_ordinal
      AND a.session_id = m.session_id
      AND a.status = 'bound'
)
BEGIN
    SELECT RAISE(ABORT, 'attachment does not belong to message session or is not bound');
END;
