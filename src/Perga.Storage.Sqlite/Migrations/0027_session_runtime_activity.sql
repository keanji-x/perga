-- Allow durable runtime output between turns while preserving existing item constraints.
CREATE TABLE session_items_v27 (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL REFERENCES agent_sessions(id) ON DELETE CASCADE,
    turn_id TEXT,
    kind TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    created_at_ms INTEGER NOT NULL,
    accepted_input_id TEXT UNIQUE,
    message_ordinal INTEGER REFERENCES session_messages(ordinal) ON DELETE SET NULL,
    FOREIGN KEY (session_id, turn_id) REFERENCES session_turns(session_id, id) ON DELETE CASCADE,
    FOREIGN KEY (accepted_input_id, session_id, turn_id) REFERENCES session_inputs(id, session_id, turn_id),
    CHECK (accepted_input_id IS NULL OR kind = 'user'),
    CHECK ((kind = 'epoch_boundary' AND turn_id IS NULL) OR (kind != 'epoch_boundary' AND turn_id IS NOT NULL)
        OR (turn_id IS NULL AND kind IN ('assistant_text', 'thinking', 'tool_call', 'tool_result'))
        OR (turn_id IS NULL AND kind IN ('user','system')
            AND COALESCE(json_extract(payload_json,'$.status') = 'interrupted', 0)
            AND COALESCE(json_extract(payload_json,'$.archived') = 1, 0)))
);
INSERT INTO session_items_v27 (id, session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id, message_ordinal)
SELECT id, session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id, message_ordinal
FROM session_items;
-- Retain the historical high watermark, including IDs of deleted items.
INSERT INTO sqlite_sequence(name, seq)
SELECT 'session_items_v27', seq FROM sqlite_sequence
WHERE name = 'session_items'
  AND NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'session_items_v27');
UPDATE sqlite_sequence
SET seq = MAX(seq, COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'session_items'), 0))
WHERE name = 'session_items_v27';
DROP TABLE session_items;
ALTER TABLE session_items_v27 RENAME TO session_items;
CREATE INDEX idx_items_session ON session_items(session_id, id);
CREATE INDEX idx_items_turn ON session_items(turn_id);
CREATE UNIQUE INDEX idx_items_message_ordinal ON session_items(message_ordinal) WHERE message_ordinal IS NOT NULL;
CREATE INDEX idx_session_items_blob_ref ON session_items (json_extract(payload_json, '$.blobRef'))
    WHERE json_type(payload_json, '$.blobRef') = 'text';

CREATE TRIGGER session_items_message_insert BEFORE INSERT ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id
            OR (NEW.turn_id IS NULL AND m.turn_id IS NULL AND m.delivery = 'interrupted'
                AND json_extract(NEW.payload_json,'$.status') = 'interrupted'
                AND json_extract(NEW.payload_json,'$.archived') = 1))
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;

CREATE TRIGGER session_items_message_update BEFORE UPDATE OF message_ordinal, session_id, turn_id, kind ON session_items
WHEN NEW.message_ordinal IS NOT NULL AND (NEW.kind != 'user' OR NOT EXISTS (
    SELECT 1 FROM session_messages m WHERE m.ordinal = NEW.message_ordinal AND m.session_id = NEW.session_id
        AND (m.turn_id = NEW.turn_id OR m.delivered_turn_id = NEW.turn_id
            OR (NEW.turn_id IS NULL AND m.turn_id IS NULL AND m.delivery = 'interrupted'
                AND json_extract(NEW.payload_json,'$.status') = 'interrupted'
                AND json_extract(NEW.payload_json,'$.archived') = 1))
))
BEGIN
    SELECT RAISE(ABORT, 'session item does not match its message');
END;
