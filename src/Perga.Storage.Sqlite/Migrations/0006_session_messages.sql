CREATE TABLE session_messages (
    ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
    id TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    source TEXT NOT NULL CHECK (source IN ('session', 'worker')),
    kind TEXT NOT NULL CHECK (kind IN ('input', 'queue', 'steer', 'worker')),
    attempt_id TEXT REFERENCES task_attempts(attempt_id) ON DELETE CASCADE,
    author_id TEXT NOT NULL,
    request_key TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    content TEXT NOT NULL,
    target_turn_id TEXT,
    turn_id TEXT,
    delivered_turn_id TEXT,
    delivery TEXT NOT NULL CHECK (delivery IN ('queued', 'dispatching', 'delivered', 'rejected', 'unknown', 'interrupted', 'withdrawn')),
    created_at_ms INTEGER NOT NULL,
    resolved_at_ms INTEGER,
    CHECK ((source = 'worker') = (attempt_id IS NOT NULL)),
    CHECK ((kind = 'worker') = (source = 'worker')),
    UNIQUE (session_id, source, id),
    UNIQUE (session_id, source, author_id, request_key)
);

CREATE INDEX idx_session_messages_queue ON session_messages(session_id, delivery, ordinal);
CREATE INDEX idx_session_messages_boot ON session_messages(delivery, source, session_id);
CREATE INDEX idx_session_messages_attempt ON session_messages(attempt_id, delivery) WHERE attempt_id IS NOT NULL;
CREATE UNIQUE INDEX idx_session_messages_input_turn ON session_messages(turn_id)
    WHERE source = 'session' AND turn_id IS NOT NULL;

INSERT INTO session_messages (id, session_id, source, kind, author_id, request_key, request_hash, content,
                              turn_id, delivered_turn_id, delivery, created_at_ms, resolved_at_ms)
SELECT id, session_id, 'session', 'input', author_id, request_key, request_hash, content, turn_id,
       CASE WHEN delivery_state = 'delivered' THEN turn_id END,
       CASE WHEN delivery_state = 'accepted' THEN 'queued' ELSE delivery_state END, accepted_at_ms, resolved_at_ms
FROM session_inputs ORDER BY accepted_at_ms, rowid;

INSERT INTO session_messages (id, session_id, source, kind, attempt_id, author_id, request_key, request_hash, content,
                              target_turn_id, turn_id, delivered_turn_id, delivery, created_at_ms)
SELECT id, session_id, 'worker', 'worker', attempt_id, author_id, request_key, request_hash, content,
       target_turn_id, result_turn_id, CASE WHEN delivery = 'delivered' THEN COALESCE(result_turn_id, target_turn_id) END,
       delivery, created_at_ms
FROM worker_messages ORDER BY created_at_ms, rowid;

-- Keep the original FK and legacy rows for older transcript entries and backups.
-- New entries only reference the shared receipt. Copy links without changing item IDs.
ALTER TABLE session_items ADD COLUMN message_ordinal INTEGER REFERENCES session_messages(ordinal) ON DELETE SET NULL;
CREATE UNIQUE INDEX idx_items_message_ordinal ON session_items(message_ordinal) WHERE message_ordinal IS NOT NULL;
UPDATE session_items SET message_ordinal = (
    SELECT ordinal FROM session_messages m WHERE m.session_id = session_items.session_id
    AND m.source = 'session' AND m.id = session_items.accepted_input_id
) WHERE accepted_input_id IS NOT NULL;

-- The new reference must identify a user item in the same session and turn.
CREATE TRIGGER session_items_message_insert BEFORE INSERT ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id)
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;

CREATE TRIGGER session_items_message_update BEFORE UPDATE OF message_ordinal, session_id, turn_id, kind ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id)
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;

-- Steering a waiting message is a separate attempt. A definitive decline lets
-- it remain queued while retaining the original target and result for replay.
CREATE TABLE session_message_steers (
    message_ordinal INTEGER NOT NULL REFERENCES session_messages(ordinal) ON DELETE CASCADE,
    target_turn_id TEXT NOT NULL,
    delivery TEXT NOT NULL CHECK (delivery IN ('dispatching', 'queued', 'delivered', 'rejected', 'unknown', 'interrupted')),
    PRIMARY KEY (message_ordinal, target_turn_id)
);
